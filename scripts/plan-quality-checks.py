#!/usr/bin/env python3
"""Plan PR validation from complete input changes.

Input classes, job responsibilities and the PR scenario selector live here.
Scenario/source definitions stay in their existing files; conditions are evaluated
with the coverage gate's evaluator.

A direct PR always has Tier core; the template full set runs only after merge,
nightly, on dispatch and for publication. A narrowed core plan (ScenarioSelection
selector) carries the incremental guarantee: the core consumers of every changed
source, every changed conditional branch on its enabled side and the trimmed side
of conditional files. Inputs that cannot be proven (shared/unknown inputs,
template.json, proof errors) get the representative smoke (ScenarioSelection
representative): every core scenario with all applicable stages, provable branch
supplements, all framework tests and consumers. Global line coverage and pairwise
combinations are proven only by the full tier before any publication.
"""
from __future__ import annotations

import argparse
import importlib.util
import itertools
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
    # A missing/deleted project, project configuration or shared input means all consumers.
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


# PostgreSQL/OIDC end-to-end run on a narrowed PR only when a registered path changes;
# otherwise the PR records them as not applicable and the pre-publication full run owns them.
POSTGRESQL_INPUT = re.compile(
    r'template/backend/(?:src|tests)/.*(?:Persistence|Migrat|Tenant|DbMigrator|DataProtection|DbContext).*|'
    r'framework/(?:components/(?:data|multi-tenancy|unit-of-work)|ddd-struct/Leistd\.Ddd\.Infrastructure)/.+|'
    r'framework/components/[^/]+/Leistd\.[^/]+\.EntityFrameworkCore/.+')
OIDC_INPUT = re.compile(
    r'template/backend/(?:src|tests)/.*(?:Auth|Token|Connect|OpenIddict|OpenApplication|Security|Session|SignIn|'
    r'ServiceInfo|WhoAmI|ServiceClient|Tenant).*|'
    r'framework/components/(?:authorization(?:-[a-z-]+)?|security|service-client|multi-tenancy)/.+')
BINARY_SOURCE = ('.png', '.jpg', '.jpeg', '.webp', '.ico')
CONFIG_PATH = 'template/.template.config/template.json'

JOB_NAMES = ('frontend-gates', 'framework-pack', 'package-consumption', 'template-generation',
             'template-slices', 'test', 'postgresql-e2e', 'oidc-e2e')
INTERNAL_DOCUMENT = re.compile(r'(?:README\.md|(?:docs|\.agents|skills)/.+\.md|framework/README\.md)')
# Repository configuration read only by maintainer services (Dependabot, the root MCP servers for agents) and
# static gates: no build, test or package consumes it. validate-skills checks the MCP pin in every docs-sync run.
REPOSITORY_CONFIG = re.compile(r'\.github/dependabot\.yml|\.mcp\.json')
# Template channel inputs: the generated payload and its packaging project (template-package/).
# They publish Leistd.Templates only; the packaging project is otherwise an unknown input (representative smoke on a direct PR).
TEMPLATE_RELEASE_INPUTS = ('template/', 'template-package/')
TEMPLATE_RELEASE_TAG = re.compile(r'template-v(\d+)\.(\d+)\.(\d+)')
# The stable framework release write-back commit (VERSION and the template's default LeistdFrameworkVersion).
# Same rule as Test-ReleaseWriteBack in scripts/release-commits.ps1: a framework-only change never bumps the template.
RELEASE_WRITE_BACK = re.compile(r'chore: 发布 v\d+\.\d+\.\d+ \[skip ci\]')
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
        elif (INTERNAL_DOCUMENT.fullmatch(path) or REPOSITORY_CONFIG.fullmatch(path)
              or path.startswith('framework/docs/') and path.endswith('.md')):
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
        unknown = [path for path in paths if path not in harmless and not path.startswith((*TEMPLATE_RELEASE_INPUTS, 'framework/tests/'))]
        return bool(hits or unknown), 'Changed package or unmodelled inputs: ' + ', '.join(sorted(set(hits + unknown))) if hits or unknown else 'Only non-package inputs'
    except (ValueError, OSError, subprocess.CalledProcessError, ElementTree.ParseError) as error:
        return True, f'Publication input proof unavailable: {error}'


def template_release(channel: str, head: str) -> dict:
    """Template channel: changed when template inputs moved since the latest template-v tag.

    Required is decided by create_plan: on main the template publishes exactly when the framework does, under one version.
    The template release job recomputes the same range with the shared commit parser and
    fails when its result differs from this decision.
    """
    if channel not in ('stable', 'beta'):
        return dict(Changed=False, Required=False, BaseTag='', BaseSha='',
                    Reason='Nightly carries no template channel' if channel else 'Not a publication candidate')
    tags = [name for name in git('tag', '--merged', head).splitlines() if TEMPLATE_RELEASE_TAG.fullmatch(name)]
    if not tags:
        changed, base_tag, base_sha, reason = True, '', '', 'No published template version'
    else:
        base_tag = max(tags, key=lambda name: tuple(map(int, TEMPLATE_RELEASE_TAG.fullmatch(name).groups())))
        base_sha = git('rev-parse', f'{base_tag}^{{commit}}')
        subjects = git('log', '--no-merges', '--format=%s', f'{base_tag}..{head}', '--', *TEMPLATE_RELEASE_INPUTS).splitlines()
        commits = [subject for subject in subjects if not RELEASE_WRITE_BACK.fullmatch(subject)]
        changed = bool(commits)
        reason = f'{len(commits)} template commits since {base_tag}' if changed else f'No template changes since {base_tag}'
    if changed and channel != 'stable':
        reason += '; template publishes only from main'
    return dict(Changed=changed, Required=False, BaseTag=base_tag, BaseSha=base_sha, Reason=reason)


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
    """Bind the registered logical allocation to the candidate's selected work.

    The core tier runs one scenario per group, including scenarios the selector adds
    from outside core; the full tier keeps its registered balanced groups.
    """
    if not selected:
        return []
    sample = next(iter(scenarios.values()))
    selected_names = set(selected)
    containers = set(sample['Containers']) if container_smoke else set()
    if tier == 'core':
        logical_groups = [[name] for name in scenarios if name in selected_names]
    else:
        logical_groups = [[name for name, info in scenarios.items()
                           if name in selected_names and info['Slices'].get(tier) == logical]
                          for logical in sample['Titles'][tier]]
    bins = []
    for members in logical_groups:
        if members:
            bins.append(dict(key=f'execution-{len(bins)+1:02d}', title='', Scenarios=members,
                             Containers=[name for name in members if name in containers]))
    for group in bins:
        preview = '、'.join(sorted(group['Scenarios'], key=lambda name: (len(name), name))[:2])
        stages = {'full':'完整阶段','frontend':'前端阶段','backend':'后端阶段'}[mode]
        container = '；含容器' if group['Containers'] else ''
        group['title'] = f"{stages} · {preview}（共{len(group['Scenarios'])}场景{container}）"
    return bins


def changed_lines(base: str, head: str, path: str) -> tuple[set[int], set[int]]:
    """Changed line numbers of one file on the base and head side.

    A pure insertion or deletion touches both neighbouring lines of the other side,
    so a branch that only gained or lost lines still counts as changed there.
    """
    diff = git('-c', 'core.quotePath=false', 'diff', '--no-renames', '--no-ext-diff', '-U0', base, head, '--', path)
    old, new = set(), set()
    for match in re.finditer(r'^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@', diff, re.M):
        for start, count, side in ((int(match[1]), match[2], old), (int(match[3]), match[4], new)):
            count = 1 if count is None else int(count)
            side.update(range(start, start + count) if count else (start, start + 1))
    return old, new


def source_text(sha: str, path: str) -> str | None:
    """Text of a template source at one commit; None when absent there."""
    try:
        git('cat-file', '-e', f'{sha}:{path}')
    except subprocess.CalledProcessError:
        return None
    if path.endswith(BINARY_SOURCE):
        return ''
    return git('show', f'{sha}:{path}')


def template_requirements(config: dict, scenarios: dict, base: str, head: str, paths: list[str],
                          eligible: set[str]) -> list[tuple[str, set[str]]]:
    """Generation contexts the changed template sources must reach, with the scenarios that reach each.

    Every changed file is generated at least once; a file with file-level conditions is
    also trimmed at least once; every arm (nested, each elif, implicit empty else) of a
    changed conditional block is generated on its enabled side at least once. Both trees
    count, so deleted and renamed files keep their old conditions. Only scenarios whose
    stages apply to the plan mode count; a context no such scenario reaches is the full
    tier's responsibility (its coverage gate requires every reachable line to be produced).
    """
    values = {name: coverage.symbol_values(config, coverage.parse_cli_arguments(config, info['Arguments']))
              for name, info in scenarios.items() if name in eligible}
    requirements = []
    for path in paths:
        matches = [(condition, coverage.file_conditions(relative, modifiers))
                   for relative, _, condition, modifiers in coverage.iter_sources(config, [path])]
        if not matches:
            raise ValueError(f'No known template source for {path}')

        def reached(frames):
            return {name for name, value in values.items()
                    if any(coverage.generated(value, frames, condition, excludes) for condition, excludes in matches)}
        requirements.append((f'{path}: generated', reached(())))
        if any(condition or excludes for condition, excludes in matches):
            requirements.append((f'{path}: trimmed', set(values) - reached(())))
        sides = changed_lines(base, head, path)
        for sha, lines in ((base, sides[0]), (head, sides[1])):
            text = source_text(sha, path)
            if not text:
                continue
            for frames in coverage.branch_leaves(text, lines):
                label = ' > '.join(current.strip() for current, _ in frames)
                requirements.append((f'{path}@{sha[:7]}: {label}', reached(frames)))
    return [(label, owners) for label, owners in requirements if owners]


def select_scenarios(requirements: list[tuple[str, set[str]]], initial: set[str], order: list[str],
                     preferred: set[str]) -> list[str]:
    """Smallest added scenario set covering every requirement the initial set leaves open.

    Exact search up to four added scenarios, preferred (core) scenarios first and then the
    registered order; beyond that a deterministic greedy cover.
    """
    open_requirements = [owners for _, owners in requirements if not owners & initial]
    candidates = sorted((name for name in order if name not in initial and any(name in owners for owners in open_requirements)),
                        key=lambda name: (name not in preferred, order.index(name)))
    chosen: tuple[str, ...] = ()
    if open_requirements:
        for size in range(1, min(4, len(candidates)) + 1):
            chosen = next((combo for combo in itertools.combinations(candidates, size)
                           if all(owners & set(combo) for owners in open_requirements)), ())
            if chosen:
                break
        if not chosen:
            remaining, picked = list(open_requirements), []
            while remaining:
                best = max(candidates, key=lambda name: (sum(name in owners for owners in remaining), -candidates.index(name)))
                picked.append(best)
                remaining = [owners for owners in remaining if best not in owners]
            chosen = tuple(picked)
    selected = initial | set(chosen)
    return [name for name in order if name in selected]


def create_plan(tier: str, base: str, event: str, candidate_input: str,
                container_smoke: bool = False, release_channel: str = '', release_base_tag: str = '',
                release_base_sha: str = '') -> dict:
    """tier core on a direct PR yields Tier core (selector or representative); every other request is the full tier."""
    if tier not in ('core', 'full'):
        raise ValueError('unknown tier')
    head = git('rev-parse', 'HEAD')
    if candidate_input and candidate_input != head:
        raise ValueError('candidate input differs from checkout')
    scenarios = coverage.load_scenarios()
    order = list(scenarios)
    core = [name for name, info in scenarios.items() if 'core' in info['Slices']]
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
    template = template_release(release_channel, head)
    if release_channel == 'stable':
        # Framework and template share one version on main: either side changing publishes both.
        if template['Changed'] and not publish:
            publish, release_reason = True, 'Template changes publish with the framework under one version'
        template['Required'] = publish
        if publish and not template['Changed']:
            template['Reason'] += '; published with the framework under one version'
    # Only a direct PR may lower the template responsibility; every other trigger keeps full.
    direct_pr = tier == 'core' and event == 'pull_request' and not candidate_input and not release_channel
    plan = dict(Version=3, CandidateSha=head, BaseSha=base, Tier='full', Event=event,
                ReleaseRequired=publish, ReleaseChannel=release_channel, ReleaseBaseTag=tag,
                ReleaseBaseSha=published, ReleaseReason=release_reason, TemplateRelease=template,
                QualityBaseline=dict(Verified=False, RunId=0, Attempt=0),
                DocsOnly=False, Mode='full', Scenarios=order, ScenarioSelection='all', FrameworkTests=True,
                ConsumerProjects=None, GeneratedDocumentation=True, PackageDocumentation=True,
                Jobs={name: True for name in JOB_NAMES}, Inputs={}, ChangedPaths=[],
                Reason='Full: shared/unknown inputs, publication, explicit run or uncertain baseline',
                FrameworkTestProjects=all_tests, FrameworkTestSelection='all', FrameworkTestReason='')
    complete = json.loads(json.dumps(plan))

    def finish(value):
        value['Jobs']['test'] = value['FrameworkTests']
        value['Jobs']['template-slices'] = bool(value['Scenarios'])
        value['DocsOnly'] = not any(value['Jobs'].values())
        # Container responsibility is meaningful only alongside full runtime stages.
        # A publication candidate always carries it, independently of input classification.
        value['ContainerSmoke'] = bool((container_smoke or value['ReleaseRequired'] or value['TemplateRelease']['Required'])
                                       and value['Mode'] == 'full')
        if container_smoke and value['Mode'] not in ('full', 'documentation'):
            raise ValueError('Container scope requires a dynamic full-mode plan')
        if not value['FrameworkTests']:
            value.update(FrameworkTestProjects=[], FrameworkTestSelection='none', FrameworkTestReason='No framework test inputs')
        elif value['FrameworkTestSelection'] != 'affected':
            value.update(FrameworkTestProjects=all_tests, FrameworkTestSelection='all',
                         FrameworkTestReason=value['FrameworkTestReason'] or value['Reason'])
        if not value['Scenarios']:
            value['ScenarioSelection'] = 'none'
        value['Slices'] = execution_slices(scenarios, value['Scenarios'], value['Tier'], value['Mode'], value['ContainerSmoke'])
        return value

    def representative(reason, supplements=()):
        """Direct-PR fallback: core scenarios with all stages instead of the template full set.

        Nothing narrowed before survives: framework tests, consumers, generation and static
        responsibilities stay complete; PostgreSQL/OIDC keep their path triggers (unknown paths: run).
        """
        paths = plan['ChangedPaths']
        jobs = {name: True for name in JOB_NAMES}
        jobs.update({'postgresql-e2e': not paths or any(POSTGRESQL_INPUT.fullmatch(path) for path in paths),
                     'oidc-e2e': not paths or any(OIDC_INPUT.fullmatch(path) for path in paths)})
        selected = set(core) | set(supplements)
        return finish(dict(complete, ChangedPaths=paths, Inputs=plan['Inputs'], QualityBaseline=plan['QualityBaseline'],
                           Tier='core', ScenarioSelection='representative', Jobs=jobs,
                           Scenarios=[name for name in order if name in selected],
                           Reason=f'Core representative smoke ({reason}); unproven inputs reach the full set after merge'))

    def branch_supplements(classes):
        """Changed conditional branches outside core, when the template sources can still be proven."""
        sources = classes['frontend'] + classes['backend']
        if not sources:
            return set()
        try:
            old = json.loads(git('show', f'{base}:{CONFIG_PATH}'))
            current = json.loads(git('show', f'{head}:{CONFIG_PATH}'))
            if old != current:
                return set()
            for path in sources:
                source_producers(current, path, scenarios)
            requirements = template_requirements(current, scenarios, base, head, sources, set(order))
            return set(select_scenarios(requirements, set(core), order, set(core)))
        except (subprocess.CalledProcessError, ValueError, KeyError, OSError, IndexError, UnicodeError):
            return set()

    try:
        paths = changed_paths(base, head)
        plan['ChangedPaths'] = paths
        classes = classify_inputs(base, head, paths)
        plan['Inputs'] = classes
        if classes['unknown'] or any(CONTAINER_INPUT.fullmatch(path) for path in paths):
            container_smoke = True
        if git('status', '--porcelain', '--untracked-files=normal'):
            return finish(dict(plan, Reason='Full: working tree differs from candidate HEAD'))
        if publish or template['Required']:
            return finish(dict(plan, Reason='Full: publication requires all quality responsibilities'))
        if event == 'push' and release_channel and not publish:
            plan['QualityBaseline'] = verified_quality_base(base, head)
            if not plan['QualityBaseline']['Verified']:
                container_smoke = True
        documentation_push = event == 'push' and release_channel and plan['QualityBaseline']['Verified']
        if not direct_pr and not documentation_push:
            return finish(plan)
        if classes['unknown']:
            container_smoke = True
            if direct_pr:
                return representative('shared or unknown inputs: ' + ', '.join(classes['unknown']), branch_supplements(classes))
            return finish(dict(plan, Reason='Full: shared or unknown inputs: ' + ', '.join(classes['unknown'])))
        template_code = classes['frontend'] + classes['backend']
        code = template_code + classes['framework']
        generated_docs, package_docs = bool(classes['generated-doc']), bool(classes['package-doc'])
        gate_document = 'template/docs/standards/frontend-spartan.md' in paths
        narrowed = 'core' if direct_pr else 'full'
        if generated_docs or template_code:
            old = json.loads(git('show', f'{base}:{CONFIG_PATH}'))
            current = json.loads(git('show', f'{head}:{CONFIG_PATH}'))
            if old != current:
                container_smoke = True
                if direct_pr:
                    return representative('template.json changed')
                return finish(dict(plan, Reason='Full: template.json changed'))
            for path in classes['generated-doc'] + template_code:
                source_producers(old, path, scenarios)
                source_producers(current, path, scenarios)
        if not code:
            jobs = {name: False for name in JOB_NAMES}
            jobs.update({'frontend-gates': gate_document, 'framework-pack': package_docs, 'package-consumption': package_docs,
                         'template-generation': generated_docs})
            return finish(dict(plan, Tier=narrowed, Mode='documentation', Scenarios=[], FrameworkTests=False,
                               ConsumerProjects=[], Jobs=jobs, GeneratedDocumentation=generated_docs,
                               PackageDocumentation=package_docs, Reason='Documentation responsibilities only'))
        if not direct_pr:
            return finish(plan)
        front = bool(classes['frontend']) and not classes['backend'] and not classes['framework']
        back = bool(classes['backend']) and not classes['frontend'] and not classes['framework']
        mode = 'frontend' if front else 'backend' if back else 'full'
        eligible = {name for name, info in scenarios.items() if mode != 'frontend' or info['Frontend']}
        if classes['framework']:
            # Framework changes reach every product: core is the downstream smoke.
            initial = set(core)
        else:
            producers = set()
            for path in template_code:
                producers |= source_producers(old, path, scenarios) | source_producers(current, path, scenarios)
            initial = set(core) & producers & eligible
        requirements = template_requirements(current, scenarios, base, head, template_code, eligible) if template_code else []
        selected = select_scenarios(requirements, initial, order, set(core))
        if not selected:
            return representative('no registered scenario produces the changed sources')
        jobs = {name: True for name in JOB_NAMES}
        jobs.update({'frontend-gates': bool(classes['frontend']) or gate_document,
                     'package-consumption': bool(classes['framework']) or package_docs,
                     'template-generation': bool(template_code) or generated_docs,
                     'postgresql-e2e': any(POSTGRESQL_INPUT.fullmatch(path) for path in code),
                     'oidc-e2e': any(OIDC_INPUT.fullmatch(path) for path in code)})
        plan.update(Tier='core', Mode=mode, Scenarios=selected, ScenarioSelection='selector', Jobs=jobs, GeneratedDocumentation=generated_docs,
                    Reason=('Core consumers of changed sources plus changed conditional branches; '
                            f'{len(requirements)} generation contexts'))
        if not classes['framework']:
            plan.update(FrameworkTests=False, ConsumerProjects=[], PackageDocumentation=package_docs)
        else:
            plan.update(PackageDocumentation=True)
            # Template sources belong to no framework project: a mixed change keeps all tests and consumers.
            try:
                selected_tests, reason = select_framework_tests(base, head, code)
                plan.update(FrameworkTestProjects=selected_tests, FrameworkTestSelection='affected', FrameworkTestReason=reason)
            except FrameworkProofUnavailable as error:
                plan.update(FrameworkTestReason=f'All: {error}')
            owners = [project_owner(path) for path in code]
            if all(owners):
                plan['ConsumerProjects'] = sorted(set(owners))
    except (subprocess.CalledProcessError, ValueError, KeyError, OSError, IndexError, ElementTree.ParseError) as error:
        container_smoke = True
        if direct_pr:
            return representative(f'input/dependency proof unavailable ({error})')
        # Nothing narrowed before the failure survives: restart from the complete plan.
        return finish(dict(complete, ChangedPaths=plan['ChangedPaths'], Inputs=plan['Inputs'], QualityBaseline=plan['QualityBaseline'],
                           Reason=f'Full: input/dependency proof unavailable ({error})'))
    return finish(plan)


def local_scenarios(base: str) -> dict:
    """Local recipe: the PR plan's scenarios for -GenerateOnly.

    Selection selector: the PR selector, with generated documents counted as conditional
    sources so a Markdown branch is generated too. Selection representative: the PR could
    not narrow (or the local document proof failed), so the core scenarios plus provable
    supplements, the same as the PR. Selection full only for a dirty tree or a moving HEAD.
    """
    plan = create_plan('core', base, 'pull_request', '')
    scenarios = coverage.load_scenarios()
    order = list(scenarios)
    head = plan['CandidateSha']
    core = {name for name, info in scenarios.items() if 'core' in info['Slices']}
    result = dict(Kind='local-template-scenarios', HeadSha=head, BaseSha=base,
                  Scope='full', Scenarios=order, Selection='full', Reason=plan['Reason'])
    if plan['Tier'] != 'core':
        return result
    if plan['ScenarioSelection'] == 'representative':
        result.update(Scope='core', Scenarios=plan['Scenarios'], Selection='representative')
    else:
        try:
            inputs = plan['Inputs']
            sources = inputs['frontend'] + inputs['backend'] + inputs['generated-doc']
            selected = list(plan['Scenarios'])
            if sources:
                config = json.loads(git('show', f'{head}:{CONFIG_PATH}'))
                eligible = {name for name, info in scenarios.items() if plan['Mode'] != 'frontend' or info['Frontend']}
                producers = set()
                for path in sources:
                    producers |= source_producers(config, path, scenarios)
                requirements = template_requirements(config, scenarios, base, head, sources, eligible)
                selected = select_scenarios(requirements, set(selected) | (core & producers & eligible), order, core)
            result.update(Scope='core', Scenarios=selected, Selection='selector',
                          Reason='PR selector: core consumers and changed conditional branches')
        except (subprocess.CalledProcessError, ValueError, KeyError, OSError, IndexError, UnicodeError) as error:
            result.update(Scope='core', Scenarios=[name for name in order if name in core or name in plan['Scenarios']],
                          Selection='representative', Reason=f'Core representative smoke: local selection proof unavailable ({error})')
    if git('rev-parse', 'HEAD') != head or git('status', '--porcelain', '--untracked-files=normal'):
        result.update(Scope='full', Scenarios=order, Selection='full', Reason='Full: working tree or HEAD changed during selection')
    return result


def local_framework_tests(base: str) -> dict:
    """Local L1 list: the same selection as a PR plan from base to the committed HEAD."""
    plan = create_plan('core', base, 'pull_request', '')
    result = dict(Kind='local-framework-tests', HeadSha=plan['CandidateSha'], BaseSha=base,
                  Projects=plan['FrameworkTestProjects'], Selection=plan['FrameworkTestSelection'],
                  Reason=plan['FrameworkTestReason'])
    if git('rev-parse', 'HEAD') != plan['CandidateSha'] or git('status', '--porcelain', '--untracked-files=normal'):
        result.update(Projects=all_framework_tests(plan['CandidateSha']), Selection='all',
                      Reason='All: working tree or HEAD changed during selection')
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tier', choices=['core', 'full'], default='full',
                        help='core: a direct pull request (narrowed where provable, otherwise the representative smoke)')
    parser.add_argument('--base', default=os.environ.get('PR_BASE_SHA', ''))
    parser.add_argument('--event', default=os.environ.get('EVENT_NAME', ''))
    parser.add_argument('--candidate-input', default=os.environ.get('CANDIDATE_SHA', ''))
    parser.add_argument('--release-channel', choices=['stable', 'beta', 'nightly'], default='')
    parser.add_argument('--release-base-tag', default='')
    parser.add_argument('--release-base-sha', default='')
    parser.add_argument('--container-smoke', choices=['true', 'false'], default='false',
                        help='Bind the independent container-scope decision into this candidate plan')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--github-output', action='store_true')
    parser.add_argument('--local-scenarios', action='store_true',
                        help='Output the PR plan scenarios for local -GenerateOnly checks (selector or representative; full only for a dirty tree)')
    parser.add_argument('--local-framework-tests', action='store_true',
                        help='Output local L1 framework test projects (Projects) affected since --base')
    args = parser.parse_args()
    local = args.local_scenarios or args.local_framework_tests
    if local and (args.local_scenarios and args.local_framework_tests or args.tier != 'core' or args.github_output or
                  args.release_channel or args.release_base_tag or args.release_base_sha or args.candidate_input or args.container_smoke != 'false'):
        parser.error('--local-scenarios/--local-framework-tests: choose one, with --tier core and without CI output, docs-only or candidate-input')
    if args.local_scenarios:
        plan = local_scenarios(args.base)
    elif args.local_framework_tests:
        plan = local_framework_tests(args.base)
    else:
        plan = create_plan(args.tier, args.base, args.event, args.candidate_input, args.container_smoke == 'true',
                           args.release_channel, args.release_base_tag, args.release_base_sha)
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
