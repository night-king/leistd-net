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
        # A main-only stable tag is not an ancestor of develop.
        git(repo, 'reset', '--hard', base)
        append_input(repo, 'VERSION')
        stable = git(repo, 'rev-parse', 'HEAD')
        git(repo, 'tag', 'v0.14.0', stable)
        git(repo, 'reset', '--hard', base)
        append_input(repo, front)
        candidate = git(repo, 'rev-parse', 'HEAD')
        assert planner.release_tag('beta', candidate)[1] == base
        assert planner.create_plan('pr', stable, 'pull_request', '')['Mode'] == 'full'
        assert planner.create_plan('pr', '0' * 40, 'pull_request', '')['ContainerSmoke']
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
            plan = planner.create_plan('pr', base, 'pull_request', '')
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
            plan = planner.create_plan('pr', base, 'pull_request', '')
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
            plan = planner.create_plan('pr', base, 'pull_request', '')
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
                             "$null = Read-QualityValidationPlan $args[0] pr\n", encoding='utf-8')

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
            plan = planner.create_plan('pr', base, 'pull_request', '')
            assert plan['Mode'] == mode and not plan['PackageDocumentation']
            assert not plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack']
            assert plan['ConsumerProjects'] == []
            assert all(plan['Jobs'][job] for job in ('template-slices', 'template-generation', 'postgresql-e2e', 'oidc-e2e'))
            validate(plan, True)
            invalid = json.loads(json.dumps(plan))
            invalid['Jobs']['package-consumption'] = True
            validate(invalid, False, 'Invalid template-stage responsibilities')
            invalid['Jobs']['framework-pack'] = False
            validate(invalid, False, 'Invalid quality plan responsibility set')
            append_input(repo, 'framework/NuGet.md')
            union = planner.create_plan('pr', base, 'pull_request', '')
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
            plan = planner.create_plan('pr', base, 'pull_request', '')
            assert plan['Mode'] == 'full' and plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack'], path
            validate(plan, True)
            invalid = json.loads(json.dumps(plan))
            invalid['Jobs']['package-consumption'] = False
            validate(invalid, False)
            invalid['PackageDocumentation'] = False
            validate(invalid, False)
        git(repo, 'reset', '--hard', base)
        append_input(repo, sources[0][0])
        for event in ('workflow_dispatch', 'schedule'):
            plan = planner.create_plan('pr', base, event, '')
            assert plan['Mode'] == 'full' and all(plan['Jobs'].values())
            validate(plan, True)
        git(repo, 'tag', 'v0.13.0-beta.10', base)
        plan = planner.create_plan('full', base, 'push', git(repo, 'rev-parse', 'HEAD'), release_channel='beta')
        assert plan['Mode'] == 'full' and plan['Jobs']['package-consumption'] and plan['Jobs']['framework-pack']
        publication = planner.create_plan('full', base, 'workflow_dispatch', git(repo, 'rev-parse', 'HEAD'), release_channel='beta')
        assert publication['ReleaseRequired'] and publication['Mode'] == 'full' and all(publication['Jobs'].values())
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
    for name in ['docs-sync', 'docs-sync-windows']:
        assert any(s.get('run') == 'pwsh scripts/check-all.ps1' for s in jobs[name]['steps'])
    script = scope_step('ci.yml', 'template-matrix', 'quality')
    with candidate_fixture() as (repo, planner, base):
        for mode, path in [('internal', 'README.md'), ('generated', 'template/docs/standards/testing.md'),
                           ('package', 'framework/NuGet.md'), ('gate-document', 'template/docs/standards/frontend-spartan.md'),
                           ('frontend', 'template/frontend/src/app/app.ts'), ('full', '.github/workflows/ci.yml')]:
            git(repo, 'reset', '--hard', base)
            git(repo, 'clean', '-fd')
            append_input(repo, path)
            plan = planner.create_plan('pr', base, 'pull_request', '')
            candidate = plan['CandidateSha']
            normal = {name: dict(result='success', outputs={}) for name in required}
            for name, needed in plan['Jobs'].items(): normal[name]['result'] = 'success' if needed else 'skipped'
            normal['quality-plan']['outputs']['validation_plan'] = json.dumps(plan)
            def check(state, success, variant=script):
                code, output, error = evaluate(repo, variant, base, extra_env=dict(NEEDS_JSON=json.dumps(state), CANDIDATE_SHA=candidate, MATRIX_TIER='pr'))
                assert (code == 0) == success, (mode, error[-1200:], state)
                if success:
                    assert output == f"dynamic={str(plan['Jobs']['template-slices']).lower()}\nframework_tests={str(plan['FrameworkTests']).lower()}"
            check(normal, True)
            for name in required:
                for bad in ['failure', 'cancelled', '', 'success' if normal[name]['result'] == 'skipped' else 'skipped']:
                    state = json.loads(json.dumps(normal)); state[name]['result'] = bad
                    check(state, False)
                state = json.loads(json.dumps(normal)); del state[name]
                check(state, False)
            for field, value in [('CandidateSha', 'b' * 40), ('Tier', 'full'), ('FrameworkTests', 'false'),
                                 ('Mode', 'unknown'), ('Version', 9), ('DocsOnly', not plan['DocsOnly']),
                                 ('FrameworkTestProjects', None), ('Jobs', {}), ('Slices', [])]:
                invalid = dict(plan); invalid[field] = value
                if invalid == plan: continue
                state = json.loads(json.dumps(normal)); state['quality-plan']['outputs']['validation_plan'] = json.dumps(invalid)
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
    print('PASS release workflow wiring: single tag creation, upload before push, summary reads existing step ids')


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
    check_release_publish()


if __name__ == '__main__':
    main()
