#!/usr/bin/env python3
"""Plan PR validation from complete input changes; unknown inputs retain all checks.

Input classes and job responsibilities live here. Scenario/source definitions
stay in their existing files. Unproven inputs retain full validation.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import posixpath
import re
import subprocess
import sys
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from xml.etree import ElementTree

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('coverage', ROOT / 'scripts/check-template-scenario-coverage.py')
coverage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coverage)


def git(*arguments: str) -> str:
    return subprocess.check_output(['git', '-C', str(ROOT), *arguments], text=True, encoding='utf-8', stderr=subprocess.PIPE).rstrip('\n')


def frontend_source(path: str) -> bool:
    return bool(re.fullmatch(r'template/(?:frontend|\.template\.config/localization/frontend)/(?:src|public|_mock)/.+\.(?:ts|html|css|scss|json|svg|png|jpg|jpeg|webp|ico)', path))


def backend_source(path: str) -> bool:
    return bool(re.fullmatch(r'template/backend/(?:src|tests)/.+\.cs', path))


def project_owner(path: str) -> str | None:
    # Only source files inside one real package project can seed consumer selection.
    # A missing/deleted project, project configuration or shared input means full.
    if not re.fullmatch(r'framework/(?:components/[^/]+|ddd-struct)/Leistd\.[^/]+/.+\.cs', path):
        return None
    parent = ROOT / path
    while parent != ROOT:
        projects = list(parent.glob('Leistd.*.csproj')) if parent.is_dir() else []
        if projects:
            return projects[0].stem if len(projects) == 1 else None
        parent = parent.parent
    return None


# Inputs of every framework project build: a change here can affect any test.
FRAMEWORK_SHARED_INPUT = re.compile(
    r'framework/[^/]+\.props|framework/build/.+|framework/tests/Directory\.Build\.props|framework/tests/shared/.+|'
    r'Directory\.Build\.(?:props|targets)|Directory\.Packages\.props|NuGet\.config|global\.json|VERSION')
# The only MSBuild files the graph models; any other props/targets is an unmodelled import.
FRAMEWORK_BUILD_FILES = {'framework/Directory.Build.props', 'framework/Directory.Packages.props',
                         'framework/common.props', 'framework/tests/Directory.Build.props', 'Directory.Build.targets'}
# Items that only name references; any other item reaching outside its project is a cross-project input.
REFERENCE_ITEMS = {'PackageReference', 'FrameworkReference', 'Using', 'InternalsVisibleTo'}


class FrameworkProofUnavailable(ValueError):
    """The candidate cannot be proven to touch only modelled framework projects."""


def tree_files(sha: str) -> list[str]:
    return git('-c', 'core.quotePath=false', 'ls-tree', '-r', '--name-only', '-z', sha).split('\0')[:-1]


def read_blobs(sha: str, paths: list[str]) -> dict[str, str]:
    """Read many files of one commit through a single git process."""
    request = ''.join(f'{sha}:{path}\n' for path in paths).encode('utf-8')
    data = subprocess.run(['git', '-C', str(ROOT), 'cat-file', '--batch'], input=request,
                          capture_output=True, check=True).stdout
    blobs, offset = {}, 0
    for path in paths:
        header_end = data.index(b'\n', offset)
        header = data[offset:header_end].split()
        if len(header) != 3 or header[1] != b'blob':
            raise FrameworkProofUnavailable(f'cannot read {path} at {sha}')
        size = int(header[2])
        blobs[path] = data[header_end + 1:header_end + 1 + size].decode('utf-8')
        offset = header_end + 2 + size
    return blobs


JOB_NAMES = ('frontend-gates', 'framework-pack', 'package-consumption', 'template-generation',
             'template-slices', 'test', 'postgresql-e2e', 'oidc-e2e')
INTERNAL_DOCUMENT = re.compile(r'(?:README\.md|(?:docs|\.agents|skills)/.+\.md|framework/README\.md)')
CONTAINER_INPUT = re.compile(r'template/(?:Dockerfile|\.dockerignore|deploy/.+|\.template\.config/template\.json|'
                             r'(?:frontend|\.template\.config/localization/frontend)/(?:package(?:-lock)?\.json|angular\.json))')


def changed_paths(base: str, head: str) -> list[str]:
    if not re.fullmatch(r'[0-9a-f]{40}', base) or base == '0' * 40 or base == head:
        raise ValueError('uncertain or empty baseline')
    git('cat-file', '-e', f'{base}^{{commit}}')
    git('merge-base', '--is-ancestor', base, head)
    paths = git('-c', 'core.quotePath=false', 'diff', '--no-renames', '--name-only', '-z', base, head).split('\0')[:-1]
    if not paths:
        raise ValueError('empty input difference')
    return paths


def verified_quality_base(base: str, head: str) -> dict:
    """A push may narrow only after its preceding candidate passed this workflow."""
    unavailable = dict(Verified=False, RunId=0, Attempt=0)
    try:
        git('merge-base', '--is-ancestor', base, head)
        repository = os.environ.get('GITHUB_REPOSITORY', '')
        branch = os.environ.get('GITHUB_REF_NAME', '')
        token = os.environ.get('GH_TOKEN', '')
        if not re.fullmatch(r'[\w.-]+/[\w.-]+', repository) or branch not in ('main', 'develop') or not token:
            return unavailable
        api = os.environ.get('GITHUB_API_URL', 'https://api.github.com').rstrip('/')
        def get(endpoint):
            request = Request(api + '/repos/' + repository + '/actions/' + endpoint,
                              headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                                       'X-GitHub-Api-Version': '2022-11-28'})
            with urlopen(request, timeout=15) as response:
                return json.load(response)
        query = urlencode(dict(head_sha=base, branch=branch, event='push', per_page=100))
        payload = get('workflows/release.yml/runs?' + query)
        runs = payload['workflow_runs']
        if payload['total_count'] != len(runs) or not runs:
            return unavailable
        run = max(runs, key=lambda entry: entry['id'])
        if (run['head_sha'] != base or run['head_branch'] != branch or run['event'] != 'push' or
                run['path'] != '.github/workflows/release.yml' or run['status'] != 'completed' or run['conclusion'] != 'success'):
            return unavailable
        jobs = get(f"runs/{run['id']}/attempts/{run['run_attempt']}/jobs?per_page=100")
        if jobs['total_count'] != len(jobs['jobs']):
            return unavailable
        aggregates = [job for job in jobs['jobs'] if job['name'] == 'quality / 模板全场景矩阵']
        if len(aggregates) != 1 or aggregates[0]['status'] != 'completed' or aggregates[0]['conclusion'] != 'success':
            return unavailable
        return dict(Verified=True, RunId=run['id'], Attempt=run['run_attempt'])
    except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError):
        return unavailable


def packaged_documents(sha: str) -> set[str]:
    """Model the current Pack declarations; unknown declarations disable narrowing."""
    graph = framework_graph(sha)
    files = tree_files(sha)
    build_files = sorted(FRAMEWORK_BUILD_FILES & set(files))
    blobs = read_blobs(sha, list(graph) + build_files)
    expected = {
        '$(MSBuildThisFileDirectory)NuGet.md': {'Pack': 'true', 'PackagePath': '\\', 'Visible': 'false'},
        '$(_LeistdComponentDocPath)': {'Pack': 'true', 'PackagePath': 'docs\\', 'Visible': 'false',
                                    'Condition': "Exists('$(_LeistdComponentDocPath)')"},
        '$(_LeistdDddDocPath)': {'Pack': 'true', 'PackagePath': 'docs\\', 'Visible': 'false',
                              'Condition': "'$(_LeistdParentDir)' == 'ddd-struct' And Exists('$(_LeistdDddDocPath)')"},
    }
    properties = {
        'PackageReadmeFile': 'NuGet.md',
        '_LeistdParentDir': '$([System.IO.Path]::GetFileName($([System.IO.Path]::GetDirectoryName($(MSBuildProjectDirectory)))))',
        '_LeistdComponentDocPath': '$(MSBuildThisFileDirectory)docs\\components\\$(_LeistdParentDir).md',
        '_LeistdDddDocPath': '$(MSBuildThisFileDirectory)docs\\ddd-struct\\ddd-struct.md',
    }
    seen, values = set(), {}
    for path, text in blobs.items():
        for item in ElementTree.fromstring(text).iter():
            if path == 'framework/common.props':
                if item.tag == 'Import':
                    raise FrameworkProofUnavailable('unknown package import')
                if item.tag in properties:
                    if item.attrib or item.tag in values:
                        raise FrameworkProofUnavailable('unknown package property')
                    values[item.tag] = (item.text or '').strip()
                if 'Pack' in item.attrib or item.tag == 'Pack':
                    include = item.attrib.get('Include')
                    attrs = {key: value for key, value in item.attrib.items() if key != 'Include'}
                    if item.tag != 'None' or include not in expected or attrs != expected[include] or include in seen:
                        raise FrameworkProofUnavailable('unknown Pack declaration')
                    seen.add(include)
            elif (path == 'framework/tests/Directory.Build.props' and item.tag == '_LeistdIsTestProject' and
                  (item.text or '').strip() == 'true' and item.attrib == {'Condition': "$(MSBuildProjectName.EndsWith('.Tests'))"}):
                continue
            elif ('Pack' in item.attrib or item.tag == 'Pack' or item.tag.startswith('_Leistd') or
                  item.tag == 'PackageReadmeFile' and not (path == 'framework/tests/Directory.Build.props' and not item.text)):
                raise FrameworkProofUnavailable(f'unknown package input in {path}')
    if seen != set(expected) or values != properties:
        raise FrameworkProofUnavailable('package document mapping changed')
    tests = ElementTree.fromstring(blobs['framework/tests/Directory.Build.props'])
    excluded = [item for group in tests.findall('PropertyGroup') if not group.attrib
                for item in group.findall('IsPackable') if not item.attrib and (item.text or '').strip() == 'false']
    if len(excluded) != 1:
        raise FrameworkProofUnavailable('test package exclusion changed')
    result = {'framework/NuGet.md'}
    for project in graph:
        root = ElementTree.fromstring(blobs[project])
        packable = [item for item in root.iter('IsPackable')]
        if any(item.attrib or (item.text or '').strip().lower() not in ('true', 'false') for item in packable):
            raise FrameworkProofUnavailable(f'unknown IsPackable in {project}')
        if project.startswith('framework/tests/'):
            if any((item.text or '').strip().lower() == 'true' for item in packable):
                raise FrameworkProofUnavailable('test package override')
            continue
        if packable and packable[-1].text.strip().lower() == 'false':
            continue
        parent = posixpath.basename(posixpath.dirname(posixpath.dirname(project)))
        document = f'framework/docs/components/{parent}.md'
        if document in files:
            result.add(document)
        if parent == 'ddd-struct':
            result.add('framework/docs/ddd-struct/ddd-struct.md')
    return result


def classify_inputs(base: str, head: str, paths: list[str]) -> dict[str, list[str]]:
    packaged = packaged_documents(base) | packaged_documents(head)
    result = {name: [] for name in ('internal', 'generated-doc', 'package-doc', 'frontend', 'backend', 'framework', 'unknown')}
    for path in paths:
        if path in packaged:
            kind = 'package-doc'
        elif (INTERNAL_DOCUMENT.fullmatch(path) or path.startswith('framework/docs/') and path.endswith('.md')):
            kind = 'internal'
        elif path.startswith('template/') and path.endswith('.md'):
            kind = 'generated-doc'
        elif frontend_source(path):
            kind = 'frontend'
        elif backend_source(path):
            kind = 'backend'
        elif path.startswith('framework/') and path.endswith('.cs'):
            kind = 'framework'
        else:
            kind = 'unknown'
        result[kind].append(path)
    return result


def release_tag(channel: str, head: str, selected: str = '') -> tuple[str, str]:
    pattern = r'v\d+\.\d+\.\d+' + (r'(?:-beta\.\d+)?' if channel == 'beta' else '')
    names = [name for name in git('tag', '--merged', head).splitlines() if re.fullmatch(pattern, name)]
    if not names:
        if selected:
            raise ValueError('release baseline is not a published channel ancestor')
        return '', ''
    nearest = min(names, key=lambda name: (int(git('rev-list', '--count', f'{name}..{head}')),
                                          '-beta.' in name, name))
    if selected and selected != nearest:
        raise ValueError('release baseline differs from nearest published channel ancestor')
    return nearest, git('rev-parse', f'{nearest}^{{commit}}')


def needs_release(base: str, head: str) -> tuple[bool, str]:
    if not base:
        return True, 'No published channel baseline'
    if base == head:
        return False, 'Candidate already has a published channel tag'
    try:
        paths = changed_paths(base, head)
        classes = classify_inputs(base, head, paths)
        # Test projects do not produce Framework packages. Unknown production
        # and build inputs remain publication responsibilities.
        hits = classes['package-doc'] + [path for path in paths if path.startswith('framework/')
                and not path.startswith('framework/tests/') and not path.endswith('.md')]
        harmless = set(sum((classes[name] for name in ('internal', 'generated-doc', 'frontend', 'backend')), []))
        unknown = [path for path in paths if path not in harmless and not path.startswith(('template/', 'framework/tests/'))]
        return bool(hits or unknown), 'Changed package or unmodelled inputs: ' + ', '.join(sorted(set(hits + unknown))) if hits or unknown else 'Only non-package inputs'
    except (ValueError, OSError, subprocess.CalledProcessError, ElementTree.ParseError) as error:
        return True, f'Publication input proof unavailable: {error}'


def framework_test_project(path: str) -> bool:
    return path.startswith('framework/tests/') and path.endswith('.Tests.csproj')


def all_framework_tests(sha: str) -> list[str]:
    return sorted(path for path in tree_files(sha) if framework_test_project(path))


def framework_graph(sha: str) -> dict[str, tuple[str, ...]]:
    """ProjectReference graph of one commit; anything the graph cannot model raises."""
    files = tree_files(sha)
    projects = sorted(path for path in files if path.startswith('framework/') and path.endswith('.csproj'))
    unknown = sorted(path for path in files if path.endswith(('.props', '.targets')) and path not in FRAMEWORK_BUILD_FILES
                     and (path.startswith('framework/') or '/' not in path))
    if unknown:
        raise FrameworkProofUnavailable(f'unmodelled MSBuild import {unknown[0]}')
    directories = [posixpath.dirname(path) for path in projects]
    if len(set(directories)) != len(directories) or any(
            other.startswith(directory + '/') for directory in directories for other in directories):
        raise FrameworkProofUnavailable('nested or shared project directories')
    blobs = read_blobs(sha, projects + sorted(FRAMEWORK_BUILD_FILES & set(files)))
    for path in FRAMEWORK_BUILD_FILES & set(files):
        if re.search(r'<Compile\b[^>]*\bInclude\s*=|<AdditionalFiles\b|<EnableDefault(?:Compile)?Items>\s*false', blobs[path]):
            raise FrameworkProofUnavailable(f'explicit compile input in {path}')
    known = set(projects)
    graph = {}
    for project in projects:
        directory = posixpath.dirname(project)
        try:
            root = ElementTree.fromstring(blobs[project])
        except ElementTree.ParseError as error:
            raise FrameworkProofUnavailable(f'unparseable {project}') from error
        references = set()
        for element in root.iter():
            if element.tag.startswith('{'):
                raise FrameworkProofUnavailable(f'namespaced MSBuild element in {project}')
            values = [element.text or '', *element.attrib.values()]
            if element.tag == 'Import' or any(marker in value for value in values for marker in ('$(', '@(', '%(')):
                raise FrameworkProofUnavailable(f'unresolvable property or Import in {project}')
            if element.tag in ('Compile', 'AdditionalFiles') and ('Include' in element.attrib or 'Update' in element.attrib) \
                    or element.tag in ('EnableDefaultItems', 'EnableDefaultCompileItems', 'Link') or 'Link' in element.attrib:
                raise FrameworkProofUnavailable(f'unmodelled compile input in {project}')
            include = element.attrib.get('Include')
            if element.tag == 'ProjectReference':
                if not include or any(marker in include for marker in '*?;'):
                    raise FrameworkProofUnavailable(f'unresolvable ProjectReference in {project}')
                target = posixpath.normpath(posixpath.join(directory, include.replace('\\', '/')))
                if target not in known:
                    raise FrameworkProofUnavailable(f'{project} references missing project {target}')
                references.add(target)
            elif include and element.tag not in REFERENCE_ITEMS:
                target = posixpath.normpath(posixpath.join(directory, include.replace('\\', '/')))
                if include.startswith(('/', '\\')) or not target.startswith(directory + '/'):
                    raise FrameworkProofUnavailable(f'cross-project input in {project}')
        graph[project] = tuple(sorted(references))
    return graph


def select_framework_tests(base: str, head: str, paths: list[str]) -> tuple[list[str], str]:
    """Changed files -> owning projects -> reverse dependency closure -> test projects.

    Only edits inside unchanged projects narrow; the empty result is not trusted.
    """
    for path in paths:
        if FRAMEWORK_SHARED_INPUT.fullmatch(path):
            raise FrameworkProofUnavailable(f'shared build or test input {path}')
    graph = framework_graph(head)
    if framework_graph(base) != graph:
        raise FrameworkProofUnavailable('project or ProjectReference added, removed or renamed')
    owners = {posixpath.dirname(project): project for project in graph}
    seeds = set()
    for path in paths:
        parent = posixpath.dirname(path)
        while parent and parent not in owners:
            parent = posixpath.dirname(parent)
        if not parent:
            raise FrameworkProofUnavailable(f'{path} belongs to no framework project')
        owner = owners[parent]
        if owner.startswith('framework/tests/') and not framework_test_project(owner):
            raise FrameworkProofUnavailable(f'shared test base {owner}')
        seeds.add(owner)
    dependents = {project: set() for project in graph}
    for project, references in graph.items():
        for reference in references:
            dependents[reference].add(project)
    affected, pending = set(seeds), list(seeds)
    while pending:
        for dependent in dependents[pending.pop()] - affected:
            affected.add(dependent)
            pending.append(dependent)
    selected = sorted(project for project in affected if framework_test_project(project))
    if not selected:
        raise FrameworkProofUnavailable('no test project depends on the changed projects')
    return selected, 'Affected projects: ' + ', '.join(sorted(posixpath.basename(seed)[:-7] for seed in seeds))


def source_producers(config: dict, path: str, scenarios: dict) -> set[str]:
    """File-level producers, conservatively including every conditional line.

    Reuse the existing template engine semantics; no feature-directory map.
    An excluded file still gets the default/all-feature sentinels below.
    """
    # Narrow only the source semantics this proof models. Future engine rules
    # must be reviewed before they can reduce validation responsibility.
    for source in config.get('sources', []):
        if not isinstance(source, dict) or set(source) - {'source', 'target', 'condition', 'exclude', 'modifiers'}:
            raise ValueError('Unmodelled template source rule')
        for modifier in source.get('modifiers', []):
            if not isinstance(modifier, dict) or set(modifier) - {'condition', 'exclude'}:
                raise ValueError('Unmodelled template modifier rule')
    producers: set[str] = set()
    sources = list(coverage.iter_sources(config, [path]))
    if not sources:
        raise ValueError(f'No known template source for {path}')
    for relative, _, condition, modifiers in sources:
        excludes = [item['condition'] for item in modifiers
                    if any(coverage.glob_match(relative, pattern) for pattern in item.get('exclude', []))]
        for name, info in scenarios.items():
            values = coverage.symbol_values(config, coverage.parse_cli_arguments(config, info['Arguments']))
            if (not condition or coverage.evaluate(condition, values)) and not any(coverage.evaluate(expr, values) for expr in excludes):
                producers.add(name)
    return producers


def execution_slices(scenarios: dict, selected: list[str], tier: str, mode: str, container_smoke: bool) -> list[dict]:
    """Bind the registered logical allocation to the candidate's selected work."""
    if not selected:
        return []
    sample = next(iter(scenarios.values()))
    selected_names = set(selected)
    containers = set(sample['Containers']) if container_smoke else set()
    bins = []
    for logical in sample['Titles'][tier]:
        members = [name for name, info in scenarios.items()
                   if name in selected_names and info['Slices'].get(tier) == logical]
        if members:
            bins.append(dict(key=f'execution-{len(bins)+1:02d}', title='', Scenarios=members,
                             Containers=[name for name in members if name in containers]))
    for group in bins:
        preview = '、'.join(sorted(group['Scenarios'], key=lambda name: (len(name), name))[:2])
        stages = {'full':'完整阶段','frontend':'前端阶段','backend':'后端阶段'}[mode]
        container = '；含容器' if group['Containers'] else ''
        group['title'] = f"{stages} · {preview}（共{len(group['Scenarios'])}场景{container}）"
    return bins


def create_plan(tier: str, base: str, event: str, candidate_input: str,
                container_smoke: bool = False, release_channel: str = '', release_base_tag: str = '',
                release_base_sha: str = '') -> dict:
    head = git('rev-parse', 'HEAD')
    if candidate_input and candidate_input != head:
        raise ValueError('candidate input differs from checkout')
    scenarios = coverage.load_scenarios()
    registered = [name for name, info in scenarios.items() if tier in info['Slices']]
    all_tests = all_framework_tests(head)
    tag, published, publish, release_reason = '', '', False, 'Not a publication candidate'
    if release_channel:
        if release_channel not in ('stable', 'beta', 'nightly') or not candidate_input:
            raise ValueError('invalid publication context')
        if release_channel != 'nightly':
            tag, published = release_tag(release_channel, head, release_base_tag)
        if release_base_sha and release_base_sha != published:
            raise ValueError('release baseline differs from channel tag')
        if event == 'push':
            publish, release_reason = needs_release(published, head)
        else:
            publish, release_reason = True, 'Explicit or scheduled publication'
    plan = dict(Version=3, CandidateSha=head, BaseSha=base, Tier=tier, Event=event,
                ReleaseRequired=publish, ReleaseChannel=release_channel, ReleaseBaseTag=tag,
                ReleaseBaseSha=published, ReleaseReason=release_reason,
                QualityBaseline=dict(Verified=False, RunId=0, Attempt=0),
                DocsOnly=False, Mode='full', Scenarios=registered, FrameworkTests=True,
                ConsumerProjects=None, GeneratedDocumentation=True, PackageDocumentation=True,
                Jobs={name: True for name in JOB_NAMES}, Inputs={}, ChangedPaths=[],
                Reason='Full: shared/unknown inputs, publication, explicit run or uncertain baseline',
                FrameworkTestProjects=all_tests, FrameworkTestSelection='all', FrameworkTestReason='')

    def finish(value):
        value['Jobs']['test'] = value['FrameworkTests']
        value['Jobs']['template-slices'] = bool(value['Scenarios'])
        value['DocsOnly'] = not any(value['Jobs'].values())
        # Container responsibility is meaningful only alongside full runtime stages.
        value['ContainerSmoke'] = bool(container_smoke and value['Mode'] == 'full')
        if container_smoke and value['Mode'] not in ('full', 'documentation'):
            raise ValueError('Container scope requires a dynamic full-mode plan')
        if not value['FrameworkTests']:
            value.update(FrameworkTestProjects=[], FrameworkTestSelection='none', FrameworkTestReason='No framework test inputs')
        elif value['FrameworkTestSelection'] != 'affected':
            value.update(FrameworkTestProjects=all_tests, FrameworkTestSelection='all',
                         FrameworkTestReason=value['FrameworkTestReason'] or value['Reason'])
        value['Slices'] = execution_slices(scenarios, value['Scenarios'], tier, value['Mode'], value['ContainerSmoke'])
        return value

    try:
        paths = changed_paths(base, head)
        plan['ChangedPaths'] = paths
        classes = classify_inputs(base, head, paths)
        plan['Inputs'] = classes
        if classes['unknown'] or any(CONTAINER_INPUT.fullmatch(path) for path in paths):
            container_smoke = True
        if git('status', '--porcelain', '--untracked-files=normal'):
            return finish(dict(plan, Reason='Full: working tree differs from candidate HEAD'))
        if publish:
            if published and published != head:
                release_paths = changed_paths(published, head)
                container_smoke = container_smoke or bool(classify_inputs(published, head, release_paths)['unknown']) or any(CONTAINER_INPUT.fullmatch(path) for path in release_paths)
            return finish(dict(plan, Reason='Full: publication requires all quality responsibilities'))
        direct_pr = tier == 'pr' and event == 'pull_request' and not candidate_input
        if event == 'push' and release_channel and not publish:
            plan['QualityBaseline'] = verified_quality_base(base, head)
            if not plan['QualityBaseline']['Verified']:
                container_smoke = True
        documentation_push = event == 'push' and release_channel and plan['QualityBaseline']['Verified']
        if not direct_pr and not documentation_push:
            return finish(plan)
        if classes['unknown']:
            container_smoke = True
            return finish(dict(plan, Reason='Full: shared or unknown inputs: ' + ', '.join(classes['unknown'])))
        code = classes['frontend'] + classes['backend'] + classes['framework']
        generated_docs, package_docs = bool(classes['generated-doc']), bool(classes['package-doc'])
        gate_document = 'template/docs/standards/frontend-spartan.md' in paths
        if generated_docs:
            config_path = 'template/.template.config/template.json'
            old = json.loads(git('show', f'{base}:{config_path}'))
            current = json.loads(git('show', f'{head}:{config_path}'))
            if old != current:
                return finish(plan)
            for path in classes['generated-doc']:
                source_producers(old, path, scenarios)
                source_producers(current, path, scenarios)
        if not code:
            jobs = {name: False for name in JOB_NAMES}
            jobs.update({'frontend-gates': gate_document, 'framework-pack': package_docs, 'package-consumption': package_docs,
                         'template-generation': generated_docs})
            return finish(dict(plan, Mode='documentation', Scenarios=[], FrameworkTests=False,
                               ConsumerProjects=[], Jobs=jobs, GeneratedDocumentation=generated_docs,
                               PackageDocumentation=package_docs, Reason='Documentation responsibilities only'))
        if not direct_pr:
            return finish(plan)
        front = bool(classes['frontend']) and not classes['backend'] and not classes['framework']
        back = bool(classes['backend']) and not classes['frontend'] and not classes['framework']
        if front or back:
            config_path = 'template/.template.config/template.json'
            old = json.loads(git('show', f'{base}:{config_path}'))
            current = json.loads(git('show', f'{head}:{config_path}'))
            if old != current:
                return finish(plan)
            affected = {'identity', 'identity-all-features'}
            for path in code:
                affected |= source_producers(old, path, scenarios) | source_producers(current, path, scenarios)
            selected = [name for name in registered if name in affected]
            if not selected:
                return finish(plan)
            jobs = dict(plan['Jobs'])
            jobs.update({'frontend-gates': front or gate_document, 'test': False,
                         'package-consumption': package_docs})
            # PG/OIDC and generation still retain their independent responsibilities.
            plan.update(Mode='frontend' if front else 'backend', Scenarios=selected, FrameworkTests=False,
                        ConsumerProjects=[], Jobs=jobs, GeneratedDocumentation=generated_docs,
                        PackageDocumentation=package_docs, Reason='Template stages plus independent documentation responsibilities')
        else:
            try:
                selected_tests, reason = select_framework_tests(base, head, code)
                plan.update(FrameworkTestProjects=selected_tests, FrameworkTestSelection='affected', FrameworkTestReason=reason)
            except FrameworkProofUnavailable as error:
                plan.update(FrameworkTestReason=f'All: {error}')
            if classes['framework'] and not classes['frontend'] and not classes['backend']:
                owners = [project_owner(path) for path in code]
                if all(owners):
                    plan.update(ConsumerProjects=sorted(set(owners)), PackageDocumentation=True,
                                GeneratedDocumentation=generated_docs, Reason='Framework dependency closure plus documentation responsibilities')
    except (subprocess.CalledProcessError, ValueError, KeyError, OSError, IndexError, ElementTree.ParseError) as error:
        container_smoke = True
        return finish(dict(plan, Reason=f'Full: input/dependency proof unavailable ({error})'))
    return finish(plan)


def local_scenarios(base: str) -> dict:
    """Select full local products from file producers, independently of CI stages."""
    plan = create_plan('pr', base, 'pull_request', '')
    scenarios = coverage.load_scenarios()
    registered = {name: info for name, info in scenarios.items() if 'pr' in info['Slices']}
    head = plan['CandidateSha']
    result = dict(Kind='local-template-scenarios', HeadSha=head, BaseSha=base,
                  Scope='pr', Scenarios=list(registered), Selection='complete-pr',
                  Reason=plan['Reason'])
    if not re.fullmatch(r'[0-9a-f]{40}', base) or base == '0' * 40 or base == head:
        return result
    try:
        if git('status', '--porcelain', '--untracked-files=normal'):
            return result
        paths = git('-c', 'core.quotePath=false', 'diff', '--no-renames', '--name-only', '-z',
                    base, head).split('\0')[:-1]
        if not paths or not all(frontend_source(path) or backend_source(path) for path in paths):
            return result
        config_path = 'template/.template.config/template.json'
        old = json.loads(git('show', f'{base}:{config_path}'))
        current = json.loads(git('show', f'{head}:{config_path}'))
        if old != current:
            return result
        selected = {'identity', 'identity-all-features'}
        for path in paths:
            selected |= source_producers(old, path, registered) | source_producers(current, path, registered)
        if git('rev-parse', 'HEAD') != head or git('status', '--porcelain', '--untracked-files=normal'):
            raise ValueError('working tree or HEAD changed during selection')
        result.update(Scenarios=[name for name in registered if name in selected],
                      Selection='source-products', Reason='All changed source products and default/all-feature products')
    except (subprocess.CalledProcessError, ValueError, KeyError, OSError, IndexError, UnicodeError) as error:
        result['Reason'] = f'Complete PR: local product proof unavailable ({error})'
    return result


def local_framework_tests(base: str) -> dict:
    """Local L1 list: the same selection as a PR plan from base to the committed HEAD."""
    plan = create_plan('pr', base, 'pull_request', '')
    result = dict(Kind='local-framework-tests', HeadSha=plan['CandidateSha'], BaseSha=base,
                  Projects=plan['FrameworkTestProjects'], Selection=plan['FrameworkTestSelection'],
                  Reason=plan['FrameworkTestReason'])
    if git('rev-parse', 'HEAD') != plan['CandidateSha'] or git('status', '--porcelain', '--untracked-files=normal'):
        result.update(Projects=all_framework_tests(plan['CandidateSha']), Selection='all',
                      Reason='All: working tree or HEAD changed during selection')
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tier', choices=['pr', 'full'], default='full')
    parser.add_argument('--base', default=os.environ.get('PR_BASE_SHA', ''))
    parser.add_argument('--event', default=os.environ.get('EVENT_NAME', ''))
    parser.add_argument('--candidate-input', default=os.environ.get('CANDIDATE_SHA', ''))
    parser.add_argument('--release-channel', choices=['stable', 'beta', 'nightly'], default='')
    parser.add_argument('--release-base-tag', default='')
    parser.add_argument('--release-base-sha', default='')
    parser.add_argument('--expected-plan', type=Path)
    parser.add_argument('--container-smoke', choices=['true', 'false'], default='false',
                        help='Bind the independent container-scope decision into this candidate plan')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--github-output', action='store_true')
    parser.add_argument('--local-scenarios', action='store_true',
                        help='Output local L1 scenario names for -Scenarios; no CI stage reduction')
    parser.add_argument('--local-framework-tests', action='store_true',
                        help='Output local L1 framework test projects (Projects) affected since --base')
    args = parser.parse_args()
    local = args.local_scenarios or args.local_framework_tests
    if local and (args.local_scenarios and args.local_framework_tests or args.tier != 'pr' or args.github_output or
                  args.release_channel or args.release_base_tag or args.release_base_sha or args.expected_plan or args.candidate_input or args.container_smoke != 'false'):
        parser.error('--local-scenarios/--local-framework-tests: choose one, with --tier pr and without CI output, docs-only or candidate-input')
    if args.local_scenarios:
        plan = local_scenarios(args.base)
    elif args.local_framework_tests:
        plan = local_framework_tests(args.base)
    else:
        plan = create_plan(args.tier, args.base, args.event, args.candidate_input, args.container_smoke == 'true',
                           args.release_channel, args.release_base_tag, args.release_base_sha)
        if args.expected_plan and json.loads(args.expected_plan.read_text(encoding='utf-8-sig')) != plan:
            raise ValueError('caller plan differs from independently computed candidate responsibilities')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(plan, ensure_ascii=False, separators=(',', ':'))
    args.output.write_text(text, encoding='utf-8')
    if args.github_output:
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
            output.write('validation_plan=' + text + '\n')
            output.write('slices=' + json.dumps(plan['Slices'], ensure_ascii=False, separators=(',', ':')) + '\n')
            output.write('release_required=' + str(plan['ReleaseRequired']).lower() + '\n')
            output.write('release_base_tag=' + plan['ReleaseBaseTag'] + '\n')
            output.write('release_base_sha=' + plan['ReleaseBaseSha'] + '\n')
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
            if plan['DocsOnly']:
                summary.write('本次仅修改内部文档：此作业只判定范围和生成计划；未打包 Framework，动态验证不适用。\n\n')
            summary.write('验证计划：' + text + '\n')
    print(text)


if __name__ == '__main__':
    main()
