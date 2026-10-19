#!/usr/bin/env python3
"""Exercise the actual workflow scope steps in isolated Git repositories.

Run when changing CI/release scope selection, framework test receipts or the release link policy
(PackageReleaseNotes and the upgrade-guide link); this is not a per-feature gate.
The release link check packs a fixture project, so it also requires the .NET SDK.
Requires PowerShell and the same PyYAML dependency used by Skill validation.

`--release-publish` runs only the release publish/recovery checks (tag push retries against a
local bare remote, recovery summary stages, artifact bundle rehearsal); they need git and pwsh, not .NET.
"""

from contextlib import contextmanager
import importlib.util
import io
import shutil
from unittest import mock
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from xml.etree import ElementTree
import zipfile

import yaml


ROOT = Path(__file__).resolve().parents[1]


def scope_step(workflow, job, step_id):
    data = yaml.safe_load((ROOT / '.github/workflows' / workflow).read_text(encoding='utf-8'))
    return next(s['run'] for s in data['jobs'][job]['steps'] if s.get('id') == step_id)


def git(repo, *args):
    return subprocess.check_output(['git', '-C', str(repo), *args], text=True, encoding='utf-8', errors='replace').strip()


def evaluate(repo, script, base, event='push', candidate='', extra_env=None):
    output = repo / 'output.txt'
    output.unlink(missing_ok=True)
    script = script.replace("${{ github.event.pull_request.base.sha }}", base)
    script = script.replace("${{ github.event.before }}", base)
    script = script.replace("${{ github.event_name }}", event)
    script = script.replace("${{ github.ref }}", 'refs/heads/main')
    bash = script.startswith(("python - <<", "python3 - <<"))
    script_path = repo / ('scope.sh' if bash else 'scope.ps1')
    script_path.write_text(script, encoding='utf-8')
    command = ['bash', '-e', '-o', 'pipefail', str(script_path)] if bash else ['pwsh', '-NoProfile', '-File', str(script_path)]
    result = subprocess.run(command, cwd=repo,
                            env={**dict(os.environ, GITHUB_OUTPUT=str(output),
                                     GITHUB_STEP_SUMMARY=str(repo / 'summary.txt'),
                                     PR_BASE_SHA=base, EVENT_NAME=event, CANDIDATE_SHA=candidate),
                                 **(extra_env or {})},
                            capture_output=True, text=True, encoding='utf-8', errors='replace')
    return result.returncode, output.read_text(encoding='utf-8').strip() if output.exists() else '', result.stderr


@contextmanager
def candidate_fixture():
    with tempfile.TemporaryDirectory(prefix='leistd-scope-') as directory:
        repo = Path(directory)
        paths = git(ROOT, 'ls-files', '--cached', '--others', '--exclude-standard', '-z').split('\0')
        for path in paths:
            if not path or path.startswith('docs/reports/'):
                continue
            source = ROOT / path
            if source.is_file():
                target = repo / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        git(repo, 'init', '-q')
        git(repo, 'config', 'user.name', 'Scope fixture')
        git(repo, 'config', 'user.email', 'scope@example.invalid')
        git(repo, 'add', '.')
        git(repo, 'commit', '-qm', 'fixture base')
        spec = importlib.util.spec_from_file_location('planner_fixture', repo / 'scripts/plan-quality-checks.py')
        planner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(planner)
        yield repo, planner, git(repo, 'rev-parse', 'HEAD')


def append_input(repo, path):
    target = repo / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text((target.read_text(encoding='utf-8') if target.exists() else '') + '\n<!-- scope fixture -->\n', encoding='utf-8')
    git(repo, 'add', '.')
    git(repo, 'commit', '-qm', 'input change')


def check_existing_scopes():
    with candidate_fixture() as (repo, planner, base):
        git(repo, 'tag', 'v0.13.0-beta.10', base)
        git(repo, 'tag', 'v1.0.0-beta.9999', base)
        front = 'template/frontend/src/app/app.ts'
        append_input(repo, front)
        before = git(repo, 'rev-parse', 'HEAD')
        git(repo, 'tag', 'v0.13.0-preview.20261008.99')
        append_input(repo, 'README.md')
        head = git(repo, 'rev-parse', 'HEAD')
        assert planner.release_tag('beta', head)[1] == base
        run = dict(id=1, run_attempt=2, head_sha=before, head_branch='develop', event='push',
                   path='.github/workflows/release.yml', status='completed', conclusion='success')
        aggregate = dict(name='quality / 模板全场景矩阵', status='completed', conclusion='success')
        class Response:
            def __init__(self, payload): self.payload = payload
            def __enter__(self): return io.StringIO(json.dumps(self.payload))
            def __exit__(self, *args): return False
        def api(request, timeout):
            assert timeout == 15
            assert request.full_url.startswith('https://api.github.com/repos/fixture/repo/actions/')
            if '/workflows/release.yml/runs?' in request.full_url:
                assert 'head_sha=' + before in request.full_url and 'branch=develop' in request.full_url and 'event=push' in request.full_url
                return Response(dict(total_count=1, workflow_runs=[run]))
            assert '/runs/1/attempts/2/jobs?' in request.full_url
            return Response(dict(total_count=1, jobs=[aggregate]))
        env = dict(GITHUB_REPOSITORY='fixture/repo', GITHUB_REF_NAME='develop', GH_TOKEN='fixture-token', GITHUB_API_URL='https://api.github.com')
        with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'urlopen', side_effect=api):
            plan = planner.create_plan('full', before, 'push', head, release_channel='beta')
            assert plan['Mode'] == 'documentation' and plan['QualityBaseline']['Verified'] and not plan['ReleaseRequired']
            for state in ['failure', 'cancelled', 'timed_out', None]:
                run['conclusion'] = state
                plan = planner.create_plan('full', before, 'push', head, release_channel='beta')
                assert plan['Mode'] == 'full' and not plan['QualityBaseline']['Verified']
            run['conclusion'] = 'success'
            run['status'] = 'in_progress'
            assert planner.create_plan('full', before, 'push', head, release_channel='beta')['Mode'] == 'full'
            run['status'] = 'completed'
            aggregate['conclusion'] = 'skipped'
            assert not planner.verified_quality_base(before, head)['Verified']
            aggregate['conclusion'] = 'success'
            aggregate['name'] = 'quality / 模板全场景矩阵 fake'
            assert not planner.verified_quality_base(before, head)['Verified']
        with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'urlopen', side_effect=OSError('API unavailable')):
            assert planner.create_plan('full', before, 'push', head, release_channel='beta')['Mode'] == 'full'
        with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'urlopen', return_value=Response(dict(total_count=0, workflow_runs=[]))):
            assert not planner.verified_quality_base(before, head)['Verified']
        # A failed Docker change cannot disappear from the next known-source push.
        git(repo, 'reset', '--hard', base)
        append_input(repo, 'template/Dockerfile')
        before = git(repo, 'rev-parse', 'HEAD')
        append_input(repo, front)
        head = git(repo, 'rev-parse', 'HEAD')
        run.update(head_sha=before, status='completed', conclusion='success')
        aggregate.update(name='quality / 模板全场景矩阵', conclusion='success')
        with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'urlopen', side_effect=api):
            successful = planner.create_plan('full', before, 'push', head, release_channel='beta')
            assert successful['Mode'] == 'full' and not successful['ContainerSmoke'] and not successful['ReleaseRequired']
            for state in ['failure', 'cancelled']:
                run['conclusion'] = state
                carried = planner.create_plan('full', before, 'push', head, release_channel='beta')
                assert carried['Mode'] == 'full' and carried['ContainerSmoke'] and not carried['ReleaseRequired']
        # Pending/failed package publication has no tag: a later docs push still publishes and validates full.
        git(repo, 'reset', '--hard', base)
        append_input(repo, 'framework/NuGet.md')
        before = git(repo, 'rev-parse', 'HEAD')
        append_input(repo, 'README.md')
        head = git(repo, 'rev-parse', 'HEAD')
        plan = planner.create_plan('full', before, 'push', head, release_channel='beta')
        assert plan['ReleaseRequired'] and plan['Mode'] == 'full' and all(plan['Jobs'].values())
        for path in ['VERSION', 'framework/common.props', 'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'NuGet.config']:
            git(repo, 'reset', '--hard', base)
            append_input(repo, path)
            assert planner.needs_release(base, git(repo, 'rev-parse', 'HEAD'))[0], path
        # Dependabot 配置只由 GitHub 服务读取：不产出包，不触发发布
        git(repo, 'reset', '--hard', base)
        append_input(repo, '.github/dependabot.yml')
        assert not planner.needs_release(base, git(repo, 'rev-parse', 'HEAD'))[0], '.github/dependabot.yml'
        # A main-only stable tag is not an ancestor of develop.
        git(repo, 'reset', '--hard', base)
        append_input(repo, 'VERSION')
        stable = git(repo, 'rev-parse', 'HEAD')
        git(repo, 'tag', 'v0.14.0', stable)
        git(repo, 'reset', '--hard', base)
        append_input(repo, front)
        candidate = git(repo, 'rev-parse', 'HEAD')
        assert planner.release_tag('beta', candidate)[1] == base
        # A direct PR never runs the template full set: shared inputs and an uncertain baseline get the representative smoke.
        shared = planner.create_plan('core', stable, 'pull_request', '')
        assert shared['Tier'] == 'core' and shared['ScenarioSelection'] == 'representative' and shared['Mode'] == 'full', shared
        uncertain = planner.create_plan('core', '0' * 40, 'pull_request', '')
        assert uncertain['Tier'] == 'core' and uncertain['ScenarioSelection'] == 'representative' and uncertain['ContainerSmoke'], uncertain
    print('PASS publication baselines, explicit inputs, cancelled/failed push carry-forward, API fallback, nightly and force push')


def check_docs_scope():
    cases = [
        ('README.md', 'documentation', False, False), ('docs/foo.md', 'documentation', False, False),
        ('.agents/skills/foo/SKILL.md', 'documentation', False, False), ('skills/foo/SKILL.md', 'documentation', False, False),
        ('framework/README.md', 'documentation', False, False), ('framework/docs/README.md', 'documentation', False, False),
        ('framework/NuGet.md', 'documentation', True, False), ('framework/docs/components/email.md', 'documentation', True, False),
        ('template/docs/standards/testing.md', 'documentation', False, True),
        ('template/.agents/skills/leistd-project-workflow/SKILL.md', 'documentation', False, True),
        ('template/docs/standards/frontend-spartan.md', 'documentation', False, True),
        ('scripts/unknown.py', 'full', True, True), ('docs/executable.py', 'full', True, True),
        ('unknown.md', 'full', True, True), ('readme.md', 'full', True, True),
        ('Docs/foo.md', 'full', True, True), (' README.md', 'full', True, True), ('docs/说明.md', 'documentation', False, False),
    ]
    with candidate_fixture() as (repo, planner, base):
        for path, mode, package, generated in cases:
            git(repo, 'reset', '--hard', base)
            git(repo, 'clean', '-fd')
            if path in ('readme.md', 'Docs/foo.md'):
                assert planner.classify_inputs(base, base, [path])['unknown'] == [path]
                continue
            append_input(repo, path)
            plan = planner.create_plan('core', base, 'pull_request', '')
            assert plan['Mode'] == mode and plan['PackageDocumentation'] == package and plan['GeneratedDocumentation'] == generated, (path, plan)
            assert plan['Jobs']['frontend-gates'] == (mode == 'full' or path.endswith('frontend-spartan.md')), (path, plan)
            assert plan['DocsOnly'] == (mode == 'documentation' and not package and not generated)
        for source, target, expected in [('framework/NuGet.md', 'docs/NuGet.md', 'documentation'),
                                         ('template/frontend/src/app/app.ts', 'docs/app.ts', 'full'),
                                         ('template/Dockerfile', 'docs/Dockerfile', 'full')]:
            git(repo, 'reset', '--hard', base)
            git(repo, 'clean', '-fd')
            git(repo, 'mv', source, target)
            git(repo, 'commit', '-qm', 'move input')
            append_input(repo, 'README.md')
            plan = planner.create_plan('core', base, 'pull_request', '')
            assert plan['Mode'] == expected and source in plan['ChangedPaths'] and target in plan['ChangedPaths'], plan
            if source == 'framework/NuGet.md': assert plan['PackageDocumentation']
            if source == 'template/Dockerfile': assert plan['ContainerSmoke']
        for source, mode in [('template/frontend/src/app/app.ts', 'frontend'),
                             ('template/backend/src/CompanyName.ProjectName.Api/Program.cs', 'backend')]:
            git(repo, 'reset', '--hard', base)
            git(repo, 'clean', '-fd')
            append_input(repo, source)
            append_input(repo, 'template/docs/standards/frontend-spartan.md')
            append_input(repo, 'framework/NuGet.md')
            plan = planner.create_plan('core', base, 'pull_request', '')
            assert plan['Mode'] == mode and plan['GeneratedDocumentation'] and plan['PackageDocumentation'] and plan['Jobs']['frontend-gates']
    print('PASS documentation responsibilities, gate-read Markdown, mixed unions, path boundaries, deletions and moves')


def check_template_package_responsibilities():
    """Template source cannot change package contents; documentation unions still can."""
    sources = [('template/frontend/src/app/app.ts', 'frontend'),
               ('template/backend/src/CompanyName.ProjectName.Api/Program.cs', 'backend')]
    with candidate_fixture() as (repo, planner, base):
        scratch = repo / '.tmp'
        scratch.mkdir(exist_ok=True)
        validator = scratch / 'validate-plan.ps1'
        validator.write_text("$ErrorActionPreference = 'Stop'\n"
                             ". ./scripts/template-matrix-scenarios.ps1\n"
                             ". ./scripts/quality-validation-plan.ps1\n"
                             "$tier = (Get-Content $args[0] -Raw | ConvertFrom-Json).Tier\n"
                             "$null = Read-QualityValidationPlan $args[0] $tier\n", encoding='utf-8')

        def validate(plan, valid, error=''):
            path = scratch / 'plan.json'
            path.write_text(json.dumps(plan), encoding='utf-8')
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(validator), str(path)], cwd=repo,
                                    capture_output=True, text=True, encoding='utf-8', errors='replace')
            assert (result.returncode == 0) == valid, result.stdout + result.stderr
            if error:
                assert error in result.stderr, result.stderr

        for source, mode in sources:
            git(repo, 'reset', '--hard', base)
            append_input(repo, source)
            plan = planner.create_plan('core', base, 'pull_request', '')
            assert plan['Mode'] == mode and not plan['PackageDocumentation']
            assert not plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack']
            assert plan['ConsumerProjects'] == [] and plan['Tier'] == 'core'
            assert all(plan['Jobs'][job] for job in ('template-slices', 'template-generation'))
            # PG/OIDC run on a PR only for registered paths; these sources are not registered.
            assert not plan['Jobs']['postgresql-e2e'] and not plan['Jobs']['oidc-e2e'], plan['Jobs']
            validate(plan, True)
            invalid = json.loads(json.dumps(plan))
            invalid['Jobs']['package-consumption'] = True
            validate(invalid, False, 'Invalid template-stage responsibilities')
            invalid['Jobs']['framework-pack'] = False
            validate(invalid, False, 'Invalid quality plan responsibility set')
            append_input(repo, 'framework/NuGet.md')
            union = planner.create_plan('core', base, 'pull_request', '')
            assert union['Mode'] == mode and union['PackageDocumentation']
            assert union['Jobs']['package-consumption'] and union['Jobs']['framework-pack']
            validate(union, True)
            invalid = json.loads(json.dumps(union))
            invalid['Jobs']['package-consumption'] = False
            validate(invalid, False)

        framework_source = next((repo / 'framework/components/core').rglob('*.cs')).relative_to(repo).as_posix()
        for path in (framework_source, 'Directory.Packages.props', 'framework/Directory.Packages.props', 'VERSION',
                     'framework/build/test-package-consumption.ps1', 'template/.template.config/template.json',
                     'unknown-input.txt'):
            git(repo, 'reset', '--hard', base)
            append_input(repo, sources[0][0])
            append_input(repo, path)
            plan = planner.create_plan('core', base, 'pull_request', '')
            assert plan['Mode'] == 'full' and plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack'], path
            assert plan['Tier'] == 'core' and plan['ScenarioSelection'] == ('selector' if path == framework_source else 'representative'), (path, plan)
            validate(plan, True)
            invalid = json.loads(json.dumps(plan))
            invalid['Jobs']['package-consumption'] = False
            validate(invalid, False)
            invalid['PackageDocumentation'] = False
            validate(invalid, False)
            if plan['ScenarioSelection'] != 'representative':
                continue
            # Unproven inputs may not reuse stage trimming or a narrowed framework/consumer scope.
            for label, mutate in [('frontend-stages', lambda p: p.update(Mode='frontend')),
                                  ('core-scenario-dropped', lambda p: (p.update(Scenarios=p['Scenarios'][1:]), p.update(Slices=p['Slices'][1:]))),
                                  ('affected-tests', lambda p: p.update(FrameworkTestSelection='affected')),
                                  ('selected-consumers', lambda p: p.update(ConsumerProjects=['Leistd.Core'])),
                                  ('no-generation', lambda p: p['Jobs'].update({'template-generation': False}))]:
                invalid = json.loads(json.dumps(plan)); mutate(invalid)
                if label == 'no-generation':
                    invalid['GeneratedDocumentation'] = False
                validate(invalid, False, 'Representative smoke requires')
        git(repo, 'reset', '--hard', base)
        append_input(repo, sources[0][0])
        for event in ('workflow_dispatch', 'schedule'):
            plan = planner.create_plan('core', base, event, '')
            assert plan['Mode'] == 'full' and all(plan['Jobs'].values())
            validate(plan, True)
        git(repo, 'tag', 'v0.13.0-beta.10', base)
        plan = planner.create_plan('full', base, 'push', git(repo, 'rev-parse', 'HEAD'), release_channel='beta')
        assert plan['Mode'] == 'full' and plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack']
        publication = planner.create_plan('full', base, 'workflow_dispatch', git(repo, 'rev-parse', 'HEAD'), release_channel='beta')
        assert publication['ReleaseRequired'] and publication['Mode'] == 'full' and all(publication['Jobs'].values())
        # Publication always carries container scope, whatever the input classification says.
        assert publication['ContainerSmoke'], publication
    print('PASS template package responsibilities: frontend/backend, documentation unions, shared/unknown/publication inputs and validator counterexamples')


def check_quality_aggregation():
    workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8'))
    jobs = workflow['jobs']
    required = ['quality-plan', 'docs-sync', 'docs-sync-windows', 'frontend-gates', 'framework-pack', 'test',
                'template-slices', 'template-generation', 'package-consumption', 'postgresql-e2e', 'oidc-e2e']
    assert set(jobs['template-matrix']['needs']) == set(required) and jobs['template-matrix']['if'] == 'always()'
    assert jobs['quality-plan']['steps'][0]['with']['fetch-depth'] == 0
    assert workflow['permissions']['actions'] == 'read'
    for name in ['frontend-gates', 'framework-pack', 'test', 'template-slices', 'template-generation', 'package-consumption', 'postgresql-e2e', 'oidc-e2e']:
        assert 'quality-plan' in jobs[name]['needs']
        assert jobs[name]['if'] == ('fromJSON(needs.quality-plan.outputs.validation_plan).Jobs.test' if name == 'test' else f"fromJSON(needs.quality-plan.outputs.validation_plan).Jobs['{name}']"), name
        if name in ['template-slices', 'package-consumption', 'postgresql-e2e', 'oidc-e2e']:
            assert 'framework-pack' in jobs[name]['needs']
    for job in jobs.values():
        for step in job['steps']:
            if step.get('uses', '').startswith('actions/checkout@'):
                assert step['with']['ref'] == '${{ inputs.candidate_sha || github.sha }}'
    assert '-SkipSourcePreflight' in next(s for s in jobs['template-slices']['steps'] if s.get('id') == 'matrix')['run']
    # Playwright report/trace stays downloadable on success (flaky diagnosis), per slice, outside the receipt pattern.
    report = next(s for s in jobs['template-slices']['steps'] if s.get('with', {}).get('name', '').startswith('e2e-report-'))
    assert report['if'] == '${{ !cancelled() }}' and report['with']['name'] == 'e2e-report-${{ matrix.slice.key }}', report
    assert not report['with']['name'].startswith('template-matrix-'), report
    assert {line.split('/')[-2] for line in report['with']['path'].split()} == {'playwright-report', 'test-results'}, report
    for name in ['docs-sync', 'docs-sync-windows']:
        assert any(s.get('run') == 'pwsh scripts/check-all.ps1' for s in jobs[name]['steps'])
    # Every release responsibility proof is checked by the summary, gated by its own job responsibility.
    summary_steps = jobs['template-matrix']['steps']
    for kind, job in [('package', 'package-consumption'), ('generation', 'template-generation'),
                      ('postgresql', 'postgresql-e2e'), ('oidc', 'oidc-e2e')]:
        step = next(s for s in summary_steps if f'-Kind {kind} ' in s.get('run', ''))
        assert step['if'] == f"fromJSON(needs.quality-plan.outputs.validation_plan).Jobs['{job}']", (kind, step)
        download = summary_steps[summary_steps.index(step) - 1]
        assert download['uses'].startswith('actions/download-artifact@') and download['if'] == step['if'], (kind, download)
        assert download['with']['path'] in step['run'], (kind, download)
    for job, artifact in [('postgresql-e2e', 'postgresql-e2e-proof'), ('oidc-e2e', 'oidc-e2e-proof')]:
        upload = next(s for s in jobs[job]['steps'] if s.get('with', {}).get('name') == artifact)
        assert 'if' not in upload and upload['with']['if-no-files-found'] == 'error', upload
    # Generation runs as three modulo shards plus the control shard; each shard uploads its own proofs and
    # the summary downloads them by pattern into separate directories (same file names must not merge).
    generation = jobs['template-generation']
    assert generation['strategy']['matrix']['shard'] == ['0', '1', '2', 'control'] and not generation['strategy']['fail-fast']
    assert "--shard '${{ matrix.shard }}'" in next(s for s in generation['steps'] if 'test-template-generation.py' in s.get('run', ''))['run']
    for artifact in ('template-generation-proof', 'generated-documentation-proof'):
        upload = next(s for s in generation['steps'] if s.get('with', {}).get('name', '').startswith(artifact))
        assert upload['with']['name'] == artifact + '-${{ matrix.shard }}' and upload['with']['if-no-files-found'] == 'error', upload
        download = next(s for s in summary_steps if s.get('with', {}).get('pattern') == artifact + '-*')
        assert 'merge-multiple' not in download['with'] and 'name' not in download['with'], download
    # Tier comes from the plan: no workflow-level tier input remains, and only a direct PR may request core.
    assert workflow['env'] == {'PLAN_SCOPE': "${{ github.event_name == 'pull_request' && !inputs.candidate_sha && 'core' || 'full' }}"}
    assert 'tier' not in workflow[True]['workflow_call']['inputs'] and not workflow[True]['workflow_dispatch'], workflow[True]
    # Third-party NuGet download cache: keyed on Directory.Packages.props, Leistd entries removed after restore and before save.
    for name, job in jobs.items():
        steps = job['steps']
        for index, step in enumerate(steps):
            if not step.get('uses', '').startswith('actions/cache@'):
                continue
            assert step['with']['path'] == '~/.local/share/NuGet/http-cache' and 'Directory.Packages.props' in step['with']['key'], (name, step)
            assert "-iname '*leistd.*' -delete" in steps[index + 1]['run'], (name, steps[index + 1])
            restores = [i for i, s in enumerate(steps) if re.search(r'dotnet (?:restore|test|build)|test-template-|test-package-consumption', s.get('run', ''))]
            cleanup = [i for i, s in enumerate(steps) if s.get('name') == '保存前清除 Leistd 缓存条目']
            assert restores and index < min(restores) and len(cleanup) == 1 and cleanup[0] > max(restores) and 'if' not in steps[cleanup[0]], (name, restores, cleanup)
    script = scope_step('ci.yml', 'template-matrix', 'quality')
    with candidate_fixture() as (repo, planner, base):
        for mode, path in [('internal', 'README.md'), ('generated', 'template/docs/standards/testing.md'),
                           ('package', 'framework/NuGet.md'), ('gate-document', 'template/docs/standards/frontend-spartan.md'),
                           ('frontend', 'template/frontend/src/app/app.ts'), ('representative', '.github/workflows/ci.yml'),
                           ('release', 'README.md')]:
            git(repo, 'reset', '--hard', base)
            git(repo, 'clean', '-fd')
            append_input(repo, path)
            scope = 'full' if mode == 'release' else 'core'
            if mode == 'release':
                plan = planner.create_plan('full', base, 'schedule', git(repo, 'rev-parse', 'HEAD'), release_channel='nightly')
                assert plan['ReleaseRequired'] and plan['ContainerSmoke'] and all(plan['Jobs'].values()), plan
            else:
                plan = planner.create_plan('core', base, 'pull_request', '')
            candidate = plan['CandidateSha']
            normal = {name: dict(result='success', outputs={}) for name in required}
            for name, needed in plan['Jobs'].items(): normal[name]['result'] = 'success' if needed else 'skipped'
            normal['quality-plan']['outputs']['validation_plan'] = json.dumps(plan)
            def check(state, success, variant=script):
                code, output, error = evaluate(repo, variant, base, extra_env=dict(NEEDS_JSON=json.dumps(state), CANDIDATE_SHA=candidate, PLAN_SCOPE=scope))
                assert (code == 0) == success, (mode, error[-1200:], state)
                if success:
                    assert output == (f"dynamic={str(plan['Jobs']['template-slices']).lower()}\nframework_tests={str(plan['FrameworkTests']).lower()}"
                                      f"\ntier={plan['Tier']}")
            check(normal, True)
            for name in required:
                for bad in ['failure', 'cancelled', '', 'success' if normal[name]['result'] == 'skipped' else 'skipped']:
                    state = json.loads(json.dumps(normal)); state[name]['result'] = bad
                    check(state, False)
                state = json.loads(json.dumps(normal)); del state[name]
                check(state, False)
            for field, value in [('CandidateSha', 'b' * 40), ('Tier', 'full'), ('Tier', 'core'), ('Tier', 'pr'), ('FrameworkTests', 'false'),
                                 ('Mode', 'unknown'), ('Version', 9), ('DocsOnly', not plan['DocsOnly']),
                                 ('FrameworkTestProjects', None), ('Jobs', {}), ('Slices', [])]:
                invalid = dict(plan); invalid[field] = value
                if invalid == plan: continue
                state = json.loads(json.dumps(normal)); state['quality-plan']['outputs']['validation_plan'] = json.dumps(invalid)
                check(state, False)
            if mode == 'release':
                # A publication plan may not mark PG/OIDC not applicable or drop container scope, even with consistent job states.
                for name, mutate in [('postgresql-e2e', lambda p: p['Jobs'].update({'postgresql-e2e': False})),
                                     ('oidc-e2e', lambda p: p['Jobs'].update({'oidc-e2e': False})),
                                     ('container', lambda p: (p.update(ContainerSmoke=False), [g.update(Containers=[]) for g in p['Slices']]))]:
                    invalid = json.loads(json.dumps(plan)); mutate(invalid)
                    state = json.loads(json.dumps(normal)); state['quality-plan']['outputs']['validation_plan'] = json.dumps(invalid)
                    if name in state: state[name]['result'] = 'skipped'
                    check(state, False)
            mutated = script.replace("'quality-plan', 'docs-sync', ", "'quality-plan', ")
            assert mutated != script
            state = json.loads(json.dumps(normal)); state['docs-sync']['result'] = 'failure'
            check(state, True, mutated)
            print('PASS strict job aggregation:', mode)


TEST_PROJECTS = [
    'framework/tests/components/core/Leistd.Core.Tests/Leistd.Core.Tests.csproj',
    'framework/tests/components/email/Leistd.Email.Tests/Leistd.Email.Tests.csproj',
    'framework/tests/ddd-struct/Leistd.Ddd.Domain.Tests/Leistd.Ddd.Domain.Tests.csproj',
]


def check_framework_test_receipts():
    """Run the real test-list step with a fake dotnet, then the real aggregation check on its receipts."""
    run_step = next(s for s in yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8'))['jobs']['test']['steps']
                    if s.get('id') == 'framework_tests')
    assert '${{ needs.quality-plan.outputs.validation_plan }}' == run_step['env']['VALIDATION_PLAN'], 'test list must come from the candidate plan'
    verify = scope_step('ci.yml', 'template-matrix', 'framework_receipts')
    with tempfile.TemporaryDirectory(prefix='leistd-framework-receipts-') as directory:
        repo = Path(directory) / 'repo'
        repo.mkdir()
        git(repo, 'init', '-q')
        git(repo, 'config', 'user.email', 'scope-test@example.invalid')
        git(repo, 'config', 'user.name', 'Scope test')
        for project in TEST_PROJECTS + ['framework/tests/shared/Leistd.TestBase/Leistd.TestBase.csproj']:
            (repo / project).parent.mkdir(parents=True)
            (repo / project).write_text('<Project />\n', encoding='utf-8')
        git(repo, 'add', '-A')
        git(repo, 'commit', '-qm', 'fixture')
        candidate = git(repo, 'rev-parse', 'HEAD')
        fake_bin = Path(directory) / 'fake-bin'
        fake_bin.mkdir()
        log = Path(directory) / 'dotnet.log'
        # Fake dotnet: record the call, fail exactly the project named by FAIL_PROJECT.
        shim = fake_bin / 'dotnet'
        shim.write_text('#!/bin/sh\necho "$*" >> "' + str(log) + '"\n'
                        '[ -n "$FAIL_PROJECT" ] && [ "$2" = "$FAIL_PROJECT" ] && exit 1\nexit 0\n', encoding='utf-8')
        shim.chmod(0o755)
        receipts = repo / '.tmp/framework-test-receipts'

        def plan(projects, selection='affected', candidate_sha=candidate):
            return dict(Version=2, CandidateSha=candidate_sha, BaseSha='b' * 40, FrameworkTests=True,
                        FrameworkTestProjects=projects, FrameworkTestSelection=selection, FrameworkTestReason='fixture')

        def run_tests(value, fail=''):
            for item in receipts.glob('*.json'):
                item.unlink()
            log.unlink(missing_ok=True)
            return evaluate(repo, run_step['run'], '', extra_env={
                'VALIDATION_PLAN': json.dumps(value), 'FAIL_PROJECT': fail,
                'PATH': str(fake_bin) + os.pathsep + os.environ['PATH']})

        def aggregate(value, success, reason=''):
            code, _, error = evaluate(repo, verify, '', extra_env={
                'VALIDATION_PLAN': json.dumps(value), 'CANDIDATE_SHA': candidate})
            assert (code == 0) == success, (value, code, error)
            assert success or reason in error, (reason, error)

        selected = TEST_PROJECTS[:2]
        code, _, error = run_tests(plan(selected))
        assert code == 0, error
        assert [line.split()[1] for line in log.read_text(encoding='utf-8').splitlines()] == selected, 'step must run exactly the list'
        assert sorted(item.name for item in receipts.glob('*.json')) == ['Leistd.Core.Tests.json', 'Leistd.Email.Tests.json']
        aggregate(plan(selected), True)
        print('PASS framework tests: the list runs exactly, one receipt per project, aggregation accepts')
        # One selected project not run (no receipt) is rejected, whichever one it is.
        for project in selected:
            name = Path(project).stem + '.json'
            saved = (receipts / name).read_text(encoding='utf-8')
            (receipts / name).unlink()
            aggregate(plan(selected), False, '缺少回执')
            (receipts / name).write_text(saved, encoding='utf-8')
        # A plan that grew after the run (receipts for only part of it) is rejected too.
        aggregate(plan(TEST_PROJECTS), False, '缺少回执')
        # A receipt outside the list, a duplicate, a failure claim or another candidate/base is illegal.
        original = json.loads((receipts / 'Leistd.Core.Tests.json').read_text(encoding='utf-8-sig'))
        for field, value in [('Project', TEST_PROJECTS[2]), ('Result', 'failure'), ('CandidateSha', 'c' * 40),
                             ('BaseSha', 'd' * 40), ('Version', 2)]:
            (receipts / 'extra.json').write_text(json.dumps(dict(original, **{field: value})), encoding='utf-8')
            aggregate(plan(selected), False, '非法框架测试回执')
        (receipts / 'extra.json').write_text(json.dumps(original), encoding='utf-8')
        aggregate(plan(selected), False, '非法框架测试回执')
        (receipts / 'extra.json').unlink()
        # A failing project leaves no receipt and fails the job; aggregation also rejects what remains.
        code, _, error = run_tests(plan(selected), fail=selected[1])
        assert code != 0 and '框架测试失败' in error, error
        assert sorted(item.name for item in receipts.glob('*.json')) == ['Leistd.Core.Tests.json']
        aggregate(plan(selected), False, '缺少回执')
        # The step refuses a list from another candidate or an empty list.
        for value in (plan(selected, candidate_sha='c' * 40), plan([])):
            code, _, error = run_tests(value)
            assert code != 0 and '测试清单与当前候选不符' in error and not log.exists(), error
        # A full selection must equal every registered test project, not a self-declared subset.
        code, _, error = run_tests(plan(TEST_PROJECTS, 'all'))
        assert code == 0, error
        aggregate(plan(TEST_PROJECTS, 'all'), True)
        code, _, error = run_tests(plan(selected, 'all'))
        assert code == 0, error
        aggregate(plan(selected, 'all'), False, '全集清单')
        print('PASS framework test receipts: missing, extra, duplicate, failed, foreign candidate/base and narrowed full set rejected')


def release_step(step_id):
    data = yaml.safe_load((ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8'))
    return next(s for s in data['jobs']['release']['steps'] if s.get('id') == step_id)


def substitute(script, values):
    """Replace every ${{ expr }} with a fixture value; an unmodelled expression fails the test."""
    def replace(match):
        expression = match.group(1).strip()
        assert expression in values, ('workflow expression not modelled by the release fixture', expression)
        return values[expression]
    return re.sub(r'\$\{\{(.*?)\}\}', replace, script)


def run_pwsh(repo, script, env=None):
    output = repo / 'output.txt'
    output.unlink(missing_ok=True)
    # 发布步骤 dot-source 两条通道共用的提交解析；夹具仓库里放一份未跟踪的副本，不改变提交历史
    shared = repo / 'scripts/release-commits.ps1'
    if not shared.exists():
        shared.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / 'scripts/release-commits.ps1', shared)
    path = repo.parent / f'{repo.name}-step.ps1'
    path.write_text(script, encoding='utf-8')
    result = subprocess.run(['pwsh', '-NoProfile', '-File', str(path)], cwd=repo,
                            env={**os.environ, 'GITHUB_OUTPUT': str(output), **(env or {})},
                            capture_output=True, text=True, encoding='utf-8', errors='replace')
    return result.returncode, output.read_text(encoding='utf-8') if output.exists() else '', result.stdout + result.stderr


def parse_outputs(text):
    outputs, lines, index = {}, text.splitlines(), 0
    while index < len(lines):
        line = lines[index]
        if '<<' in line and '=' not in line.split('<<', 1)[0]:
            name, marker = line.split('<<', 1)
            end = lines.index(marker, index + 1)
            outputs[name] = '\n'.join(lines[index + 1:end])
            index = end + 1
            continue
        name, _, value = line.partition('=')
        outputs[name] = value
        index += 1
    return outputs


def packed_release_notes(repo):
    packages = list((repo / 'framework/artifacts').glob('*.nupkg'))
    assert len(packages) == 1, ('expected exactly one fixture package', packages)
    with zipfile.ZipFile(packages[0]) as package:
        nuspec = next(name for name in package.namelist() if name.endswith('.nuspec'))
        root = ElementTree.fromstring(package.read(nuspec))
    notes = [element.text for element in root.iter() if element.tag.endswith('}releaseNotes') or element.tag == 'releaseNotes']
    return notes[0] if notes else None


def check_release_links():
    """Release link policy: every channel, with and without a guide, down to the packed .nuspec."""
    repository = 'zengqinglei/leistd-net'
    repo_url = f'https://github.com/{repository}'
    links, notes, pack = release_step('links'), release_step('notes'), release_step('pack')
    channels = {
        'stable': '0.13.0',
        'beta': '0.13.0-beta.7',
        'nightly': '0.13.0-preview.20261007.12',
    }
    with tempfile.TemporaryDirectory(prefix='leistd-release-links-') as directory:
        for channel, version in channels.items():
            for has_guide in (True, False):
                tag = f'v{version}'
                repo = Path(directory) / f'{channel}-{"guide" if has_guide else "none"}'
                repo.mkdir()
                git(repo, 'init', '-q')
                git(repo, 'config', 'user.email', 'release-test@example.invalid')
                git(repo, 'config', 'user.name', 'Release test')
                project = repo / 'framework/Fixture/Fixture.csproj'
                project.parent.mkdir(parents=True)
                project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                                   '<TargetFramework>net10.0</TargetFramework><PackageId>Leistd.ReleaseFixture</PackageId>'
                                   '<Authors>fixture</Authors><Description>fixture</Description>'
                                   '</PropertyGroup></Project>\n', encoding='utf-8')
                (project.parent / 'Fixture.cs').write_text('namespace Fixture;\npublic static class Marker;\n', encoding='utf-8')
                (repo / 'framework/Leistd.Framework.slnx').write_text(
                    '<Solution>\n  <Project Path="Fixture/Fixture.csproj" />\n</Solution>\n', encoding='utf-8')
                if has_guide:
                    guide = repo / 'docs/framework/upgrades/0.13.0.md'
                    guide.parent.mkdir(parents=True)
                    guide.write_text('# guide\n', encoding='utf-8')
                git(repo, 'add', '-A')
                git(repo, 'commit', '-qm', 'fix: fixture')
                values = {
                    'github.repository': repository,
                    'steps.ch.outputs.channel': channel,
                    'steps.ver.outputs.tag': tag,
                    'steps.ver.outputs.version': version,
                    'steps.ver.outputs.baseVersion': '0.13.0',
                    'steps.ver.outputs.lastStableTag': '',
                }
                guide_url = f'{repo_url}/blob/{tag}/docs/framework/upgrades/0.13.0.md'
                expected = guide_url if has_guide else {
                    'stable': f'{repo_url}/releases/tag/{tag}',
                    'beta': f'{repo_url}/releases/tag/{tag}',
                    'nightly': f'{repo_url}/commit/{tag}',
                }[channel]

                code, output, log = run_pwsh(repo, substitute(links['run'], values))
                assert code == 0, (channel, has_guide, log)
                outputs = parse_outputs(output)
                assert outputs.get('guideUrl') == (guide_url if has_guide else ''), (channel, has_guide, outputs)
                assert outputs.get('packageReleaseNotes') == expected, (channel, has_guide, outputs)
                # 变异：改成分支链接必须被拒，证明判据读的是 tag 固定的地址
                mutated = links['run'].replace('blob/$tag/', 'blob/develop/')
                assert mutated != links['run'], 'mutation did not apply: guide URL literal changed'
                code, output, _ = run_pwsh(repo, substitute(mutated, values))
                assert not has_guide or parse_outputs(output).get('packageReleaseNotes') != expected, 'branch link not detected'

                values['steps.links.outputs.guideUrl'] = outputs['guideUrl']
                code, output, log = run_pwsh(repo, substitute(notes['run'], values))
                assert code == 0, (channel, has_guide, 'notes', log)
                body = parse_outputs(output)['content']
                assert ('## 升级指南' in body) == has_guide and (guide_url in body) == has_guide, (channel, has_guide, body)

                values['steps.links.outputs.packageReleaseNotes'] = outputs['packageReleaseNotes']
                env = {name: substitute(value, values) for name, value in pack.get('env', {}).items()}
                code, _, log = run_pwsh(repo, substitute(pack['run'], values), env)
                assert code == 0, (channel, has_guide, 'pack', log)
                packed = packed_release_notes(repo)
                assert packed == expected, (channel, has_guide, 'nuspec', packed)
                assert f'/{tag}/' in packed + '/' and '/main/' not in packed and '/develop/' not in packed, packed
                print(f'PASS release links {channel} guide={has_guide}: {packed}')

        code, _, log = run_pwsh(repo, substitute(pack['run'], values), {'PACKAGE_RELEASE_NOTES': ''})
        assert code != 0, ('pack must refuse a missing release notes link', log)
        values['steps.ch.outputs.channel'] = 'unknown'
        code, _, _ = run_pwsh(repo, substitute(links['run'], values))
        assert code != 0, 'unknown channel must fail closed'
        print('PASS release links: missing link and unknown channel fail closed')


FOOTNOTE_ANCHOR = '802b4dca012bda2e6d64f4a7939860bf950cd302'


def version_fixture(directory, name, commits):
    """A release checkout: v0.12.0 stable tag, VERSION 0.12.0, then candidate commits."""
    repo = Path(directory) / name
    repo.mkdir()
    git(repo, 'init', '-q', '-b', 'develop')
    git(repo, 'config', 'user.email', 'release-test@example.invalid')
    git(repo, 'config', 'user.name', 'Release test')
    git(repo, 'remote', 'add', 'origin', str(repo))
    (repo / 'VERSION').write_text('0.12.0', encoding='utf-8')
    git(repo, 'add', '-A')
    git(repo, 'commit', '-qm', 'chore: 发布 v0.12.0 [skip ci]')
    git(repo, 'tag', '-a', 'v0.12.0', '-m', 'Release v0.12.0')
    for index, message in enumerate(commits):
        (repo / f'change-{index}.txt').write_text(message, encoding='utf-8')
        git(repo, 'add', '-A')
        git(repo, 'commit', '-qm', message)
    return repo, git(repo, 'rev-list', '--max-parents=0', 'HEAD')


def check_release_version_bump():
    """The version step bumps from the same breaking-commit parser: a footer alone counts, a lowercase one does not."""
    step = release_step('ver')
    assert FOOTNOTE_ANCHOR in step['run'], 'footnote anchor literal changed; update the fixture'
    cases = [
        ('fix only', ['fix(settings): 修正'], 'patch'),
        ('footer only, no !', ['feat(settings): 新能力\n\nBREAKING-CHANGE: 设置键改名'], 'minor'),
        ('! with footer', ['refactor(lock)!: 改签名\n\nBREAKING CHANGE: 改用新签名'], 'minor'),
        ('lowercase footer is not a footer', ['fix: 修正\n\nbreaking change: none'], 'patch'),
    ]
    with tempfile.TemporaryDirectory(prefix='leistd-release-version-') as directory:
        for index, (name, commits, bump) in enumerate(cases):
            repo, root = version_fixture(directory, f'case-{index}', commits)
            script = substitute(step['run'].replace(FOOTNOTE_ANCHOR, root),
                                {'steps.ch.outputs.channel': 'beta', 'github.run_number': '1'})
            code, output, log = run_pwsh(repo, script)
            assert code == 0, (name, log)
            assert parse_outputs(output)['bump'] == bump, (name, output)
            print(f'PASS release version bump: {name} -> {bump}')


def check_release_notes_scopes():
    """Release notes group every section by Conventional Commits scope; footer-only commits are breaking."""
    step = release_step('notes')
    commits = ['fix: 无范围的修复', 'feat(settings): 设置新能力', 'fix(settings): 设置修复', 'feat(lock): 锁新能力',
               'docs(settings): 设置说明\n\nBREAKING CHANGE: 设置键改名', 'refactor(lock)!: 锁签名\n\nBREAKING-CHANGE: 改用新签名',
               'chore(multi-tenancy): 整理']
    with tempfile.TemporaryDirectory(prefix='leistd-release-notes-') as directory:
        repo, _ = version_fixture(directory, 'notes', commits)
        values = {'steps.ver.outputs.lastStableTag': 'v0.12.0', 'steps.ver.outputs.version': '0.13.0-beta.8',
                  'steps.ver.outputs.tag': 'v0.13.0-beta.8', 'steps.ver.outputs.baseVersion': '0.13.0',
                  'steps.links.outputs.guideUrl': '', 'steps.ch.outputs.channel': 'beta'}
        code, output, log = run_pwsh(repo, substitute(step['run'], values))
        assert code == 0, log
        body = parse_outputs(output)['content']
    expected = '\n'.join([
        '## Breaking Changes', '', '### lock', '', '- refactor(lock)!: 锁签名 — 改用新签名', '',
        '### settings', '', '- docs(settings): 设置说明 — 设置键改名', '',
        '## 新功能', '', '### lock', '', '- 锁新能力', '', '### settings', '', '- 设置新能力', '',
        '## 修复', '', '### settings', '', '- 设置修复', '', '### 未标注范围', '', '- 无范围的修复', '',
        '## 其他更新', '', '### multi-tenancy', '', '- chore(multi-tenancy): 整理', ''])
    assert expected in body, body
    print('PASS release notes: sections grouped by family scope, unscoped last, footer-only commits are breaking')


def template_step(step_id):
    data = yaml.safe_load((ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8'))
    return next(s for s in data['jobs']['template-release']['steps'] if s.get('id') == step_id)


def check_template_channel_plan():
    """Template channel decision: template-only → template release on main; framework-only → no template bump."""
    with candidate_fixture() as (repo, planner, base):
        git(repo, 'tag', 'v0.12.0', base)
        git(repo, 'tag', 'template-v0.1.0', base)
        backend = 'template/backend/src/CompanyName.ProjectName.Api/Program.cs'
        framework_source = next((repo / 'framework/components/core').rglob('*.cs')).relative_to(repo).as_posix()

        def plan_for(channel, *paths, message='input change'):
            git(repo, 'reset', '--hard', base)
            for path in paths:
                target = repo / path
                target.write_text(target.read_text(encoding='utf-8') + '\n// scope fixture\n', encoding='utf-8')
            git(repo, 'add', '.')
            git(repo, 'commit', '-qm', message)
            head = git(repo, 'rev-parse', 'HEAD')
            return planner.create_plan('full', base, 'push', head, release_channel=channel)

        plan = plan_for('stable', backend, message='feat(template): 模板新能力')
        release = plan['TemplateRelease']
        assert release == dict(Changed=True, Required=True, BaseTag='template-v0.1.0', BaseSha=base,
                               Reason='1 template commits since template-v0.1.0'), release
        assert not plan['ReleaseRequired'] and plan['Mode'] == 'full' and plan['ContainerSmoke'] and all(plan['Jobs'].values()), plan
        package = plan_for('stable', 'template-package/Leistd.Templates.csproj')
        assert package['TemplateRelease']['Required'] and not package['ReleaseRequired'], package['ReleaseReason']
        assert planner.classify_inputs(base, git(repo, 'rev-parse', 'HEAD'), ['template-package/Leistd.Templates.csproj'])['unknown']
        framework = plan_for('stable', framework_source, message='fix(core): 框架修复')
        assert framework['ReleaseRequired'] and framework['TemplateRelease'] == dict(
            Changed=False, Required=False, BaseTag='template-v0.1.0', BaseSha=base, Reason='No template changes since template-v0.1.0'), framework
        # 框架 stable 的版本回写提交改了模板默认的 LeistdFrameworkVersion，但不是模板变更
        writeback = plan_for('stable', 'VERSION', 'template/backend/Directory.Build.props', message='chore: 发布 v0.12.1 [skip ci]')
        assert not writeback['TemplateRelease']['Changed'], writeback['TemplateRelease']
        # 只改生成文档：发布模板也要求 full，不得按文档推送收窄
        docs = plan_for('stable', 'template/docs/standards/testing.md', message='docs(template): 测试规范')
        assert docs['TemplateRelease']['Required'] and docs['Mode'] == 'full' and docs['ContainerSmoke'], docs
        develop = plan_for('beta', backend)
        assert develop['TemplateRelease']['Changed'] and not develop['TemplateRelease']['Required'], develop['TemplateRelease']
        assert 'only on a push to main' in develop['TemplateRelease']['Reason'], develop['TemplateRelease']
        # 手动触发 main 也是 stable 通道，但模板只在 push main 时发布
        dispatched = planner.create_plan('full', base, 'workflow_dispatch', git(repo, 'rev-parse', 'HEAD'), release_channel='stable')
        assert dispatched['TemplateRelease']['Changed'] and not dispatched['TemplateRelease']['Required'], dispatched['TemplateRelease']
        nightly = planner.create_plan('full', base, 'schedule', git(repo, 'rev-parse', 'HEAD'), release_channel='nightly')
        assert not nightly['TemplateRelease']['Changed'] and not nightly['TemplateRelease']['Required'], nightly['TemplateRelease']
        pull = planner.create_plan('core', base, 'pull_request', '')
        assert pull['TemplateRelease'] == dict(Changed=False, Required=False, BaseTag='', BaseSha='', Reason='Not a publication candidate'), pull
        # 最高版本的 template-v tag 是基线；没有任何 template-v tag 时首版发布
        git(repo, 'tag', 'template-v0.10.0', base)
        assert plan_for('stable', backend)['TemplateRelease']['BaseTag'] == 'template-v0.10.0'
        git(repo, 'tag', '-d', 'template-v0.1.0', 'template-v0.10.0')
        first = plan_for('stable', framework_source)['TemplateRelease']
        assert first == dict(Changed=True, Required=True, BaseTag='', BaseSha='', Reason='No published template version'), first

        scratch = repo / '.tmp'
        scratch.mkdir(exist_ok=True)
        validator = scratch / 'validate-plan.ps1'
        validator.write_text("$ErrorActionPreference = 'Stop'\n. ./scripts/template-matrix-scenarios.ps1\n"
                             ". ./scripts/quality-validation-plan.ps1\n$null = Read-QualityValidationPlan $args[0] 'full'\n", encoding='utf-8')

        def validate(value):
            path = scratch / 'plan.json'
            path.write_text(json.dumps(value), encoding='utf-8')
            return subprocess.run(['pwsh', '-NoProfile', '-File', str(validator), str(path)], cwd=repo,
                                  capture_output=True, text=True, encoding='utf-8', errors='replace')
        git(repo, 'tag', 'template-v0.1.0', base)
        valid = plan_for('stable', backend)
        assert validate(valid).returncode == 0, validate(valid).stderr
        for label, mutate, error in [
                ('required on develop', lambda v: v.update(ReleaseChannel='beta'), 'Invalid template release responsibility'),
                ('required on a manual run', lambda v: v.update(Event='workflow_dispatch'), 'Invalid template release responsibility'),
                ('required without change', lambda v: v['TemplateRelease'].update(Changed=False), 'Invalid template release responsibility'),
                ('missing decision', lambda v: v.pop('TemplateRelease'), 'Invalid template release responsibility'),
                ('narrowed publication', lambda v: v['Jobs'].update({'oidc-e2e': False}), 'Publication requires full candidate validation')]:
            invalid = json.loads(json.dumps(valid))
            mutate(invalid)
            result = validate(invalid)
            assert result.returncode != 0 and error in result.stderr, (label, result.stderr)
    print('PASS template channel plan: template-only and packaging changes release the template on main only, '
          'framework-only and write-back commits do not, docs-only template publication stays full, validator counterexamples')


def template_fixture(directory, name, tag, commits):
    """A checkout with template/ and an optional template-v tag, then (message, path) commits."""
    repo = Path(directory) / name
    (repo / 'template/backend').mkdir(parents=True)
    git(repo, 'init', '-q', '-b', 'main')
    git(repo, 'config', 'user.email', 'release-test@example.invalid')
    git(repo, 'config', 'user.name', 'Release test')
    (repo / 'template/backend/Directory.Build.props').write_text(
        '<Project><PropertyGroup><LeistdFrameworkVersion>0.13.0</LeistdFrameworkVersion></PropertyGroup></Project>\n', encoding='utf-8')
    (repo / 'README.md').write_text('fixture\n', encoding='utf-8')
    git(repo, 'add', '-A')
    git(repo, 'commit', '-qm', 'chore: 初始')
    if tag:
        git(repo, 'tag', '-a', tag, '-m', tag)
    for index, (message, path) in enumerate(commits):
        target = repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        # 版本回写提交改的是 Directory.Build.props：保持合法，只换内容
        target.write_text(f'<Project><PropertyGroup><LeistdFrameworkVersion>0.13.0</LeistdFrameworkVersion></PropertyGroup><!-- {index} --></Project>\n'
                          if target.name == 'Directory.Build.props' else f'{index}\n', encoding='utf-8')
        git(repo, 'add', '-A')
        git(repo, 'commit', '-qm', message)
    return repo


def check_template_release_version():
    """The template version step: bump from template commits since the last template-v tag, same parser as the framework."""
    step = template_step('tver')
    values = {'needs.candidate.outputs.channel': 'stable', 'github.run_id': '77', 'github.run_attempt': '2', 'github.event_name': 'push'}
    t = 'template/backend/src/Api/Program.cs'
    cases = [
        ('first version', '', [('feat(template): 首版', t)], 'stable', dict(Changed=True, Required=True, BaseTag=''), '0.1.0', 'initial', True),
        ('feat', 'template-v0.1.0', [('feat(template): 新能力', t)], 'stable', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.2.0', 'minor', True),
        ('fix', 'template-v0.1.0', [('fix(template): 修复', t)], 'stable', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.1.1', 'patch', True),
        ('packaging only', 'template-v0.1.0', [('fix(template): 包元数据', 'template-package/Leistd.Templates.csproj')], 'stable',
         dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.1.1', 'patch', True),
        ('breaking in 0.x', 'template-v0.1.0', [('refactor(template)!: 参数改名\n\nBREAKING CHANGE: --x 改为 --y', t)], 'stable',
         dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.2.0', 'minor', True),
        # 框架提交与版本回写不计入模板：只有它们时没有模板变化
        ('framework feat ignored', 'template-v0.1.0', [('feat(core): 框架能力', 'framework/x.cs'), ('fix(template): 修复', t)], 'stable',
         dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.1.1', 'patch', True),
        ('write-back ignored', 'template-v0.1.0', [('fix(template): 修复', t), ('chore: 发布 v0.13.0 [skip ci]', 'template/backend/Directory.Build.props')], 'stable',
         dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '0.1.1', 'patch', True),
        ('develop validates without publishing', 'template-v0.1.0', [('feat(template): 新能力', t)], 'beta',
         dict(Changed=True, Required=False, BaseTag='template-v0.1.0'), '0.2.0-ci.77.2', 'minor', False),
        ('manual run on main validates without publishing', 'template-v0.1.0', [('feat(template): 新能力', t)], 'stable:workflow_dispatch',
         dict(Changed=True, Required=False, BaseTag='template-v0.1.0'), '0.2.0-ci.77.2', 'minor', False),
    ]
    with tempfile.TemporaryDirectory(prefix='leistd-template-version-') as directory:
        for index, (name, tag, commits, channel, decision, version, bump, publish) in enumerate(cases):
            repo = template_fixture(directory, f'case-{index}', tag, commits)
            channel, _, event = channel.partition(':')
            script = substitute(step['run'], dict(values, **{'needs.candidate.outputs.channel': channel, 'github.event_name': event or 'push'}))
            plan = json.dumps({'TemplateRelease': dict(decision, BaseSha='', Reason='fixture')})
            code, output, log = run_pwsh(repo, script, {'VALIDATION_PLAN': plan})
            assert code == 0, (name, log)
            outputs = parse_outputs(output)
            assert (outputs['version'], outputs['bump'], outputs['publish']) == (version, bump, str(publish).lower()), (name, outputs)
            assert outputs['tag'] == 'template-v' + version.split('-')[0] and outputs['frameworkVersion'] == '0.13.0', (name, outputs)
            assert outputs['sourceSha'] == git(repo, 'rev-parse', 'HEAD'), (name, outputs)
            print(f'PASS template version: {name} -> {version} publish={publish}')
        failures = [
            ('! without footer', 'template-v0.1.0', [('feat(template)!: 改参数', t)], 'stable', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), 'BREAKING CHANGE'),
            ('only framework and write-back, plan says changed', 'template-v0.1.0',
             [('feat(core): 框架能力', 'framework/x.cs'), ('chore: 发布 v0.13.0 [skip ci]', 'template/backend/Directory.Build.props')], 'stable',
             dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '质量计划不一致'),
            ('baseline differs from plan', 'template-v0.2.0', [('fix(template): 修复', t)], 'stable', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '质量计划不一致'),
            ('develop asked to publish', 'template-v0.1.0', [('fix(template): 修复', t)], 'beta', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '只有 push main 发布模板'),
            ('manual run asked to publish', 'template-v0.1.0', [('fix(template): 修复', t)], 'stable:workflow_dispatch', dict(Changed=True, Required=True, BaseTag='template-v0.1.0'), '只有 push main 发布模板'),
        ]
        for index, (name, tag, commits, channel, decision, error) in enumerate(failures):
            repo = template_fixture(directory, f'failure-{index}', tag, commits)
            channel, _, event = channel.partition(':')
            script = substitute(step['run'], dict(values, **{'needs.candidate.outputs.channel': channel, 'github.event_name': event or 'push'}))
            plan = json.dumps({'TemplateRelease': dict(decision, BaseSha='', Reason='fixture')})
            code, _, log = run_pwsh(repo, script, {'VALIDATION_PLAN': plan})
            assert code != 0 and error in log, (name, log)
            print(f'PASS template version: {name} -> rejected')


def check_template_release_notes():
    """The template Release body: install line, default framework version, template-only changes grouped by scope."""
    step = template_step('tnotes')
    t = 'template/backend/src/Api/Program.cs'
    commits = [('feat(template): 模板新能力', t), ('feat(core): 框架能力', 'framework/x.cs'), ('fix(frontend): 前端修复', 'template/frontend/a.ts'),
               ('chore: 发布 v0.13.0 [skip ci]', 'template/backend/Directory.Build.props')]
    values = {'steps.tver.outputs.lastTag': 'template-v0.1.0', 'steps.tver.outputs.tag': 'template-v0.2.0',
              'steps.tver.outputs.baseVersion': '0.2.0', 'steps.tver.outputs.frameworkVersion': '0.13.0',
              'github.repository': 'owner/repo'}
    with tempfile.TemporaryDirectory(prefix='leistd-template-notes-') as directory:
        repo = template_fixture(directory, 'notes', 'template-v0.1.0', commits)
        code, output, log = run_pwsh(repo, substitute(step['run'], values))
        assert code == 0, log
        body = parse_outputs(output)['content']
        expected = '\n'.join(['## 安装', '', '```bash', 'dotnet new install Leistd.Templates::0.2.0', 'dotnet new fullstack-app -n <项目名>', '```', '',
                              '## 默认框架版本', '', '- 生成项目的 `LeistdFrameworkVersion` 为 0.13.0，只用 nuget.org 即可还原。', '',
                              '## 变更范围', '', '- template-v0.1.0..template-v0.2.0', '',
                              '## 新功能', '', '### template', '', '- 模板新能力', '',
                              '## 修复', '', '### frontend', '', '- 前端修复', ''])
        assert body.startswith(expected) and '框架能力' not in body and '发布 v0.13.0' not in body and '升级说明' not in body, body
        guide = repo / 'docs/template/upgrades/0.2.0.md'
        guide.parent.mkdir(parents=True)
        guide.write_text('# 升级\n', encoding='utf-8')
        code, output, log = run_pwsh(repo, substitute(step['run'], values))
        assert code == 0 and '(https://github.com/owner/repo/blob/template-v0.2.0/docs/template/upgrades/0.2.0.md)' in parse_outputs(output)['content'], log
        first = template_fixture(directory, 'first', '', commits)
        code, output, log = run_pwsh(first, substitute(step['run'], dict(values, **{'steps.tver.outputs.lastTag': ''})))
        body = parse_outputs(output)['content']
        assert code == 0 and '- 首个模板版本。' in body and '## 新功能' not in body, body
    print('PASS template release notes: install line, default framework version, template-only scoped changes, guide link, first version')


def check_template_release_wiring():
    """develop/nightly never push the template; main publishes only after validation, after the framework channel."""
    data = yaml.safe_load((ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8'))
    job = data['jobs']['template-release']
    steps = job['steps']
    ids = [s.get('id') or s.get('name') for s in steps]
    assert job['needs'] == ['candidate', 'quality', 'release'], job['needs']
    for fragment in ("needs.quality.result == 'success'", "needs.release.result == 'success' || needs.release.result == 'skipped'",
                     '.TemplateRelease.Changed', ".Mode == 'full'", '.CandidateSha == needs.candidate.outputs.sha'):
        assert fragment in job['if'], (fragment, job['if'])
    assert steps[0]['with']['ref'] == '${{ needs.release.outputs.release_sha || needs.candidate.outputs.sha }}', steps[0]
    assert data['jobs']['release']['outputs']['release_sha'] == '${{ steps.tag.outputs.release_sha }}'
    runs = '\n'.join(s.get('run', '') for s in steps)
    assert len(re.findall(r'git tag -a ', runs)) == 1 and 'git push' not in runs, 'template tag created once, pushed only through push-release-ref.ps1'
    assert job['if'].startswith('!cancelled()'), job['if']
    validate = ids.index('验证模板包')
    assert 'test-template-package.ps1' in steps[validate]['run'], steps[validate]
    # nuget.org 还原只在发布时：develop 上默认框架版本之后新增的包还没有正式版
    assert "$restore = @(if ('${{ steps.tver.outputs.publish }}' -eq 'true') { '-NuGetOrgRestore' })" in steps[validate]['run'], steps[validate]
    assert steps[validate]['run'].count('-NuGetOrgRestore') == 1 and '@restore' in steps[validate]['run'], steps[validate]
    # 框架通道被跳过时没有刚推送的正式版要等索引
    assert "if ('${{ needs.release.result }}' -eq 'skipped') { $restore += @('-RestoreTimeoutMinutes', '0') }" in steps[validate]['run'], steps[validate]
    # 升级闸门：只在发布且有上一个 template-v tag 时执行（develop 每次 push 都算有变化，不在那里跑），位于包验证之后、任何发布写操作之前
    gate = ids.index('模板升级闸门')
    gate_step = steps[gate]
    gate_condition = "steps.tver.outputs.publish == 'true' && steps.tver.outputs.lastTag != ''"
    assert gate > validate and gate_step['if'] == gate_condition, gate_step
    for fragment in ('template-package/test-template-upgrade.ps1', "-replace '^template-v', ''", '-LocalFeedPath .tmp/local-feed', '-Verify Full',
                     'Leistd.Templates.${{ steps.tver.outputs.version }}.nupkg'):
        assert fragment in gate_step['run'], (fragment, gate_step['run'])
    prepare = [s for s in steps[validate:gate] if s.get('if') == gate_condition]
    assert any('pack-local-feed.ps1' in s.get('run', '') and 'playwright' in s.get('run', '') for s in prepare), prepare
    assert any(s.get('uses', '').startswith('actions/setup-node@') for s in prepare), prepare
    assert all(s.get('if') != "steps.tver.outputs.lastTag != ''" for s in steps), 'upgrade gate steps must not run on develop or manual runs'
    writes = [s for s in steps if s.get('uses', '').startswith(('NuGet/login@', 'softprops/action-gh-release@'))
              or 'nuget push' in s.get('run', '') or 'push-release-ref.ps1' in s.get('run', '') or 'git tag -a' in s.get('run', '')]
    assert len(writes) == 5, [s.get('name') for s in writes]
    for step in writes:
        assert "steps.tver.outputs.publish == 'true'" in step['if'], step
        assert steps.index(step) > gate, ('template writes only after validation and the upgrade gate', step['name'])
    release = next(s for s in steps if s.get('uses', '').startswith('softprops/action-gh-release@'))
    assert release['with']['make_latest'] is False and release['with']['prerelease'] is False, release
    print('PASS template release wiring: publish steps gated on main, after package validation, nuget.org restore and the upgrade gate, single tag creation')


def release_step_named(name):
    data = yaml.safe_load((ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8'))
    return next(s for s in data['jobs']['release']['steps'] if s.get('name') == name)


def write_hook(path, body):
    path.write_text('#!/bin/sh\n' + body, encoding='utf-8')
    path.chmod(0o755)


def count(path):
    return int(path.read_text(encoding='utf-8').strip() or 0) if path.exists() else 0


class ReleaseRemote:
    """A local bare repository standing in for GitHub; hooks inject server-side push failures."""

    def __init__(self, root):
        self.root = root
        self.remote = root / 'origin.git'
        self.work = root / 'work'
        self.counter = root / 'receive-count'
        git(root, 'init', '-q', '--bare', 'origin.git')
        self.work.mkdir()
        git(self.work, 'init', '-q', '-b', 'main')
        git(self.work, 'config', 'user.email', 'release-test@example.invalid')
        git(self.work, 'config', 'user.name', 'Release test')
        git(self.work, 'remote', 'add', 'origin', str(self.remote))
        git(self.work, 'commit', '-q', '--allow-empty', '-m', 'candidate')
        self.base = git(self.work, 'rev-parse', 'HEAD')
        git(self.work, 'push', '-q', 'origin', 'main')
        git(self.work, 'commit', '-q', '--allow-empty', '-m', 'chore: release commit')
        self.version_commit = git(self.work, 'rev-parse', 'HEAD')
        git(self.work, 'checkout', '-q', '-b', 'other', self.base)
        git(self.work, 'commit', '-q', '--allow-empty', '-m', 'someone else')
        self.other = git(self.work, 'rev-parse', 'HEAD')
        git(self.work, 'checkout', '-q', 'main')
        self.fail_pushes(0)

    def fail_pushes(self, times):
        """Count every receive; reject the first `times` the way GitHub did (remote rejected)."""
        self.counter.unlink(missing_ok=True)
        write_hook(self.remote / 'hooks/pre-receive',
                   f'n=$(cat "{self.counter}" 2>/dev/null || echo 0); n=$((n+1)); echo $n > "{self.counter}"\n'
                   f'if [ $n -le {times} ]; then echo "Internal Server Error" >&2; exit 1; fi\n')

    def fail_queries(self, times):
        """Make the first `times` ls-remote calls through `origin` fail (upload-pack is what ls-remote talks to)."""
        self.queries = self.root / 'query-count'
        self.queries.unlink(missing_ok=True)
        wrapper = self.root / 'upload-pack-wrapper.sh'
        write_hook(wrapper, f'n=$(cat "{self.queries}" 2>/dev/null || echo 0); n=$((n+1)); echo $n > "{self.queries}"\n'
                            f'if [ $n -le {times} ]; then echo "fatal: Internal Server Error" >&2; exit 128; fi\n'
                            'exec git upload-pack "$@"\n')
        git(self.work, 'config', 'remote.origin.uploadpack', str(wrapper))

    def checkout_candidate(self, name):
        """A maintainer's checkout holding only remote main (the candidate): no tags, no shared objects."""
        git(self.root, 'clone', '-q', '--no-local', '--single-branch', '--no-tags', str(self.remote), name)
        return self.root / name

    def apply_but_report_failure(self):
        """Server applies the update, then the connection fails: local git exits non-zero."""
        wrapper = self.root / 'receive-pack-wrapper.sh'
        write_hook(wrapper, 'git receive-pack "$@"\nexit 1\n')
        git(self.work, 'config', 'remote.origin.receivepack', str(wrapper))

    def remote_sha(self, ref):
        lines = git(self.work, 'ls-remote', str(self.remote), ref, f'{ref}^{{}}').splitlines()
        values = dict(reversed(line.split('\t', 1)) for line in lines)
        return values.get(f'{ref}^{{}}') or values.get(ref, '')

    def push(self, *args, attempts=3):
        output = self.root / 'push-output.txt'
        output.unlink(missing_ok=True)
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'scripts/push-release-ref.ps1'), *args,
                                 '-MaxAttempts', str(attempts), '-RetryDelaySeconds', '0'],
                                cwd=self.work, env={**os.environ, 'GITHUB_OUTPUT': str(output)},
                                capture_output=True, text=True, encoding='utf-8', errors='replace')
        outputs = parse_outputs(output.read_text(encoding='utf-8')) if output.exists() else {}
        return result.returncode, outputs.get('result'), result.stdout + result.stderr


def summary(**params):
    args = []
    for name, value in params.items():
        args += [f'-{name}', value]
    with tempfile.TemporaryDirectory(prefix='leistd-release-summary-') as directory:
        path = Path(directory) / 'summary.md'
        # 与 workflow 一样在同一会话内调用，空值作为空字符串绑定
        quoted = ' '.join(a if a.startswith('-') else "'" + a.replace("'", "''") + "'" for a in args)
        result = subprocess.run(['pwsh', '-NoProfile', '-Command',
                                 f"& '{ROOT / 'scripts/write-release-recovery-summary.ps1'}' {quoted} -SummaryPath '{path}'"],
                                capture_output=True, text=True, encoding='utf-8', errors='replace')
        assert result.returncode == 0, result.stdout + result.stderr
        text = path.read_text(encoding='utf-8')
    stage = re.match(r'<!-- release-recovery-stage: (\S+) -->', text)
    assert stage, text
    return stage.group(1), text


def run_recovery_commands(text, cwd, drop_fetch=False):
    """Execute the summary's git/push-release-ref blocks verbatim (download, NuGet and gh blocks need real services)."""
    blocks = re.findall(r'```bash\n(.*?)```', text, re.S)
    selected = [b for b in blocks if not any(s in b for s in ('gh run download', 'gh release', 'dotnet nuget push'))]
    assert selected, ('summary has no executable recovery block', text)
    lines = [line for block in selected for line in block.splitlines()
             if not (drop_fetch and line.startswith('git fetch'))]
    script = '\n'.join(line.replace('pwsh scripts/push-release-ref.ps1',
                                     f"pwsh -NoProfile -File '{ROOT / 'scripts/push-release-ref.ps1'}' -RetryDelaySeconds 0")
                        for line in lines)
    path = cwd.parent / f'{cwd.name}-recovery.sh'
    path.write_text(script + '\n', encoding='utf-8')
    result = subprocess.run(['bash', '-e', '-o', 'pipefail', str(path)], cwd=cwd, capture_output=True,
                            text=True, encoding='utf-8', errors='replace')
    return result.returncode, script + '\n---\n' + result.stdout + result.stderr


def check_release_ref_push():
    """Tag/branch push: retries only the push, queries the remote first, never rewrites a conflicting ref."""
    tag = 'v0.13.0-beta.7'
    ref = f'refs/tags/{tag}'
    cases = 0
    with tempfile.TemporaryDirectory(prefix='leistd-release-push-') as directory:
        def fresh(name):
            root = Path(directory) / name
            root.mkdir()
            return ReleaseRemote(root)

        r = fresh('retry')
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        r.fail_pushes(1)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (0, 'pushed') and count(r.counter) == 2, (code, result, log)
        assert r.remote_sha(ref) == r.base, log
        cases += 1

        r = fresh('applied')
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        r.apply_but_report_failure()
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (0, 'already-present') and count(r.counter) == 1, (code, result, log)
        cases += 1

        # 远端已有同名 tag 且解引用到同一提交（这里是轻量 tag，对象不同）：完成，不再推送
        r = fresh('present')
        git(r.work, 'push', '-q', 'origin', f'{r.base}:{ref}')
        r.fail_pushes(0)
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (0, 'already-present') and count(r.counter) == 0, (code, result, log)
        cases += 1

        r = fresh('conflict')
        git(r.work, 'push', '-q', 'origin', f'{r.other}:{ref}')
        r.fail_pushes(0)
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (2, 'conflict') and count(r.counter) == 0, (code, result, log)
        assert r.remote_sha(ref) == r.other and git(r.work, 'rev-parse', f'{tag}^{{commit}}') == r.base, log
        cases += 1

        r = fresh('local-mismatch')
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.other)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (1, 'failed') and count(r.counter) == 0 and r.remote_sha(ref) == '', (code, result, log)
        cases += 1

        r = fresh('exhausted')
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        r.fail_pushes(99)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (1, 'failed') and count(r.counter) == 3 and r.remote_sha(ref) == '', (code, result, log)
        # 最终失败的 summary 必须带本次的版本、tag、实际 SHA 与阶段
        stage, text = summary(Channel='beta', Version='0.13.0-beta.7', Tag=tag, CandidateSha=r.base, ReleaseSha=r.base,
                              Repository='owner/repo', RunId='42', ArtifactName='release-packages-0.13.0-beta.7',
                              ArtifactOutcome='success', PushOutcome='success', TagOutcome='failure', TagResult=result,
                              ReleaseOutcome='skipped')
        assert stage == 'tag', text
        assert f'-Kind tag -Name {tag} -ExpectedSha {r.base}' in text and '0.13.0-beta.7' in text, text
        assert 'dotnet nuget push' not in text and 'gh release create' in text, text
        cases += 1

        r = fresh('branch')
        code, result, log = r.push('-Kind', 'branch', '-Name', 'main', '-ExpectedSha', r.version_commit, '-BaseSha', r.base)
        assert (code, result) == (0, 'pushed') and r.remote_sha('refs/heads/main') == r.version_commit, (code, result, log)
        code, result, log = r.push('-Kind', 'branch', '-Name', 'main', '-ExpectedSha', r.version_commit, '-BaseSha', r.base)
        assert (code, result) == (0, 'already-present'), (code, result, log)
        cases += 2

        # 查询失败是"状态未知"，不是冲突：照常推送并按有限次数重试
        r = fresh('branch-query-recovers')
        r.fail_queries(1)
        r.fail_pushes(1)
        code, result, log = r.push('-Kind', 'branch', '-Name', 'main', '-ExpectedSha', r.version_commit, '-BaseSha', r.base)
        assert (code, result) == (0, 'pushed') and (count(r.queries), count(r.counter)) == (2, 2), (code, result, log)
        assert r.remote_sha('refs/heads/main') == r.version_commit, log
        cases += 1

        r = fresh('branch-query-down')
        r.fail_queries(99)
        r.fail_pushes(99)
        code, result, log = r.push('-Kind', 'branch', '-Name', 'main', '-ExpectedSha', r.version_commit, '-BaseSha', r.base)
        assert (code, result) == (1, 'failed') and (count(r.queries), count(r.counter)) == (4, 3), (code, result, log)
        assert r.remote_sha('refs/heads/main') == r.base, log
        cases += 1

        r = fresh('tag-query-down')
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.base)
        r.fail_queries(99)
        r.fail_pushes(99)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (1, 'failed') and (count(r.queries), count(r.counter)) == (4, 3), (code, result, log)
        r.fail_pushes(0)
        code, result, log = r.push('-Kind', 'tag', '-Name', tag, '-ExpectedSha', r.base)
        assert (code, result) == (0, 'pushed') and r.remote_sha(ref) == r.base, (code, result, log)
        cases += 2

        r = fresh('branch-moved')
        git(r.work, 'push', '-q', 'origin', f'{r.other}:refs/heads/main')
        r.fail_pushes(0)
        code, result, log = r.push('-Kind', 'branch', '-Name', 'main', '-ExpectedSha', r.version_commit, '-BaseSha', r.base)
        assert (code, result) == (2, 'conflict') and count(r.counter) == 0, (code, result, log)
        assert r.remote_sha('refs/heads/main') == r.other, log
        cases += 1
    print(f'PASS release ref push: {cases} cases (retry, applied-but-failed, present, conflict, local mismatch, exhausted, '
          'query failure recovered/persistent, branch)')


def check_release_recovery_summary():
    """Stage comes only from step outcomes/results; guidance matches the channel and actual SHAs."""
    cand, rel = 'a' * 40, 'b' * 40
    common = dict(Version='0.13.0', Tag='v0.13.0', CandidateSha=cand, Repository='owner/repo', RunId='42',
                  ArtifactName='release-packages-0.13.0', ArtifactOutcome='success')
    tag_cmd, branch_cmd = '-Kind tag', '-Kind branch'

    for push in ('skipped', ''):
        stage, text = summary(Channel='beta', **{**common, 'ArtifactOutcome': ''}, PushOutcome=push)
        assert stage == 'before-push', (push, text)
        assert tag_cmd not in text and 'gh release' not in text and 'dotnet nuget push' not in text, text
    stage, text = summary(Channel='', PushOutcome='')
    assert stage == 'before-push', text

    stage, text = summary(Channel='beta', **{**common, 'Version': '0.13.0-beta.7', 'Tag': 'v0.13.0-beta.7'},
                          ReleaseSha=cand, PushOutcome='failure', TagOutcome='skipped', ReleaseOutcome='skipped')
    assert stage == 'packages', text
    assert 'api.nuget.org' in text and f'-ExpectedSha {cand}' in text and '--prerelease --latest=false' in text, text

    stage, text = summary(Channel='nightly', **common, ReleaseSha=cand, PushOutcome='cancelled')
    assert stage == 'packages' and 'nuget.pkg.github.com/owner/' in text and 'gh release' not in text, text

    stage, text = summary(Channel='beta', **common, ReleaseSha=cand, PushOutcome='success',
                          TagOutcome='failure', TagResult='conflict')
    assert stage == 'tag' and tag_cmd not in text and 'force' in text, text

    # stable：tag 在版本回写提交上，main 回写单独恢复，起点是候选 SHA
    stage, text = summary(Channel='stable', **common, ReleaseSha=rel, PushOutcome='success', TagOutcome='failure',
                          TagResult='failed', BranchOutcome='skipped', ReleaseOutcome='skipped')
    assert stage == 'tag' and f'-ExpectedSha {rel}' in text and f'-BaseSha {cand}' in text, text
    assert '--latest' in text and '--prerelease' not in text, text
    stage, text = summary(Channel='stable', **common, ReleaseSha=cand, PushOutcome='success', TagOutcome='failure')
    assert stage == 'tag' and branch_cmd not in text, ('no version commit: nothing to write back', text)
    stage, text = summary(Channel='stable', **common, ReleaseSha=rel, PushOutcome='success', TagOutcome='success',
                          BranchOutcome='failure', BranchResult='failed', ReleaseOutcome='skipped')
    assert stage == 'main' and tag_cmd not in text and branch_cmd in text and 'gh release create' in text, text

    stage, text = summary(Channel='beta', **common, ReleaseSha=cand, PushOutcome='success', TagOutcome='success',
                          ReleaseOutcome='failure')
    assert stage == 'release' and tag_cmd not in text and 'dotnet nuget push' not in text and '--clobber' in text, text
    stage, text = summary(Channel='nightly', **common, ReleaseSha=cand, PushOutcome='success', TagOutcome='success',
                          ReleaseOutcome='skipped')
    assert stage == 'unknown', text
    print('PASS release recovery summary: before-push/packages/tag/main/release/unknown across stable, beta, nightly')


def check_release_artifact_rehearsal():
    """Run the real tag/artifact steps, then recover from the bundle alone: same tag object, same SHA."""
    tag_step = release_step('tag')
    files_step = release_step('release-files')
    prep_step = release_step_named('准备 stable 版本提交')
    with tempfile.TemporaryDirectory(prefix='leistd-release-artifact-') as directory:
        for channel, version in (('beta', '0.13.0-beta.7'), ('stable', '0.13.0')):
            root = Path(directory) / channel
            root.mkdir()
            r = ReleaseRemote(root)
            git(r.work, 'checkout', '-q', '--detach', r.base)
            (r.work / 'VERSION').write_text('0.12.0', encoding='utf-8')
            props = r.work / 'template/backend/Directory.Build.props'
            props.parent.mkdir(parents=True)
            props.write_text('<Project><PropertyGroup><LeistdFrameworkVersion>0.12.0</LeistdFrameworkVersion>'
                             '</PropertyGroup></Project>\n', encoding='utf-8')
            git(r.work, 'add', '-A')
            git(r.work, 'commit', '-qm', 'candidate files')
            git(r.work, 'push', '-q', 'origin', 'HEAD:refs/heads/main', '--force')
            candidate = git(r.work, 'rev-parse', 'HEAD')
            artifacts = r.work / 'framework/artifacts'
            artifacts.mkdir(parents=True)
            for package in ('Leistd.Core', 'Leistd.Ddd.Domain'):
                (artifacts / f'{package}.{version}.nupkg').write_bytes(package.encode())
            values = {
                'steps.ch.outputs.channel': channel, 'steps.ver.outputs.version': version,
                'steps.ver.outputs.tag': f'v{version}', 'needs.candidate.outputs.sha': candidate, 'github.run_id': '42',
            }
            runner_temp = root / 'runner-temp'
            runner_temp.mkdir()
            if channel == 'stable':
                code, _, log = run_pwsh(r.work, substitute(prep_step['run'], values))
                assert code == 0, log
            code, output, log = run_pwsh(r.work, substitute(tag_step['run'], values))
            assert code == 0, log
            release_sha = parse_outputs(output)['release_sha']
            assert (release_sha != candidate) == (channel == 'stable'), (channel, release_sha, candidate)
            values['steps.tag.outputs.release_sha'] = release_sha
            env = {'RUNNER_TEMP': str(runner_temp), 'RELEASE_NOTES': '## NuGet\n\nnotes'}
            code, _, log = run_pwsh(r.work, substitute(files_step['run'], values), env)
            assert code == 0, log
            out = runner_temp / 'release-artifact'
            manifest = json.loads((out / 'manifest.json').read_text(encoding='utf-8-sig'))
            assert manifest['releaseSha'] == release_sha and manifest['candidateSha'] == candidate, manifest
            assert [p['id'] for p in manifest['packages']] == ['Leistd.Core', 'Leistd.Ddd.Domain'], manifest
            assert (out / 'release-notes.md').read_text(encoding='utf-8-sig').startswith('## NuGet'), 'notes'
            tag_object = git(r.work, 'rev-parse', f'v{version}')

            # 恢复：执行 tag 阶段 summary 实际给出的命令；检出里只有候选提交，runner 本地的 tag 与版本提交已消失
            tag = f'v{version}'
            stage, text = summary(Channel=channel, Version=version, Tag=tag, CandidateSha=candidate, ReleaseSha=release_sha,
                                  Repository='owner/repo', RunId='42', ArtifactName=f'release-packages-{version}',
                                  ArtifactOutcome='success', PushOutcome='success', TagOutcome='failure',
                                  TagResult='failed', BranchOutcome='skipped', ReleaseOutcome='skipped')
            assert stage == 'tag', text
            for drop_fetch in (True, False):
                restore = r.checkout_candidate(f'restore-{"nofetch" if drop_fetch else "fetch"}')
                shutil.copytree(out, restore / f'.tmp/release-{version}')  # 代替 gh run download
                code, log = run_recovery_commands(text, restore, drop_fetch)
                if drop_fetch:
                    assert code != 0 and r.remote_sha(f'refs/tags/{tag}') == '', ('commands must need the bundle', log)
                    assert r.remote_sha('refs/heads/main') == candidate, log
                else:
                    assert code == 0, log
            assert git(restore, 'rev-parse', tag) == tag_object, 'bundle must carry the original tag object'
            assert r.remote_sha(f'refs/tags/{tag}') == release_sha
            assert r.remote_sha('refs/heads/main') == (release_sha if channel == 'stable' else candidate)
            print(f'PASS release artifact {channel}: manifest, notes and bundle restore tag {release_sha[:8]}')


def check_release_main_stage_rehearsal():
    """stable: tag landed, main write-back failed. The main-stage commands must fetch the release commit themselves."""
    with tempfile.TemporaryDirectory(prefix='leistd-release-main-') as directory:
        r = ReleaseRemote(Path(directory))
        tag = 'v0.13.0'
        git(r.work, 'tag', '-a', tag, '-m', 'Release', r.version_commit)
        git(r.work, 'push', '-q', 'origin', f'refs/tags/{tag}')
        stage, text = summary(Channel='stable', Version='0.13.0', Tag=tag, CandidateSha=r.base, ReleaseSha=r.version_commit,
                              Repository='owner/repo', RunId='42', ArtifactName='release-packages-0.13.0',
                              ArtifactOutcome='success', PushOutcome='success', TagOutcome='success',
                              BranchOutcome='failure', BranchResult='failed', ReleaseOutcome='skipped')
        assert stage == 'main', text
        code, log = run_recovery_commands(text, r.checkout_candidate('nofetch'), drop_fetch=True)
        assert code != 0 and r.remote_sha('refs/heads/main') == r.base, ('main commands must fetch the release commit', log)
        code, log = run_recovery_commands(text, r.checkout_candidate('fetch'))
        assert code == 0 and r.remote_sha('refs/heads/main') == r.version_commit, log
        stage, text = summary(Channel='stable', Version='0.13.0', Tag=tag, CandidateSha=r.base, ReleaseSha=r.version_commit,
                              PushOutcome='success', TagOutcome='success', BranchOutcome='failure', BranchResult='conflict')
        assert stage == 'main' and f'git fetch origin refs/tags/{tag}' in text and 'git merge' in text, text
    print('PASS release main stage: summary commands fetch and verify the release commit; without fetch they fail')


def check_release_workflow_wiring():
    """The workflow creates the tag once, uploads before pushing, and the summary reads only real step ids."""
    data = yaml.safe_load((ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8'))
    steps = data['jobs']['release']['steps']
    ids = [s.get('id') for s in steps]
    runs = '\n'.join(s.get('run', '') for s in steps)
    assert len(re.findall(r'git tag -a ', runs)) == 1, 'release tag must be created exactly once'
    assert 'git push' not in runs, 'all pushes go through push-release-ref.ps1'
    script_pushes = [line.strip() for line in (ROOT / 'scripts/push-release-ref.ps1').read_text(encoding='utf-8').splitlines()
                     if line.strip().startswith('git push')]
    assert script_pushes and not any(re.search(r'--force|\s-f\b|\+', line) for line in script_pushes), script_pushes
    for push in ('push-nuget', 'push-gpr'):
        assert ids.index('artifact') < ids.index(push), 'final packages must be uploaded before pushing'
    assert ids.index('tag') < ids.index('pack') and ids.index('push-tag') < ids.index('gh-release'), ids
    final = steps[-1]
    assert 'failure()' in final['if'] and 'write-release-recovery-summary.ps1' in final['run'], final
    referenced = set(re.findall(r'steps\.([\w-]+)\.', json.dumps(final)))
    assert referenced <= set(ids), ('summary references unknown step ids', referenced - set(ids))
    assert {'push-nuget', 'push-gpr', 'push-tag', 'push-main', 'gh-release', 'artifact'} <= referenced, referenced
    gh = steps[ids.index('gh-release')]
    assert gh['uses'].startswith('softprops/action-gh-release@'), gh
    # 来源证明绑定整理后的最终包，并在上传产物、推包之前完成
    assert ids.index('release-files') < ids.index('attest') < ids.index('artifact'), ids
    attest = steps[ids.index('attest')]
    assert attest['uses'] == 'actions/attest-build-provenance@v4', attest
    assert attest['with']['subject-path'] == '${{ runner.temp }}/release-artifact/packages/*.nupkg', attest
    assert gh['with']['files'] == 'framework/artifacts/*.nupkg', gh
    permissions = data['jobs']['release']['permissions']
    assert permissions.get('attestations') == 'write' and permissions.get('id-token') == 'write', permissions
    print('PASS release workflow wiring: single tag creation, provenance on final packages before upload and push, summary reads existing step ids')


def check_release_publish():
    check_release_workflow_wiring()
    check_release_ref_push()
    check_release_recovery_summary()
    check_release_artifact_rehearsal()
    check_release_main_stage_rehearsal()


def main():
    if '--release-publish' in sys.argv[1:]:
        check_release_publish()
        return
    check_existing_scopes()
    check_docs_scope()
    check_template_package_responsibilities()
    check_quality_aggregation()
    check_framework_test_receipts()
    check_release_links()
    check_release_version_bump()
    check_release_notes_scopes()
    check_template_channel_plan()
    check_template_release_version()
    check_template_release_notes()
    check_template_release_wiring()
    check_release_publish()


if __name__ == '__main__':
    main()
