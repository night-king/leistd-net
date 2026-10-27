#!/usr/bin/env python3
"""Maintain selection/receipt contracts with real Git inputs and template generation.

Not a per-feature gate. Logs and generation evidence stay under .tmp.
"""
import argparse
import hashlib
import io
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
import sys
import zipfile
from unittest import mock

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]
# Receipts and the checker must agree with framework-pack's candidate.json version.
CANDIDATE_VERSION = '0.12.1-ci.1.1'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / '.tmp/quality-plan-evidence')
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    records = []

    def run(label, command, cwd, success=True):
        started = time.monotonic()
        result = subprocess.run(command, cwd=cwd, text=True, encoding='utf-8', errors='replace', capture_output=True)
        (out / (label + '.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
        records.append(dict(label=label, seconds=round(time.monotonic() - started, 3), exit_code=result.returncode))
        (out / 'results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
        assert (result.returncode == 0) == success, (label, result.stdout[-1000:], result.stderr[-1500:])
        return result.stdout

    with tempfile.TemporaryDirectory(prefix='leistd-plan-') as directory:
        repo = Path(directory) / 'repo'
        repo.mkdir()
        paths = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT).decode().split('\0')
        for path in (p for p in paths if p and not p.startswith("docs/specs/")):
            source = ROOT / path
            if source.is_file():
                target = repo / path
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        def git(*args):
            return subprocess.check_output(['git', *args], cwd=repo, text=True, encoding='utf-8', errors='replace', stderr=subprocess.PIPE).strip()
        git('init', '-q'); git('config', 'user.name', 'Quality fixture'); git('config', 'user.email', 'fixture@example.invalid')
        git('add', '.'); git('commit', '-qm', 'fixture baseline')
        base = git('rev-parse', 'HEAD')
        spec = importlib.util.spec_from_file_location('plan', repo / 'scripts/plan-quality-checks.py')
        planner = importlib.util.module_from_spec(spec); spec.loader.exec_module(planner)
        scenarios = planner.coverage.load_scenarios()
        registered = set(scenarios)
        prove_dependency_audit_inputs(planner)
        prove_release_reuse(planner, repo, base, out, run)
        core = {name for name, info in scenarios.items() if 'core' in info['Slices']}
        front = 'template/frontend/src/app/core/services/notification-service.ts'
        back = 'template/backend/src/CompanyName.ProjectName.Api/Controllers/AuthController.cs'
        fw = next(path for path in paths if path.startswith('framework/components/email/Leistd.Email.Smtp/') and path.endswith('.cs'))
        cases = [
            ('frontend-feature', [front], 'frontend'), ('backend-api', [back], 'backend'),
            ('cross-layer', [front, back], 'full'), ('frontend-lock', ['template/frontend/package-lock.json'], 'full'),
            ('template-parameters', ['template/.template.config/template.json'], 'full'),
            ('packaged-doc', ['framework/docs/components/email.md'], 'documentation'),
            ('generated-doc', ['template/docs/standards/testing.md'], 'documentation'),
            ('workflow', ['.github/workflows/ci.yml'], 'full'), ('unknown', ['unknown-input.cs'], 'full'),
            ('framework-source', [fw], 'full'),
            ('dependabot-config', ['.github/dependabot.yml'], 'documentation'),
            ('mcp-config', ['.mcp.json'], 'documentation'),
            ('localized-frontend', ['template/.template.config/localization/frontend/src/app/app.spec.ts'], 'frontend'),
            ('frontend-roles-15', ['template/frontend/src/app/features/platform/components/roles/roles.ts'], 'frontend'),
            ('backend-signing-9', ['template/backend/tests/CompanyName.ProjectName.IntegrationTests/SigningKeyRotationTests.cs'], 'backend'),
            ('backend-roles-18', ['template/backend/src/CompanyName.ProjectName.Application/Roles/AppServices/RoleAppService.cs'], 'backend'),
        ]
        for label, changed, expected_mode in cases:
            git('reset', '--hard', base)
            for path in changed:
                target = repo / path; target.parent.mkdir(parents=True, exist_ok=True)
                # No need for valid source in a classifier-only fixture; actual
                # generation uses the first two cases and their valid comments.
                target.write_text((target.read_text(encoding='utf-8') if target.exists() else '') + '\n// Quality scope fixture\n', encoding='utf-8')
            git('add', '.'); git('commit', '-qm', label)
            plan = planner.create_plan('core', base, 'pull_request', '', container_smoke=label == 'cross-layer')
            assert plan['Mode'] == expected_mode, (label, plan)
            # A direct PR is always core: unproven inputs get the representative smoke, never the template full set.
            narrowed = label not in ('frontend-lock', 'template-parameters', 'workflow', 'unknown')
            assert plan['Tier'] == 'core', (label, plan['Tier'])
            assert plan['ScenarioSelection'] == ('representative' if not narrowed else 'none' if expected_mode == 'documentation'
                                                 else 'selector'), (label, plan['ScenarioSelection'])
            assert plan['Version'] == 4 and (label != 'cross-layer' or plan['ContainerSmoke'])
            # The frontend dependency audit follows proven inputs that reach the dependency closure or the audit itself.
            audits = label in DEPENDENCY_AUDIT_CASES
            assert plan['DependencyAudit'] is audits, (label, plan['DependencyAudit'])
            assigned = [name for group in plan['Slices'] for name in group['Scenarios']]
            assert len(assigned) == len(set(assigned)) and set(assigned) == set(plan['Scenarios'])
            if plan['Tier'] == 'core':
                assert [group['Scenarios'] for group in plan['Slices']] == [[name] for name in scenarios if name in plan['Scenarios']]
            else:
                assert len(plan['Slices']) == len({scenarios[name]['Slices']['full'] for name in plan['Scenarios']})
            assert all(group['Scenarios'] for group in plan['Slices'])
            assert all(any(name in group['title'] for name in group['Scenarios']) for group in plan['Slices'])
            assert plan['Slices'] == planner.execution_slices(scenarios, plan['Scenarios'], plan['Tier'], plan['Mode'], plan['ContainerSmoke'])
            if expected_mode in ('frontend', 'backend'):
                try:
                    planner.create_plan('core', base, 'pull_request', '', container_smoke=True)
                except ValueError as error:
                    assert 'Container scope requires' in str(error)
                else:
                    raise AssertionError('Conflicting container responsibility must fail explicitly')
            if plan['ScenarioSelection'] == 'selector':
                prove_selection(repo, base, plan, scenarios, planner, git)
            local = planner.local_scenarios(base)
            assert local['Kind'] == 'local-template-scenarios' and local['HeadSha'] == git('rev-parse', 'HEAD')
            assert 'Version' not in local and 'Mode' not in local and 'CandidateSha' not in local
            if expected_mode in ('frontend', 'backend', 'full') and narrowed:
                # Code-only changes: the local recipe uses exactly the PR selector.
                assert local['Selection'] == 'selector' and local['Scenarios'] == plan['Scenarios'], (label, local)
            elif narrowed:
                assert local['Selection'] == 'selector' and set(local['Scenarios']) <= registered, (label, local)
                assert bool(local['Scenarios']) == (label == 'generated-doc'), (label, local)
            else:
                # The local recipe mirrors the PR: the same representative scenarios, not the full set.
                assert local['Selection'] == 'representative' and local['Scenarios'] == plan['Scenarios'], (label, local)
            (out / (label + '-local.json')).write_text(json.dumps(local, indent=2), encoding='utf-8')
            if expected_mode in ('frontend', 'backend') or label == 'cross-layer':
                assert plan['FrameworkTests'] is False and plan['ConsumerProjects'] == []
                assert plan['FrameworkTestProjects'] == [] and plan['FrameworkTestSelection'] == 'none'
                assert set(plan['Scenarios']) <= registered and plan['Jobs']['template-generation']
            elif expected_mode == 'documentation':
                assert not plan['FrameworkTests'] and not plan['Scenarios'] and plan['ConsumerProjects'] == []
                assert plan['PackageDocumentation'] == (label == 'packaged-doc')
                assert plan['GeneratedDocumentation'] == (label == 'generated-doc')
            elif label == 'framework-source':
                assert plan['ConsumerProjects'] == ['Leistd.Email.Smtp'] and plan['FrameworkTests']
                assert plan['FrameworkTestProjects'] == [EMAIL_TESTS] and plan['FrameworkTestSelection'] == 'affected', plan
                # Framework only: core as the downstream smoke, no shape generation, PG/OIDC not registered for email.
                assert plan['Scenarios'] == [name for name in scenarios if name in core], plan['Scenarios']
                assert not plan['Jobs']['template-generation'] and not plan['Jobs']['postgresql-e2e'] and not plan['Jobs']['oidc-e2e']
            else:
                # Representative smoke: every core scenario with full stages and containers; framework tests,
                # consumers and every job except the path-triggered PostgreSQL/OIDC stay complete.
                assert plan['ConsumerProjects'] is None and plan['FrameworkTests'] and set(plan['Scenarios']) == core, plan
                assert plan['FrameworkTestSelection'] == 'all' and plan['FrameworkTestProjects'] == planner.all_framework_tests('HEAD'), plan
                assert plan['ContainerSmoke'] and plan['GeneratedDocumentation'] and plan['PackageDocumentation'], plan
                assert all(needed for job, needed in plan['Jobs'].items() if job not in ('postgresql-e2e', 'oidc-e2e')), plan
                assert not plan['Jobs']['postgresql-e2e'] and not plan['Jobs']['oidc-e2e'], plan['Jobs']
                # The same unknown input stays full on every other trigger and publication channel.
                head = git('rev-parse', 'HEAD')
                for tier, event, candidate, channel in [('full', 'push', head, 'beta'), ('full', 'push', head, 'stable'),
                                                        ('full', 'workflow_dispatch', head, 'beta'),
                                                        ('full', 'workflow_dispatch', head, 'stable'), ('full', 'push', '', ''),
                                                        ('core', 'workflow_dispatch', '', ''), ('core', 'pull_request', head, '')]:
                    other = planner.create_plan(tier, base, event, candidate, release_channel=channel)
                    assert other['Tier'] == 'full' and other['Mode'] == 'full' and other['ScenarioSelection'] == 'all', (label, event, channel, other['Reason'])
                    assert set(other['Scenarios']) == registered and all(other['Jobs'].values()) and other['ContainerSmoke'], (label, event, channel)
                    assert other['FrameworkTestSelection'] == 'all' and other['ConsumerProjects'] is None, (label, event, channel)
                    assert other['ReleaseRequired'] == bool(channel), (label, event, channel, other['ReleaseReason'])
                    # Stable and manual runs always audit; other candidates follow the inputs.
                    expected_audit = channel == 'stable' or event == 'workflow_dispatch' or audits
                    assert other['DependencyAudit'] is expected_audit, (label, event, channel, other['DependencyAudit'])
            for tier, event, baseline, candidate in [('full','pull_request',base,''), ('core','workflow_dispatch',base,'')]:
                conservative = planner.create_plan(tier, baseline, event, candidate)
                assert conservative['Tier'] == 'full' and conservative['Mode'] == 'full' and conservative['FrameworkTests'] and conservative['ConsumerProjects'] is None
                assert set(conservative['Scenarios']) == registered and all(conservative['Jobs'].values())
                assert conservative['FrameworkTestSelection'] == 'all' and conservative['FrameworkTestProjects'] == planner.all_framework_tests('HEAD')
            # An uncertain baseline on a direct PR: representative smoke; the changed paths are unknown, so PG/OIDC run too.
            for baseline in ('0'*40, git('rev-parse','HEAD')):
                uncertain = planner.create_plan('core', baseline, 'pull_request', '')
                assert uncertain['Tier'] == 'core' and uncertain['ScenarioSelection'] == 'representative' and uncertain['Mode'] == 'full'
                assert set(uncertain['Scenarios']) == core and all(uncertain['Jobs'].values()) and uncertain['ContainerSmoke']
                assert uncertain['FrameworkTestSelection'] == 'all' and uncertain['ConsumerProjects'] is None and 'proof unavailable' in uncertain['Reason']
                assert uncertain['DependencyAudit'], 'An unproven input difference audits'
            (out / (label + '-plan.json')).write_text(json.dumps(plan, indent=2), encoding='utf-8')
            print('PASS complete-input plan:', label, flush=True)
            if label in ('frontend-feature', 'backend-api'):
                prove_generation(repo, base, plan, scenarios, out, run, git)
                prove_receipts(repo, plan, scenarios, out, run)
            elif label == 'cross-layer':
                prove_generation(repo, base, dict(plan, Mode='local-mixed'), scenarios, out, run, git)
                prove_receipts(repo, plan, scenarios, out, run)
        # Deletion, move across boundaries and later docs commit must keep the
        # original source impact. Evaluate old/new paths, never just HEAD^.
        git('reset', '--hard', base); git('rm', front); git('commit', '-qm', 'delete frontend input')
        deleted = planner.create_plan('core',base,'pull_request','')
        assert deleted['Mode'] == 'frontend' and deleted['Tier'] == 'core'
        prove_selection(repo, base, deleted, scenarios, planner, git)
        assert planner.local_scenarios(base)['Selection'] == 'selector'
        git('reset', '--hard', base); git('mv',front,'docs/moved-source.ts'); git('commit','-qm','cross boundary move')
        (repo / 'docs/later.md').write_text('later documentation', encoding='utf-8')
        git('add','.'); git('commit','-qm','later doc change')
        moved = planner.create_plan('core',base,'pull_request','')
        assert moved['Tier'] == 'core' and moved['ScenarioSelection'] == 'representative' and moved['Mode'] == 'full', moved
        assert planner.local_scenarios(base)['Selection'] == 'representative'
        git('reset', '--hard', base)
        (repo / front).write_text((repo / front).read_text(encoding='utf-8') + '\n// Uncommitted edit\n', encoding='utf-8')
        git('add', front); git('commit', '-qm', 'committed frontend input')
        assert planner.create_plan('core', base, 'pull_request', '')['Mode'] == 'frontend'
        (repo / back).write_text((repo / back).read_text(encoding='utf-8') + '\n// Uncommitted backend edit\n', encoding='utf-8')
        dirty = planner.create_plan('core', base, 'pull_request', '')
        assert dirty['Tier'] == 'full' and dirty['Mode'] == 'full' and dirty['FrameworkTests'] and dirty['ConsumerProjects'] is None and 'working tree' in dirty['Reason']
        assert dirty['FrameworkTestSelection'] == 'all' and dirty['FrameworkTestProjects'] == planner.all_framework_tests('HEAD')
        assert dirty['DependencyAudit'], 'A dirty tree cannot prove the dependency closure unchanged'
        assert planner.local_scenarios(base)['Selection'] == 'full'
        git('reset', '--hard', base)
        (repo / 'untracked-input.ts').write_text('untracked', encoding='utf-8')
        assert planner.local_scenarios(base)['Selection'] == 'full'
        (repo / 'untracked-input.ts').unlink()
        for invalid in ('', '0'*40, git('rev-parse','HEAD')):
            assert planner.local_scenarios(invalid)['Selection'] == 'representative'
        git('reset', '--hard', base)
        print('PASS deletion, cross-boundary rename and multi-commit representative fallback', flush=True)
        prove_scheduling(planner, scenarios)
        # Put each unknown rule on BOTH sides of the diff. Otherwise a changed
        # template.json alone would force full and fail to exercise this guard.
        config_path = repo / 'template/.template.config/template.json'
        for label, modifier in [('unknown-source-rule', False), ('unknown-modifier-rule', True)]:
            git('reset', '--hard', base)
            config = json.loads(config_path.read_text(encoding='utf-8'))
            target = config['sources'][0]['modifiers'][0] if modifier else config['sources'][0]
            target['include'] = ['**/*']
            config_path.write_text(json.dumps(config), encoding='utf-8')
            git('add', '.'); git('commit', '-qm', label + ' baseline')
            rule_base = git('rev-parse', 'HEAD')
            (repo / front).write_text((repo / front).read_text(encoding='utf-8') + '\n// Quality scope fixture\n', encoding='utf-8')
            git('add', '.'); git('commit', '-qm', label + ' source edit')
            guarded = planner.create_plan('core', rule_base, 'pull_request', '')
            assert guarded['Tier'] == 'core' and guarded['ScenarioSelection'] == 'representative' and guarded['Mode'] == 'full'
            assert guarded['FrameworkTests'] and guarded['ConsumerProjects'] is None and guarded['FrameworkTestSelection'] == 'all'
            assert set(guarded['Scenarios']) == core and 'proof unavailable' in guarded['Reason'] and guarded['ContainerSmoke']
            assert all(needed for job, needed in guarded['Jobs'].items() if job not in ('postgresql-e2e', 'oidc-e2e'))
            assert planner.local_scenarios(rule_base)['Selection'] == 'representative'
            (out / (label + '-plan.json')).write_text(json.dumps(guarded, indent=2), encoding='utf-8')
            print('PASS unknown engine rule representative fallback:', label, flush=True)
        prove_selector_counterexamples(repo, base, planner, scenarios, out, git)
        prove_local_products(repo, base, planner, scenarios, out, run, git)
        prove_framework_tests(repo, base, planner, out, run, git)
        prove_release_responsibilities(repo, base, planner, scenarios, out, run, git)
    print('Selection and receipt evidence:', out, flush=True)


EMAIL_TESTS = 'framework/tests/components/email/Leistd.Email.Tests/Leistd.Email.Tests.csproj'
DEPENDENCY_AUDIT_CASES = {'frontend-lock', 'template-parameters', 'workflow'}


def prove_release_reuse(planner, repo, base, out, run):
    """Exercise the production reader and real summary against an isolated merge and remote responses."""
    import yaml
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=repo, text=True).strip()
    def clone(value): return json.loads(json.dumps(value))
    target = repo / 'README.md'
    target.write_text(target.read_text() + '\nReuse fixture\n')
    git('add', '.'); git('commit', '-qm', 'fix: reuse fixture')
    source = git('rev-parse', 'HEAD')
    source_plan = planner.create_plan('full', base, 'push', source, True, 'beta')
    # A non-publication source is also valid if its actual code responsibilities are complete.
    source_plan['ReleaseRequired'] = False
    tree = git('rev-parse', 'HEAD^{tree}')
    head = git('commit-tree', tree, '-p', base, '-p', source, '-m', 'merge fixture')
    git('reset', '--hard', head)
    workflow = yaml.safe_load((repo / '.github/workflows/ci.yml').read_text())
    names = set()
    for key in ['quality-plan', 'docs-sync', 'docs-sync-windows', *source_plan['Jobs'], 'template-matrix']:
        label = workflow['jobs'][key]['name']
        if key == 'template-generation':
            names.update('quality / ' + label.replace('${{ matrix.shard }}', shard) for shard in workflow['jobs'][key]['strategy']['matrix']['shard'])
        elif key == 'template-slices':
            names.update('quality / ' + label.replace('${{ matrix.slice.title }}', group['title']) for group in source_plan['Slices'])
        else: names.add('quality / ' + label)
    assert names == planner.quality_job_names(source_plan), 'Source job names must match the real workflow'
    repository = dict(id=17, full_name='fixture/repo')
    source_run = dict(id=41, run_attempt=2, head_sha=source, head_branch='develop', event='push',
                      path='.github/workflows/release.yml', status='completed', conclusion='success',
                      repository=repository, head_repository=repository)
    good = dict(runs=[source_run], current=source_run, plan=source_plan,
                jobs=[dict(name=name, status='completed', conclusion='success') for name in sorted(names)],
                artifacts=[dict(id=73, name='quality-plan-41-2', expired=False,
                                workflow_run=dict(id=41, repository_id=17, head_repository_id=17, head_sha=source, head_branch='develop'))])
    state = clone(good)
    def api(endpoint):
        if endpoint.startswith('workflows/release.yml/runs?'):
            assert 'head_sha=' + source in endpoint and 'branch=develop' in endpoint and 'status=' not in endpoint
            key, items = 'workflow_runs', state['runs']
        elif endpoint == 'runs/41/attempts/2/jobs?per_page=100': key, items = 'jobs', state['jobs']
        elif endpoint == 'runs/41/artifacts?per_page=100': key, items = 'artifacts', state['artifacts']
        elif endpoint == 'runs/41': return state['current']
        else: raise AssertionError('Unexpected API request: ' + endpoint)
        return dict(total_count=len(items) + (1 if state.get('incomplete') == key else 0), **{key: items})
    records = []
    env = dict(GITHUB_REPOSITORY='fixture/repo', GITHUB_REF_NAME='main', GH_TOKEN='fixture-token')
    with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'actions_get', side_effect=api), \
            mock.patch.object(planner, 'actions_quality_plan', side_effect=lambda artifact: clone(state['plan'])):
        witness, reason = planner.verified_release_quality(head)
        assert witness == dict(Verified=True, RunId=41, Attempt=2, HeadSha=source, Tree=tree, PlanArtifactId=73), reason
        plan = planner.create_plan('full', base, 'push', head, release_channel='stable')
        assert plan['QualityExecution'] == 'reuse' and plan['QualityReuse'] == witness and not plan['QualityValidated'], plan
        assert plan['CandidateSha'] == head and plan['ReleaseRequired'] and plan['TemplateRelease']['Required'] and plan['DependencyAudit']
        assert plan['Jobs'] == source_plan['Jobs'] and plan['Slices'] == source_plan['Slices']
        def reject(label, mutate):
            nonlocal state
            state = clone(good); mutate(state)
            evidence, why = planner.verified_release_quality(head)
            assert evidence == planner.no_quality_reuse() and why.startswith('Full:'), (label, why, evidence)
            records.append(dict(case=label, reason=why))
        for field, value in [('status', 'in_progress'), ('conclusion', 'failure'), ('conclusion', 'cancelled'),
                             ('event', 'workflow_dispatch'), ('head_branch', 'main'), ('path', '.github/workflows/ci.yml'),
                             ('head_sha', 'f' * 40), ('head_repository', dict(id=99, full_name='fork/repo'))]:
            reject('run-' + field + '-' + str(value), lambda s, f=field, v=value: s['runs'][0].update({f: v}))
        reject('newer-failed-run', lambda s: s['runs'].append(dict(s['runs'][0], id=42, conclusion='failure')))
        reject('no-runs', lambda s: s.update(runs=[]))
        reject('attempt-changed', lambda s: s['current'].update(run_attempt=3))
        for key in ['workflow_runs', 'jobs', 'artifacts']:
            reject('pagination-' + key, lambda s, k=key: s.update(incomplete=k))
        for field, value in [('Version', 3), ('CandidateSha', head), ('Tier', 'core'), ('Mode', 'backend'),
                             ('ContainerSmoke', False), ('Scenarios', []), ('Slices', []), ('ConsumerProjects', []),
                             ('FrameworkTestSelection', 'affected'), ('FrameworkTestProjects', []), ('GeneratedDocumentation', False),
                             ('PackageDocumentation', False), ('ScenarioSelection', 'selector'), ('QualityExecution', 'reuse'),
                             ('QualityValidated', True)]:
            reject('plan-' + field, lambda s, f=field, v=value: s['plan'].update({f: v}))
        for job in source_plan['Jobs']:
            reject('plan-job-' + job, lambda s, j=job: s['plan']['Jobs'].update({j: False}))
        for name in sorted(names):
            reject('missing-job-' + name, lambda s, n=name: s.update(jobs=[j for j in s['jobs'] if j['name'] != n]))
        for conclusion in ['failure', 'cancelled', 'skipped', None]:
            reject('job-' + str(conclusion), lambda s, v=conclusion: s['jobs'][0].update(conclusion=v))
        reject('duplicate-job', lambda s: s['jobs'].append(clone(s['jobs'][0])))
        reject('artifact-missing', lambda s: s.update(artifacts=[]))
        reject('artifact-duplicate', lambda s: s['artifacts'].append(clone(s['artifacts'][0])))
        reject('artifact-expired', lambda s: s['artifacts'][0].update(expired=True))
        reject('artifact-old-attempt', lambda s: s['artifacts'][0].update(name='quality-plan-41-1'))
        for field, value in [('id', 42), ('head_sha', head), ('head_branch', 'main'), ('repository_id', 99), ('head_repository_id', 99)]:
            reject('artifact-owner-' + field, lambda s, f=field, v=value: s['artifacts'][0]['workflow_run'].update({f: v}))
        state = clone(good)
        fallback = planner.create_plan('full', base, 'push', head, release_channel='stable')
        assert fallback['QualityExecution'] == 'reuse'
        state['plan']['ContainerSmoke'] = False
        fallback = planner.create_plan('full', base, 'push', head, release_channel='stable')
        assert fallback['QualityExecution'] == 'current' and all(fallback['Jobs'].values()) and fallback['ContainerSmoke'] and fallback['DependencyAudit']
        assert fallback['CandidateSha'] == head and 'ContainerSmoke' in fallback['Reason']
        for event, channel in [('push', 'beta'), ('workflow_dispatch', 'stable')]:
            with mock.patch.object(planner, 'verified_release_quality', side_effect=AssertionError('Unexpected reuse query')):
                ordinary = planner.create_plan('full', base, event, head, release_channel=channel)
                assert ordinary['QualityExecution'] == 'current'
    for error in [OSError('unavailable'), TimeoutError('timeout'), json.JSONDecodeError('invalid JSON', '', 0)]:
        with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'actions_get', side_effect=error):
            assert not planner.verified_release_quality(head)[0]['Verified']
    with mock.patch.dict(os.environ, dict(env, GH_TOKEN='')), mock.patch.object(planner, 'urlopen', side_effect=AssertionError('No token')):
        assert 'credentials unavailable' in planner.verified_release_quality(head)[1]
    assert 'two-parent' in planner.verified_release_quality(source)[1]
    original_git = planner.git
    with mock.patch.object(planner, 'git', side_effect=lambda *a: 'f' * 40 if a == ('rev-parse', source + '^{tree}') else original_git(*a)):
        assert 'trees differ' in planner.verified_release_quality(head)[1]
    # The raw artifact reader checks the archive itself; its redirect never forwards the token to storage.
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as archive: archive.writestr('quality-plan.json', json.dumps(source_plan))
    class ArchiveResponse:
        def __enter__(self): return self
        def __exit__(self, *args): pass
        def read(self): return buffer.getvalue()
    with mock.patch.dict(os.environ, env), mock.patch.object(planner, 'build_opener') as opener:
        opener.return_value.open.return_value = ArchiveResponse()
        assert planner.actions_quality_plan(73) == source_plan
    request = planner.Request('https://api.github.com/test', headers={'Authorization': 'Bearer fixture-token'})
    redirected = planner.ArtifactRedirect().redirect_request(request, None, 302, '', {}, 'https://storage.example.invalid/signed')
    assert not redirected.has_header('Authorization')
    (out / 'release-reuse-fixtures.json').write_text(json.dumps(records, ensure_ascii=False, indent=2))
    # Execute the actual summary scripts: skipped reused jobs are required, the fresh audit must succeed.
    summary = next(s['run'] for s in workflow['jobs']['template-matrix']['steps'] if s.get('id') == 'quality')
    complete = next(s['run'] for s in workflow['jobs']['template-matrix']['steps'] if s.get('id') == 'complete')
    states = {name: dict(result='skipped', outputs={}) for name in workflow['jobs']['template-matrix']['needs']}
    states['quality-plan'] = dict(result='success', outputs=dict(validation_plan=json.dumps(plan)))
    states['dependency-audit']['result'] = 'success'
    script = out / 'reuse-summary.ps1'; script.write_text(summary)
    output = out / 'reuse-summary-output.txt'
    def summary_check(label, jobs, success):
        output.unlink(missing_ok=True)
        with mock.patch.dict(os.environ, dict(NEEDS_JSON=json.dumps(jobs), CANDIDATE_SHA=head, PLAN_SCOPE='full', GITHUB_OUTPUT=str(output))):
            run(label, ['pwsh', '-NoProfile', '-File', str(script)], repo, success)
        if success: assert output.read_text().splitlines() == ['dynamic=false', 'framework_tests=false', 'tier=full']
    summary_check('reuse-summary-valid', states, True)
    for job in states:
        invalid = clone(states); invalid[job]['result'] = 'failure' if job in ('quality-plan', 'dependency-audit') else 'success'
        summary_check('reuse-summary-reject-' + job, invalid, False)
    for field, value in [('Verified', False), ('HeadSha', head), ('Tree', 'f' * 40), ('Attempt', 0), ('PlanArtifactId', 0)]:
        invalid = clone(states); changed = clone(plan); changed['QualityReuse'][field] = value
        invalid['quality-plan']['outputs']['validation_plan'] = json.dumps(changed)
        summary_check('reuse-summary-witness-' + field, invalid, False)
    script.write_text(complete)
    with mock.patch.dict(os.environ, dict(VALIDATION_PLAN=json.dumps(plan), GITHUB_OUTPUT=str(output))):
        output.unlink(missing_ok=True); run('reuse-summary-output', ['pwsh', '-NoProfile', '-File', str(script)], repo)
        final = json.loads(output.read_text().removeprefix('validation_plan='))
        assert final['QualityValidated'] is True and final['QualityReuse'] == plan['QualityReuse']
    git('reset', '--hard', base)
    print(f'PASS same-tree reuse: {len(records)} source counterexamples, full fallback and real summary states', flush=True)


def prove_dependency_audit_inputs(planner):
    """Every path class that reaches the audited dependency closure or the audit itself; generation-free."""
    audited = ['template/.template.config/localization/frontend/package-lock.json',
               'template/.template.config/localization/frontend/package.json',
               'template/frontend/package.json', 'template/frontend/.npmrc', 'template/frontend/angular.json',
               'template/.template.config/template.json', 'scripts/audit-frontend-dependencies.ps1',
               'scripts/plan-quality-checks.py', 'scripts/quality-validation-plan.ps1', '.github/workflows/release.yml']
    unaudited = ['template/frontend/src/app/app.ts', 'template/.template.config/localization/frontend/src/app/app.spec.ts',
                 'scripts/scan-nuget-vulnerabilities.ps1', '.github/workflows/scheduled-checks.yml',
                 'template/backend/src/CompanyName.ProjectName.Api/Program.cs', 'docs/README.md']
    for path in audited:
        assert planner.dependency_audit('', 'pull_request', [path]) and planner.dependency_audit('beta', 'push', [path]), path
    for path in unaudited:
        assert not planner.dependency_audit('', 'pull_request', [path]) and not planner.dependency_audit('beta', 'push', [path]), path
        assert planner.dependency_audit('stable', 'push', [path]) and planner.dependency_audit('', 'workflow_dispatch', [path]), path
    assert planner.dependency_audit('', 'pull_request', None) and planner.dependency_audit('beta', 'push', None)
    print('PASS dependency audit inputs: closure, audit machinery, proof fallback and channel overrides', flush=True)


def prove_framework_tests(repo, base, planner, out, run, git):
    """Framework test list: changed file -> project -> reverse closure -> tests; anything unproven -> all."""
    smtp_dir = 'framework/components/email/Leistd.Email.Smtp'
    smtp = next(path for path in git('ls-files', smtp_dir).split('\n') if path.endswith('.cs'))
    core = next(path for path in git('ls-files', 'framework/components/core/Leistd.Core').split('\n') if path.endswith('.cs'))
    email_test = next(path for path in git('ls-files', posixpath_dir(EMAIL_TESTS)).split('\n') if path.endswith('.cs'))
    test_base = next(path for path in git('ls-files', 'framework/tests/shared/Leistd.TestBase').split('\n') if path.endswith('.cs'))
    everything = planner.all_framework_tests('HEAD')
    assert len(everything) > 20

    def append(path, text='\n// Framework selection fixture\n'):
        target = repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text((target.read_text(encoding='utf-8') if target.exists() else '') + text, encoding='utf-8')

    def edit(path, old, new):
        target = repo / path
        content = target.read_text(encoding='utf-8')
        assert old in content, (path, old)
        target.write_text(content.replace(old, new, 1), encoding='utf-8')

    def select(label, change, prepare=None):
        """prepare runs in its own commit, so the guard holds on BOTH sides of the diff."""
        git('reset', '--hard', base)
        case_base = base
        if prepare:
            prepare(); git('add', '-A'); git('commit', '-qm', label + ' baseline')
            case_base = git('rev-parse', 'HEAD')
        change(); git('add', '-A'); git('commit', '-qm', label)
        plan = planner.create_plan('core', case_base, 'pull_request', '')
        assert plan['BaseSha'] == case_base and plan['CandidateSha'] == git('rev-parse', 'HEAD'), plan
        assert plan['FrameworkTests'] and plan['FrameworkTestProjects'], plan
        (out / f'framework-{label}-plan.json').write_text(json.dumps(plan, indent=2), encoding='utf-8')
        return plan

    def expect_all(label, change, reason, prepare=None):
        plan = select(label, change, prepare)
        assert plan['FrameworkTestSelection'] == 'all' and plan['FrameworkTestProjects'] == planner.all_framework_tests('HEAD'), (label, plan)
        assert reason in (plan['FrameworkTestReason'] + plan['Reason']) or plan['Inputs'].get('unknown'), (label, reason, plan['FrameworkTestReason'])
        print('PASS framework tests fall back to all:', label, flush=True)

    single = select('single-family', lambda: append(smtp))
    assert single['FrameworkTestSelection'] == 'affected' and single['FrameworkTestProjects'] == [EMAIL_TESTS], single
    many = select('core-many-dependents', lambda: append(core))
    families = {path.split('/')[3] for path in many['FrameworkTestProjects']}
    assert many['FrameworkTestSelection'] == 'affected' and len(families) >= 5, many
    assert EMAIL_TESTS in many['FrameworkTestProjects'] and len(many['FrameworkTestProjects']) < len(everything), many
    own = select('test-project-itself', lambda: append(email_test))
    assert own['FrameworkTestSelection'] == 'affected' and own['FrameworkTestProjects'] == [EMAIL_TESTS], own
    # Mapped edits combine: the union of both closures, still bound to this base.
    both = select('two-projects-one-test', lambda: (append(smtp), append(email_test)))
    assert both['FrameworkTestProjects'] == [EMAIL_TESTS]
    print('PASS framework tests: single family, Core reverse closure, test project itself', flush=True)
    local_file = out / 'framework-local.json'
    run('framework-local-cli', [sys.executable, 'scripts/plan-quality-checks.py', '--local-framework-tests', '--tier', 'core',
                                '--base', both['BaseSha'], '--output', str(local_file)], repo)
    local = json.loads(local_file.read_text(encoding='utf-8'))
    assert local['Kind'] == 'local-framework-tests' and local['Projects'] == both['FrameworkTestProjects'] and local['Selection'] == 'affected'
    run('framework-local-both-modes', [sys.executable, 'scripts/plan-quality-checks.py', '--local-framework-tests', '--local-scenarios',
                                       '--tier', 'core', '--base', base, '--output', str(local_file)], repo, success=False)

    for label, path in [('shared-tests-props', 'framework/tests/Directory.Build.props'), ('shared-common-props', 'framework/common.props'),
                        ('shared-packages', 'framework/Directory.Packages.props'), ('shared-build-script', 'framework/build/pack-local-feed.ps1'),
                        ('shared-test-base', test_base), ('root-build-targets', 'Directory.Build.targets')]:
        expect_all(label, lambda path=path: append(path, '\n<!-- fixture -->\n' if path.endswith(('.props', '.targets')) else '\n# fixture\n'),
                   'shared')
    for label, paths in [('unmappable-family-file', ['framework/components/email/notes.txt']),
                         ('mixed-with-template', [smtp, 'template/backend/src/CompanyName.ProjectName.Api/Program.cs'])]:
        expect_all(label, lambda paths=paths: [append(path) for path in paths], 'belongs to no framework project')
    smtp_project = f'{smtp_dir}/Leistd.Email.Smtp.csproj'
    renamed_dir = 'framework/components/email/Leistd.Email.Smtp2'
    expect_all('project-rename', lambda: (git('mv', smtp_dir, renamed_dir),
                                          git('mv', f'{renamed_dir}/Leistd.Email.Smtp.csproj', f'{renamed_dir}/Leistd.Email.Smtp2.csproj')),
               'missing project')
    expect_all('project-delete', lambda: git('rm', '-rq', posixpath_dir(EMAIL_TESTS)), 'added, removed or renamed')
    expect_all('reference-added', lambda: edit(EMAIL_TESTS, '<ProjectReference ',
               '<ProjectReference Include="..\\..\\..\\..\\components\\core\\Leistd.Core\\Leistd.Core.csproj" />\n    <ProjectReference '),
               'added, removed or renamed')
    smtp_reference = 'Include="..\\..\\..\\..\\components\\email\\Leistd.Email.Smtp\\Leistd.Email.Smtp.csproj"'
    expect_all('unresolvable-reference', lambda: append(smtp), 'unresolvable',
               prepare=lambda: edit(EMAIL_TESTS, smtp_reference, 'Include="$(LeistdRoot)\\Leistd.Email.Smtp.csproj"'))
    expect_all('missing-referenced-project', lambda: append(smtp), 'missing project',
               prepare=lambda: edit(EMAIL_TESTS, smtp_reference, smtp_reference.replace('Smtp.csproj', 'Missing.csproj')))
    expect_all('unresolvable-import', lambda: append(smtp), 'Import',
               prepare=lambda: edit(EMAIL_TESTS, '<ItemGroup>', '<Import Project="shared.targets" />\n  <ItemGroup>'))
    expect_all('linked-compile-input', lambda: append(smtp), 'compile input',
               prepare=lambda: edit(smtp_project, '</Project>', '<ItemGroup><Compile Include="..\\Leistd.Email.Core\\Shared.cs" /></ItemGroup>\n</Project>'))
    expect_all('unmodelled-props', lambda: append(smtp), 'unmodelled MSBuild import',
               prepare=lambda: append('framework/components/email/Directory.Build.props', '<Project />\n'))
    expect_all('empty-selection', lambda: append(smtp), 'no test project',
               prepare=lambda: edit(EMAIL_TESTS, f'<ProjectReference {smtp_reference} />', ''))
    git('reset', '--hard', base)


def posixpath_dir(path):
    return path.rsplit('/', 1)[0]


def scenario_values(planner, config, scenarios):
    return {name: planner.coverage.symbol_values(config, planner.coverage.parse_cli_arguments(config, info['Arguments']))
            for name, info in scenarios.items()}


def prove_selection(repo, base, plan, scenarios, planner, git):
    """Re-derive the PR guarantee line by line (line_contexts, not the selector's block walk).

    Every changed content line, on both trees, is generated by a selected scenario whenever an
    eligible registered scenario generates it; core consumers of each changed source are selected.
    """
    coverage = planner.coverage
    config = json.loads(git('show', f"{plan['CandidateSha']}:template/.template.config/template.json"))
    values = scenario_values(planner, config, scenarios)
    eligible = {name for name, info in scenarios.items() if plan['Mode'] != 'frontend' or info['Frontend']}
    selected = set(plan['Scenarios'])
    core = {name for name, info in scenarios.items() if 'core' in info['Slices']}
    for path in plan['Inputs']['frontend'] + plan['Inputs']['backend']:
        matches = [(condition, coverage.file_conditions(relative, modifiers)) for relative, _, condition, modifiers in coverage.iter_sources(config, [path])]
        def producers(frames):
            return {name for name, value in values.items() if name in eligible and
                    any(coverage.generated(value, frames, condition, excludes) for condition, excludes in matches)}
        whole = producers(())
        assert not whole or whole & selected, ('changed file never generated', path)
        if not plan['Inputs']['framework']:
            assert core & whole <= selected, ('core consumer omitted', path)
        old_lines, new_lines = planner.changed_lines(base, plan['CandidateSha'], path)
        for sha, lines in ((base, old_lines), (plan['CandidateSha'], new_lines)):
            text = planner.source_text(sha, path)
            if not text:
                continue
            for lineno, frames in coverage.line_contexts(text):
                if lineno in lines and frames:
                    owners = producers(frames)
                    assert not owners or owners & selected, ('changed branch never generated', path, sha[:7], lineno, sorted(owners))


def prove_selector_counterexamples(repo, base, planner, scenarios, out, git):
    """Nested leaf, elif arm, computed symbol, trimmed side, delete/rename and template.json changes."""
    git('reset', '--hard', base); git('clean', '-fdq')
    config = json.loads((repo / 'template/.template.config/template.json').read_text(encoding='utf-8'))
    values = scenario_values(planner, config, scenarios)
    core = [name for name, info in scenarios.items() if 'core' in info['Slices']]
    app = 'template/frontend/src/app/app.ts'
    def commit(label, change):
        git('reset', '--hard', base); git('clean', '-fdq')
        change(); git('add', '-A'); git('commit', '-qm', label)
        plan = planner.create_plan('core', base, 'pull_request', '')
        (out / f'selector-{label}-plan.json').write_text(json.dumps(plan, indent=2), encoding='utf-8')
        return plan
    def append(path, text):
        target = repo / path
        target.write_text(target.read_text(encoding='utf-8') + text, encoding='utf-8')
    def has(plan, predicate):
        return [name for name in plan['Scenarios'] if predicate(values[name])]
    def none_in_core(predicate):
        assert not any(predicate(values[name]) for name in core), 'fixture must need a scenario outside core'
    # 1. Nested leaf: only the inner arm needs notifications without localization.
    leaf = lambda v: v['SpaFrontend'] and v['IncludeNotifications'] and not v['IncludeLocalization']
    none_in_core(leaf)
    plan = commit('nested-leaf', lambda: append(app, '\n//#if (IncludeNotifications)\n//#if (!IncludeLocalization)\n// nested leaf\n//#endif\n//#endif\n'))
    assert plan['Tier'] == 'core' and plan['Mode'] == 'frontend' and has(plan, leaf), plan['Scenarios']
    prove_selection(repo, base, plan, scenarios, planner, git)
    # 2. Each elif arm: a Resource frontend and a Standalone with localization, neither in core.
    resource = lambda v: v['SpaFrontend'] and v['ServiceRole'] == 'Resource'
    standalone = lambda v: v['SpaFrontend'] and v['ServiceRole'] == 'Standalone' and v['IncludeLocalization']
    none_in_core(resource); none_in_core(standalone)
    plan = commit('elif-arms', lambda: append(app, '\n//#if (ServiceRole == "Identity")\n// a\n'
                                              '//#elif (ServiceRole == "Resource")\n// b\n//#elif (IncludeLocalization)\n// c\n//#endif\n'))
    assert has(plan, resource) and has(plan, standalone), plan['Scenarios']
    prove_selection(repo, base, plan, scenarios, planner, git)
    # 3. A computed symbol in the changed branch is evaluated, not read as an unknown name.
    computed = lambda v: v['SpaFrontend'] and v['ResourceBrowserSession'] and v['IncludeNotifications']
    none_in_core(computed)
    plan = commit('computed-branch', lambda: append(app, '\n//#if (ResourceBrowserSession && IncludeNotifications)\n// computed\n//#endif\n'))
    assert has(plan, computed), plan['Scenarios']
    # A computed change in template.json affects files nobody edited: no branch is provable, core only.
    def change_computed():
        data = json.loads((repo / 'template/.template.config/template.json').read_text(encoding='utf-8'))
        data['symbols']['Impersonation']['value'] = '(LocalIdentity && IncludeMultiTenancy)'
        (repo / 'template/.template.config/template.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
        append(app, '\n// unrelated edit\n')
    plan = commit('computed-changed', change_computed)
    assert plan['Tier'] == 'core' and plan['ScenarioSelection'] == 'representative' and set(plan['Scenarios']) == set(core), plan['Reason']
    assert plan['Mode'] == 'full' and plan['ContainerSmoke'] and plan['FrameworkTestSelection'] == 'all', plan
    # An unknown input next to a provable branch: core plus the branch supplement, with full stages.
    plan = commit('unknown-with-branch', lambda: (append(app, '\n//#if (IncludeNotifications)\n//#if (!IncludeLocalization)\n// nested leaf\n//#endif\n//#endif\n'),
                                                   (repo / 'unknown-input.txt').write_text('unknown', encoding='utf-8')))
    assert plan['ScenarioSelection'] == 'representative' and plan['Mode'] == 'full' and set(core) < set(plan['Scenarios']), plan['Scenarios']
    assert has(plan, leaf), plan['Scenarios']
    prove_selection(repo, base, plan, scenarios, planner, git)
    # 4. Trimmed side: a file excluded without notifications is also built where it is absent.
    service = 'template/frontend/src/app/core/services/notification-service.ts'
    matches = [(condition, planner.coverage.file_conditions(relative, modifiers)) for relative, _, condition, modifiers in planner.coverage.iter_sources(config, [service])]
    trimmed = lambda v: v['SpaFrontend'] and not any(planner.coverage.generated(v, (), c, e) for c, e in matches)
    producers = planner.source_producers(config, service, scenarios)
    assert any(trimmed(values[name]) for name in core) and not set(core) & producers - {'identity-all-features'}
    plan = commit('trimmed-side', lambda: append(service, '\n// trimmed side\n'))
    assert has(plan, trimmed) and set(core) & producers <= set(plan['Scenarios']), plan['Scenarios']
    # 5. Delete and rename: both ends keep their producers and old branches.
    conditional = 'template/backend/src/CompanyName.ProjectName.Api/Notifications/NotificationSecurityAlertPublisher.cs'
    old_producers = planner.source_producers(config, conditional, scenarios)
    plan = commit('delete', lambda: git('rm', '-q', conditional))
    assert plan['Tier'] == 'core' and set(core) & old_producers <= set(plan['Scenarios']), plan
    prove_selection(repo, base, plan, scenarios, planner, git)
    renamed = 'template/backend/src/CompanyName.ProjectName.Api/Hosting/NotificationSecurityAlertPublisher.cs'
    plan = commit('rename', lambda: git('mv', conditional, renamed))
    new_producers = planner.source_producers(config, renamed, scenarios)
    assert conditional in plan['ChangedPaths'] and renamed in plan['ChangedPaths'], plan['ChangedPaths']
    assert set(core) & (old_producers | new_producers) <= set(plan['Scenarios']), plan['Scenarios']
    prove_selection(repo, base, plan, scenarios, planner, git)
    # 6. sources/modifiers/symbols changes: representative smoke (core only, full stages, containers).
    for label, mutate in [('modifiers', lambda data: data['sources'][0]['modifiers'][0]['exclude'].append('frontend/README.md')),
                          ('sources', lambda data: data['sources'][0]['exclude'].append('**/fixture/**')),
                          ('symbols', lambda data: data['symbols']['IncludeEmail'].update(defaultValue='true'))]:
        def change(mutate=mutate):
            data = json.loads((repo / 'template/.template.config/template.json').read_text(encoding='utf-8'))
            mutate(data)
            (repo / 'template/.template.config/template.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
        plan = commit('template-json-' + label, change)
        assert plan['Tier'] == 'core' and plan['ScenarioSelection'] == 'representative' and set(plan['Scenarios']) == set(core), (label, plan['Reason'])
        assert plan['Mode'] == 'full' and plan['ContainerSmoke'], (label, plan)
    # 7. Shared declaration: an unconditional edit only guarantees core consumers. A consumer that
    #    exists only outside core is not selected; the develop full run is where such a break shows.
    sources = planner.tree_files(base)
    consumer = next(path for path in sources if planner.backend_source(path) and
                    (owners := planner.source_producers(config, path, scenarios)) and not owners & set(core))
    consumer_owners = planner.source_producers(config, consumer, scenarios)
    shared = next(path for path in sources if planner.backend_source(path) and '#if' not in (repo / path).read_text(encoding='utf-8')
                  and planner.source_producers(config, path, scenarios) == set(scenarios))
    plan = commit('shared-declaration', lambda: append(shared, '\n// shared declaration change\n'))
    assert plan['Tier'] == 'core' and not set(plan['Scenarios']) & consumer_owners, (consumer, plan['Scenarios'])
    assert all('full' in scenarios[name]['Slices'] for name in consumer_owners)
    (out / 'selector-shared-declaration.json').write_text(json.dumps(dict(
        Detection='full-only', Shared=shared, Consumer=consumer, ConsumerScenarios=sorted(consumer_owners),
        PullRequestScenarios=plan['Scenarios']), indent=2), encoding='utf-8')
    # 8. PostgreSQL trigger covers EF Core projects of every family; a non-data project of the same family does not trigger.
    for project, expected in [('framework/components/settings/Leistd.Settings.EntityFrameworkCore', True),
                              ('framework/components/notifications/Leistd.Notifications.EntityFrameworkCore', True),
                              ('framework/components/settings/Leistd.Settings.Core', False)]:
        source = next(path for path in sources if path.startswith(project + '/') and path.endswith('.cs'))
        plan = commit('postgresql-' + project.rsplit('/', 1)[-1], lambda source=source: append(source, '\n// postgresql trigger\n'))
        assert plan['Tier'] == 'core' and plan['Jobs']['postgresql-e2e'] == expected, (project, plan['Jobs'])
    # 9. Each end-to-end script triggers its own job; OIDC also covers the generated assets S12 reads. Another script does not.
    for path, job, expected in [('scripts/test-template-oidc-e2e.ps1', 'oidc-e2e', True),
                                ('scripts/test-template-postgresql-e2e.ps1', 'postgresql-e2e', True),
                                ('scripts/test-template-postgresql-e2e.ps1', 'oidc-e2e', False),
                                ('template/deploy/docker-compose.yml', 'oidc-e2e', True),
                                ('template/backend/src/CompanyName.ProjectName.DbMigrator/Program.cs', 'oidc-e2e', True),
                                ('scripts/test-template-matrix.ps1', 'oidc-e2e', False)]:
        plan = commit('e2e-input-' + job + '-' + path.rsplit('/', 1)[-1], lambda path=path: append(path, '\n// e2e trigger\n' if path.endswith('.cs') else '\n# e2e trigger\n'))
        assert plan['Jobs'][job] == expected, (path, job, plan['Jobs'], plan['Reason'])
    git('reset', '--hard', base); git('clean', '-fdq')
    print('PASS selector counterexamples: nested leaf, elif arms, computed, trimmed side, delete/rename, template.json and unknown inputs (representative), EF Core PG trigger, e2e self and S12 inputs; shared declaration is full-only', flush=True)


def prove_local_products(repo, base, planner, scenarios, out, run, git):
    """The local recipe uses the PR selector; conditional products, snapshot races and separate CI schema."""
    git('reset', '--hard', base)
    source = 'template/frontend/src/app/app.spec.ts'
    path = repo / source
    path.write_text(path.read_text(encoding='utf-8') + '\n//#if (IncludeNotifications)\n// local branch fixture\n//#endif\n', encoding='utf-8')
    git('add', '.'); git('commit', '-qm', 'local conditional source')
    selected = planner.local_scenarios(base)
    assert selected['Selection'] == 'selector', selected
    config = json.loads((repo / 'template/.template.config/template.json').read_text(encoding='utf-8'))
    symbols = {name: planner.coverage.symbol_values(config, planner.coverage.parse_cli_arguments(config, info['Arguments']))
               for name, info in scenarios.items() if name in selected['Scenarios']}
    assert all(values['SpaFrontend'] for values in symbols.values()), selected
    assert any(values['SpaFrontend'] and values['IncludeNotifications'] for values in symbols.values())
    assert any(values['SpaFrontend'] and not values['IncludeNotifications'] for values in symbols.values())
    plan = planner.create_plan('core',base,'pull_request','')
    assert plan['Scenarios'] == selected['Scenarios']
    prove_selection(repo, base, plan, scenarios, planner, git)
    prove_generation(repo, base, dict(plan, Mode='local-conditional'), scenarios, out, run, git)
    local_file = out / 'conditional-local.json'
    run('local-cli', [sys.executable,'scripts/plan-quality-checks.py','--local-scenarios','--tier','core',
                     '--base',base,'--output',str(local_file)], repo)
    assert json.loads(local_file.read_text(encoding='utf-8')) == selected
    for flags in (['--tier','full'], ['--tier','core','--github-output'],
                  ['--tier','core','--docs-only','true'], ['--tier','core','--candidate-input','a'*40]):
        run('local-invalid-' + str(len(flags)) + '-' + flags[-1],
            [sys.executable,'scripts/plan-quality-checks.py','--local-scenarios',*flags,
             '--output',str(out/'invalid-local.json')], repo, success=False)
    run('local-not-ci-plan', ['pwsh','-NoProfile','-Command',
        "$ErrorActionPreference='Stop'; . ./scripts/quality-validation-plan.ps1; . ./scripts/template-matrix-scenarios.ps1; "
        f"Read-QualityValidationPlan -Path '{local_file}' -ExpectedTier core"], repo, success=False)
    assert 'Invalid quality plan:' in (out/'local-not-ci-plan.log').read_text(encoding='utf-8')
    # A real new commit during selection must invalidate the computed snapshot.
    original = planner.git
    fixture_head = git('rev-parse', 'HEAD')
    head_reads = 0
    def changing_head(*arguments):
        nonlocal head_reads
        if arguments == ('rev-parse', 'HEAD'):
            head_reads += 1
            if head_reads == 2:
                git('commit', '--allow-empty', '-qm', 'selection race')
        return original(*arguments)
    try:
        planner.git = changing_head
        raced = planner.local_scenarios(base)
        assert raced['Selection'] == 'full' and 'HEAD changed' in raced['Reason'], raced
    finally:
        planner.git = original
        git('reset', '--hard', fixture_head)
    original_producers = planner.source_producers
    untracked = repo / 'during-selection.cs'
    producer_calls = 0
    def changing_tree(*arguments):
        nonlocal producer_calls
        producers = original_producers(*arguments)
        producer_calls += 1
        # create_plan first evaluates old/new; inject during local selection itself.
        if producer_calls == 3:
            untracked.write_text('new input', encoding='utf-8')
        return producers
    try:
        planner.source_producers = changing_tree
        raced = planner.local_scenarios(base)
        assert raced['Selection'] == 'full' and 'working tree' in raced['Reason'], raced
    finally:
        planner.source_producers = original_producers
        untracked.unlink(missing_ok=True)
    print('PASS local conditional/mixed products, snapshot races and separate CI contract', flush=True)


def prove_generation(repo, base, plan, scenarios, out, run, git):
    """Actually generate every registered product on both sides and compare stage inputs.

    Omitted stages see no changed input in any product, and every generated file that
    changed anywhere also changed in a selected product. Unselected products may change:
    the PR only guarantees core consumers and changed branches (full covers the rest).
    Only generated UserSecretsId values are normalized (in project files and the source
    marker): the template explicitly randomizes that property at creation, while its
    XML structure stays checked.
    """
    import io
    import tarfile
    label = plan['Mode']
    old = out / (label + '-base-template')
    # Every run starts from empty hives, templates and outputs: a reused hive keeps registrations of
    # deleted fixture paths and `dotnet new --force` leaves stale files that rglob would read.
    for stale in (old, out / f'{label}-base-hive', out / f'{label}-head-hive', out / 'generated' / label):
        if stale.exists():
            shutil.rmtree(stale)
    old.mkdir(parents=True)
    data = subprocess.check_output(['git','archive',base,'template'],cwd=repo)
    # 解压过滤器（PEP 706）在 3.12 起提供，并回移到 3.10.12+、3.11.4+；
    # 缺少时明确报错，不回落到不带过滤器的旧行为。
    if not hasattr(tarfile, 'data_filter'):
        raise SystemExit('Python with tarfile extraction filters is required (3.12+, or 3.10.12+/3.11.4+)')
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        # 本地 git archive 的产物：先拒绝意外路径，再以 'data' 过滤器解压。
        assert all(member.name.startswith('template/') or member.name == 'template' for member in archive.getmembers())
        archive.extractall(old, filter='data')
    digests = {}
    for version, template in [('base', old / 'template'), ('head', repo / 'template')]:
        hive = out / f'{label}-{version}-hive'
        run(f'{label}-{version}-install',['dotnet','new','--debug:custom-hive',str(hive),'install',str(template),'--force'],repo)
        for name, info in scenarios.items():
            target = out / 'generated' / label / version / name
            run(f'{label}-{version}-{name}', ['dotnet','new','--debug:custom-hive',str(hive),'fullstack-app','-n','Quality.Scope','-o',str(target),'--force',*info['Arguments']],repo)
            files = {}
            for path in sorted(target.rglob('*')):
                if not path.is_file(): continue
                content = path.read_bytes()
                if path.suffix == '.csproj':
                    content = re.sub(rb'<UserSecretsId>[^<]+</UserSecretsId>',b'<UserSecretsId>GENERATED</UserSecretsId>',content)
                if path.relative_to(target).as_posix() == '.leistd/template.json':
                    # 来源标记同样记录这些 UserSecretsId（标记里只有它们是 GUID）
                    content = re.sub(rb'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}',b'GENERATED',content)
                files[path.relative_to(target).as_posix()] = hashlib.sha256(content).hexdigest()
            digests[version,name] = files
    omitted = 'backend/' if label == 'frontend' else ('frontend/' if label == 'backend' else None)
    selected = set(plan['Scenarios'])
    changed, selected_changed = set(), set()
    for name in {name for version,name in digests}:
        old_files, new_files = digests['base',name],digests['head',name]
        delta = {path for path in old_files.keys() | new_files.keys() if old_files.get(path) != new_files.get(path)}
        assert omitted is None or not any(path.startswith(omitted) for path in delta), (label,name,'omitted stage input changed',delta)
        changed |= delta
        if name in selected: selected_changed |= delta
    assert changed and changed <= selected_changed, (label, 'changed output never generated by a selected product', changed - selected_changed)
    (out / (label + '-generation-digests.json')).write_text(json.dumps({f'{version}/{name}': files for (version,name),files in digests.items()},indent=2), encoding='utf-8')
    print('PASS actual generation: omitted stage inputs unchanged, every changed output generated by a selected product:', label, flush=True)


def prove_receipts(repo, plan, scenarios, out, run, label=None):
    mode = plan['Mode']
    label = label or mode
    tier = plan['Tier']
    # Scenarios whose full stages run through the generated project's own verify.ps1.
    verified = set(json.loads(subprocess.check_output(['pwsh', '-NoProfile', '-Command',
        ". ./scripts/template-matrix-scenarios.ps1; @($AllScenarios | Where-Object { $scenarioMap[$_].Verify }) | ConvertTo-Json -AsArray"],
        cwd=repo, text=True, encoding='utf-8')))
    assert verified, 'no scenario registered with Verify'
    # Scenarios whose verify runs Playwright end-to-end tests (local identity products).
    end_to_end = set(json.loads(subprocess.check_output(['pwsh', '-NoProfile', '-Command',
        ". ./scripts/template-matrix-scenarios.ps1; @($AllScenarios | Where-Object { Test-ScenarioE2E $_ }) | ConvertTo-Json -AsArray"],
        cwd=repo, text=True, encoding='utf-8')))
    assert verified & end_to_end and verified - end_to_end, 'Verify fixture lacks an end-to-end or a pure API scenario'
    expected_file = out / (label + '-expected.json')
    expected_file.write_text(json.dumps(plan), encoding='utf-8')
    receipt_dir = out / (label + '-receipts')
    # 上一次运行留下的回执会被当作本次的分片，先清空
    shutil.rmtree(receipt_dir, ignore_errors=True)
    receipt_dir.mkdir()
    receipts = {}
    for group in plan['Slices']:
        receipt = receipts[group['key']] = dict(Version=3,Tier=tier,Slice=group['key'],CandidateSha=plan['CandidateSha'],
                                                CandidateVersion=CANDIDATE_VERSION,Mode=mode,Results=[])
        for name in group['Scenarios']:
            result = dict(Scenario=name, Container='pass' if name in group['Containers'] else 'skipped',
                          Verify='pass' if mode == 'full' and name in verified else 'not-run')
            for stage in ['Backend','Runtime','Lint','Frontend','Test']:
                omitted = (mode == 'frontend' and stage in ['Backend','Runtime']) or ((mode == 'backend' or not scenarios[name].get('Frontend', True)) and stage in ['Lint','Frontend','Test'])
                result[stage] = 'not-applicable' if omitted else 'pass'
            # Chromium-executed frontend test count: positive exactly when Test ran.
            result['BrowserTests'] = 12 if result['Test'] == 'pass' else 0
            # Playwright-executed end-to-end count: positive exactly when verify ran them.
            result['E2ETests'] = 3 if result['Verify'] == 'pass' and name in end_to_end else 0
            receipt['Results'].append(result)
    def write():
        for key, receipt in receipts.items(): (receipt_dir / f'matrix-{key}.json').write_text(json.dumps(receipt), encoding='utf-8')
    write()
    command=['pwsh','-NoProfile','-File','scripts/check-template-matrix-results.ps1','-CandidateVersion',CANDIDATE_VERSION,
             '-ResultsPath',str(receipt_dir),'-Tier',tier,'-ValidationPlanPath',str(expected_file)]
    run(label+'-receipts-valid',command,repo)
    first = next(iter(receipts.values()))
    for field,bad in [('Version',1),('CandidateSha','b'*40),('CandidateVersion','0.12.1-ci.1.2'),('CandidateVersion',None),
                      ('Mode','backend' if mode == 'full' else 'full'),('Tier','full' if tier == 'core' else 'core'),('Slice','unregistered')]:
        saved=first[field];first[field]=bad;write();run(label+'-reject-'+field+('-missing' if bad is None else ''),command,repo,False);first[field]=saved
        if field == 'CandidateVersion':
            assert 'framework-pack' in (out/(label+'-reject-'+field+('-missing' if bad is None else '')+'.log')).read_text(encoding='utf-8')
    result=first['Results'][0]
    for stage in ['Backend','Runtime','Lint','Frontend','Test']:
        saved=result[stage]
        for bad in ['skipped','failure','', 'pass' if saved=='not-applicable' else 'not-applicable']:
            result[stage]=bad;write();run(f'{label}-reject-{stage}-{bad or "missing"}',command,repo,False)
        result[stage]=saved
    # Browser test count: zero, missing, non-integer or negative is no evidence; non-run tests may not claim any.
    browser = next((r for receipt in receipts.values() for r in receipt['Results'] if r['Test'] == 'pass'), None)
    idle = next((r for receipt in receipts.values() for r in receipt['Results'] if r['Test'] != 'pass'), None)
    assert mode == 'backend' or browser, 'fixture lacks a frontend test result'
    for target, values in [(browser, [0, None, '12', -1]), (idle, [1, None])]:
        if target is None: continue
        for bad in values:
            if bad is None: del target['BrowserTests']
            else: target['BrowserTests'] = bad
            case = f"{label}-reject-browser-tests-{target['Scenario']}-{'missing' if bad is None else bad}"
            write(); run(case, command, repo, False)
            assert 'BrowserTests' in (out / (case + '.log')).read_text(encoding='utf-8')
            target['BrowserTests'] = 12 if target['Test'] == 'pass' else 0
    write()
    # End-to-end count: same evidence rule, owned by verify on local identity scenarios only.
    e2e = next((r for receipt in receipts.values() for r in receipt['Results'] if r['E2ETests'] > 0), None)
    no_e2e = [r for receipt in receipts.values() for r in receipt['Results'] if r['E2ETests'] == 0]
    assert mode != 'full' or e2e, 'full fixture lacks an end-to-end result'
    for target, values in [(e2e, [0, None, '3', -1])] + [(r, [1, None]) for r in no_e2e[:1] + [r for r in no_e2e if r['Verify'] == 'pass'][:1]]:
        if target is None: continue
        for bad in values:
            if bad is None: del target['E2ETests']
            else: target['E2ETests'] = bad
            case = f"{label}-reject-e2e-tests-{target['Scenario']}-{'missing' if bad is None else bad}"
            write(); run(case, command, repo, False)
            assert 'E2ETests' in (out / (case + '.log')).read_text(encoding='utf-8')
            target['E2ETests'] = 3 if target['Verify'] == 'pass' and target['Scenario'] in end_to_end else 0
    write()
    saved=first['Results'];first['Results']=saved[:-1];write();run(label+'-reject-missing-scenario',command,repo,False);first['Results']=saved
    # Exact ownership, counts and container responsibility are independent of claimed success.
    second = list(receipts.values())[1]
    first['Results'][0],second['Results'][0] = second['Results'][0],first['Results'][0]
    write();run(label+'-reject-moved-scenario',command,repo,False)
    first['Results'][0],second['Results'][0] = second['Results'][0],first['Results'][0]
    first['Results'].append(first['Results'][0]);write();run(label+'-reject-duplicate-scenario',command,repo,False);first['Results'].pop()
    write()
    first_file = receipt_dir / f"matrix-{first['Slice']}.json"
    first_file.unlink();run(label+'-reject-missing-slice',command,repo,False);write()
    duplicate = receipt_dir / 'matrix-duplicate.json';duplicate.write_text(json.dumps(first), encoding='utf-8')
    run(label+'-reject-duplicate-slice',command,repo,False);duplicate.unlink()
    # Verify: a registered scenario must report pass in full mode; nobody else may claim it ran.
    results = [r for receipt in receipts.values() for r in receipt['Results']]
    samples = [next(r for r in results if r['Verify'] == 'not-run')] if any(r['Verify'] == 'not-run' for r in results) else []
    samples += [r for r in results if r['Verify'] == 'pass'][:1]
    assert mode != 'full' or any(r['Verify'] == 'pass' for r in samples), 'full fixture lacks a Verify scenario'
    for result in samples:
        saved = result['Verify']
        for bad in (['not-run', 'skipped', 'failure', None] if saved == 'pass' else ['pass', None]):
            if bad is None:
                del result['Verify']
            else:
                result['Verify'] = bad
            write(); run(f"{label}-reject-verify-{result['Scenario']}-{bad or 'missing'}", command, repo, False)
            assert 'Verify expected' in (out / f"{label}-reject-verify-{result['Scenario']}-{bad or 'missing'}.log").read_text(encoding='utf-8')
            result['Verify'] = saved
    write()
    result = first['Results'][0];saved = result['Container']
    result['Container'] = 'skipped' if saved == 'pass' else 'pass';write();run(label+'-reject-container-claim',command,repo,False);result['Container'] = saved
    if plan['ContainerSmoke']:
        result = next(result for receipt in receipts.values() for result in receipt['Results'] if result['Container'] == 'pass')
        result['Container'] = 'skipped';write();run(label+'-reject-required-container',command,repo,False);result['Container'] = 'pass'
    write()
    mutations = {
        'legacy-version':lambda p:p.update(Version=1),
        'missing-groups':lambda p:p.pop('Slices'),
        'missing-group':lambda p:p['Slices'].pop(),
        'empty-group':lambda p:p['Slices'][0].update(Scenarios=[]),
        'unknown-member':lambda p:p['Slices'][0]['Scenarios'].append('unknown'),
        'duplicate-member':lambda p:p['Slices'][0]['Scenarios'].append(p['Slices'][0]['Scenarios'][0]),
        'duplicate-group':lambda p:p['Slices'][1].update(key=p['Slices'][0]['key']),
        'missing-container-scope':lambda p:p.pop('ContainerSmoke'),
        'bad-container-type':lambda p:p.update(ContainerSmoke='false'),
        'bad-container-assignment':lambda p:p['Slices'][0]['Containers'].append(p['Slices'][0]['Scenarios'][0]),
        'reordered-members':lambda p:p['Slices'][0]['Scenarios'].reverse(),
    }
    def move_plan_member(value):
        left, right = value['Slices'][0]['Scenarios'], value['Slices'][1]['Scenarios']
        left[0], right[0] = right[0], left[0]
    mutations['moved-plan-members'] = move_plan_member
    if plan['ContainerSmoke']:
        mutations['missing-container-assignment'] = lambda p:next(g for g in p['Slices'] if g['Containers']).update(Containers=[])
    if len(plan['Slices'][0]['Scenarios']) < 2:
        # One scenario per core group: reversing it is no mutation; swapping two groups is.
        mutations['reordered-members'] = lambda p: (p['Slices'].insert(0, p['Slices'].pop(1)),
                                                    [g.update(key=f'execution-{i + 1:02d}') for i, g in enumerate(p['Slices'])])
    for mutation,apply in mutations.items():
        invalid = json.loads(json.dumps(plan));apply(invalid);expected_file.write_text(json.dumps(invalid), encoding='utf-8')
        run(label+'-reject-plan-'+mutation,command,repo,False)
        if mutation in ('reordered-members', 'moved-plan-members'):
            assert 'preserve registered group members and order' in (out/(label+'-reject-plan-'+mutation+'.log')).read_text(encoding='utf-8')
    expected_file.write_text(json.dumps(plan), encoding='utf-8')
    if mode == 'full':
        pure_api = next(result for receipt in receipts.values() for result in receipt['Results'] if not scenarios[result['Scenario']].get('Frontend', True))
        for stage in ['Lint', 'Frontend', 'Test']:
            assert pure_api[stage] == 'not-applicable' and pure_api['BrowserTests'] == 0
            pure_api[stage] = 'pass'; write()
            run(f'{label}-reject-pure-api-{stage}-claimed-pass', command, repo, False)
            pure_api[stage] = 'not-applicable'
        # A forged pure-API Test=pass stays rejected even with a plausible browser count.
        pure_api['Test'] = 'pass'; pure_api['BrowserTests'] = 12; write()
        run(f'{label}-reject-pure-api-forged-browser-tests', command, repo, False)
        pure_api['Test'] = 'not-applicable'; pure_api['BrowserTests'] = 0
    write();run(label+'-receipts-restored',command,repo)
    # Old full checker/entry must reject manual/claimed skips too.
    run(label+'-manual-contract-rejects-ci-groups',command[:-2],repo,False)


def prove_release_responsibilities(repo, base, planner, scenarios, out, run, git):
    """Release responsibility table: every row is checked for a publication candidate and each gap exits non-zero."""
    git('reset', '--hard', base)
    (repo / 'README.md').write_text((repo / 'README.md').read_text(encoding='utf-8') + '\nrelease fixture\n', encoding='utf-8')
    git('add', '.'); git('commit', '-qm', 'documentation-only release candidate')
    head = git('rev-parse', 'HEAD')
    # Without publication the same documentation input carries no container responsibility.
    ordinary = planner.create_plan('full', base, 'push', '')
    assert not ordinary['ReleaseRequired'] and not ordinary['ContainerSmoke'], ordinary
    plan = planner.create_plan('full', base, 'workflow_dispatch', head, release_channel='beta')
    assert plan['ReleaseRequired'] and plan['Mode'] == 'full' and plan['ContainerSmoke'] and all(plan['Jobs'].values()), plan
    assert plan['DependencyAudit'], 'Manual publication always runs the dependency audit'
    core = [name for name, info in scenarios.items() if 'core' in info['Slices']]
    containers = [name for group in plan['Slices'] for name in group['Containers']]
    assert sorted(containers) == sorted(['standalone-external-login', 'resource-capabilities-03']), containers
    (out / 'release-plan.json').write_text(json.dumps(plan, indent=2), encoding='utf-8')
    plan_file = out / 'release-expected.json'
    validator = ['pwsh', '-NoProfile', '-Command',
                 "$ErrorActionPreference='Stop'; . ./scripts/template-matrix-scenarios.ps1; . ./scripts/quality-validation-plan.ps1; "
                 f"$null = Read-QualityValidationPlan -Path '{plan_file}' -ExpectedTier full"]
    plan_file.write_text(json.dumps(plan), encoding='utf-8'); run('release-plan-valid', validator, repo)
    for name, mutate in [('postgresql-not-applicable', lambda p: p['Jobs'].update({'postgresql-e2e': False})),
                         ('oidc-not-applicable', lambda p: p['Jobs'].update({'oidc-e2e': False})),
                         ('no-container-scope', lambda p: (p.update(ContainerSmoke=False), [g.update(Containers=[]) for g in p['Slices']])),
                         ('core-scenarios-only', lambda p: p.update(Scenarios=core, Slices=planner.execution_slices(scenarios, core, 'full', 'full', True))),
                         ('representative-downgrade', lambda p: p.update(Tier='core', ScenarioSelection='representative', Scenarios=core,
                                                                         Slices=planner.execution_slices(scenarios, core, 'core', 'full', True))),
                         ('affected-framework-tests', lambda p: p.update(FrameworkTestSelection='affected'))]:
        invalid = json.loads(json.dumps(plan)); mutate(invalid)
        plan_file.write_text(json.dumps(invalid), encoding='utf-8')
        # A downgraded publication plan is rejected whichever tier the caller expects.
        tier = invalid['Tier']
        run('release-plan-reject-' + name, validator[:-1] + [validator[-1].replace('-ExpectedTier full', f'-ExpectedTier {tier}')], repo, False)
        assert 'Publication requires' in (out / ('release-plan-reject-' + name + '.log')).read_text(encoding='utf-8')
    # Stable is the template release gate: a plan without the dependency audit, or without the field, is rejected.
    stable = planner.create_plan('full', base, 'push', head, release_channel='stable')
    assert stable['DependencyAudit'], stable
    plan_file.write_text(json.dumps(stable), encoding='utf-8'); run('stable-plan-valid', validator, repo)
    for name, mutate, message in [('stable-without-audit', lambda p: p.update(DependencyAudit=False), 'Stable publication requires the frontend dependency audit'),
                                  ('missing-audit-field', lambda p: p.pop('DependencyAudit'), 'Invalid quality plan'),
                                  ('invalid-release-channel', lambda p: p.update(ReleaseChannel='invalid'), 'Invalid quality plan')]:
        invalid = json.loads(json.dumps(stable)); mutate(invalid)
        plan_file.write_text(json.dumps(invalid), encoding='utf-8')
        run('stable-plan-reject-' + name, validator, repo, False)
        assert message in (out / ('stable-plan-reject-' + name + '.log')).read_text(encoding='utf-8')
    plan_file.write_text(json.dumps(plan), encoding='utf-8')
    # Every registered full-tier scenario, both containers and the browser count, on full-tier execution groups.
    prove_receipts(repo, plan, scenarios, out, run, label='release')
    receipt_dir = out / 'release-receipts'
    command = ['pwsh', '-NoProfile', '-File', 'scripts/check-template-matrix-results.ps1', '-CandidateVersion', CANDIDATE_VERSION,
               '-ResultsPath', str(receipt_dir), '-Tier', 'full', '-ValidationPlanPath', str(out / 'release-expected.json')]
    run('release-receipts-valid', command, repo)
    for container in ('resource-capabilities-03', 'standalone-external-login'):
        receipt_file = next(path for path in receipt_dir.glob('matrix-*.json')
                            if any(r['Scenario'] == container for r in json.loads(path.read_text(encoding='utf-8'))['Results']))
        saved = receipt_file.read_text(encoding='utf-8')
        receipt = json.loads(saved)
        result = next(r for r in receipt['Results'] if r['Scenario'] == container)
        for bad in ('skipped', 'failure', None):
            if bad is None: del result['Container']
            else: result['Container'] = bad
            receipt_file.write_text(json.dumps(receipt), encoding='utf-8')
            run(f'release-reject-container-{container}-{bad or "missing"}', command, repo, False)
            result['Container'] = 'pass'
        receipt['Results'] = [r for r in receipt['Results'] if r['Scenario'] != container]
        receipt_file.write_text(json.dumps(receipt), encoding='utf-8')
        run(f'release-reject-container-{container}-result-missing', command, repo, False)
        receipt_file.write_text(saved, encoding='utf-8')
    run('release-receipts-restored', command, repo)
    prove_release_proofs(repo, plan, out, run)
    git('reset', '--hard', base)
    print('PASS release responsibilities: forced container scope, PG/OIDC applicable, receipts, browser counts and proofs', flush=True)


def template_source_digests(repo):
    """The same candidate source digests the generation shards record (and the summary recomputes)."""
    root = repo / 'template'
    return {p.relative_to(repo).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(root.rglob('*')) if p.is_file()
            and not any(part in ('node_modules', 'bin', 'obj', '.cache') for part in p.relative_to(root).parts)}


def generation_shards(plan, repo):
    """A consistent four-shard generation proof: three modulo shape shards and the control shard."""
    sources = template_source_digests(repo)
    roles = ['Identity', 'Standalone', 'Resource']
    def shape(index):
        return [index // 32, index % 32]
    def digest(index):
        return hashlib.sha256(str(index).encode()).hexdigest()
    keys = [dict(Script=hashlib.sha256(f'verify-{k}'.encode()).hexdigest(), Steps=['static', f'build-{k}', 'test']) for k in range(2)]
    common = dict(Version=1, Result='pass', CandidateSha=plan['CandidateSha'], TemplateSourceDigests=sources,
                  ShapeShards=3, EffectiveShapes=320)
    shards = {str(k): dict(common, Shard=str(k), Shapes=[dict(Index=i, Effective=shape(i), Files=10, Digest=digest(i),
                                                             VerifyKey=keys[i % 2]) for i in range(320) if i % 3 == k])
              for k in range(3)}
    representatives = [0, 1, 106, 319]
    shards['control'] = dict(common, Shard='control', RawInputs=768,
        InputMapping=[dict(input=['--input', str(i)], effective=shape(i % 320)) for i in range(768)],
        Representatives=[dict(Index=i, Effective=shape(i), Digest=digest(i)) for i in representatives],
        EquivalenceComparisons=[dict(variant=320 + n, representative=representatives[n % 4], role=roles[n % 3],
                                     Digest=digest(representatives[n % 4])) for n in range(6)],
        CiVariants=[dict(variant=326 + n, representative=representatives[n // 2], ci=['gitlab', 'none'][n % 2], changed=[]) for n in range(4)],
        VerifySignatures=2,
        VerifyChecks=[dict(Key=key, Steps=key['Steps'], FailingStep=key['Steps'][1], ExitCode=7, Calls=2) for key in keys])
    return shards


def prove_release_proofs(repo, plan, out, run):
    """Generation shards and PG/OIDC proofs: each missing item, failed result or zero count is rejected."""
    plan_file = out / 'release-expected.json'
    plan_file.write_text(json.dumps(plan), encoding='utf-8')
    proofs = out / 'release-proofs'
    generation = generation_shards(plan, repo)
    e2e = dict(Version=1, Result='pass', CandidateSha=plan['CandidateSha'], Assertions=40)
    def check(kind, proof, success, case):
        if proofs.exists(): shutil.rmtree(proofs)
        proofs.mkdir()
        if kind == 'generation':
            # Each shard's artifact lands in its own directory, as download-artifact does by pattern.
            for index, item in enumerate(proof or []):
                (proofs / f'template-generation-proof-{index}').mkdir()
                (proofs / f'template-generation-proof-{index}' / 'generation-results.json').write_text(json.dumps(item), encoding='utf-8')
        elif proof is not None:
            (proofs / 'proof.json').write_text(json.dumps(proof), encoding='utf-8')
        run(f'release-proof-{kind}-{case}', ['pwsh', '-NoProfile', '-File', 'scripts/check-quality-proof.ps1',
            '-ValidationPlanPath', str(plan_file), '-Kind', kind, '-ResultsPath', str(proofs)], repo, success)
    def shards(**changes):
        """Mutate a copy: changes map shard name -> function(proof); None drops that shard."""
        value = json.loads(json.dumps(generation))
        for name, apply in changes.items():
            if apply is None: value.pop(name)
            else: apply(value[name])
        return list(value.values())
    check('generation', shards(), True, 'valid')
    control_cases = [
        ('missing-raw-inputs', lambda p: p.pop('RawInputs')), ('missing-mapping', lambda p: p.pop('InputMapping')),
        ('short-mapping', lambda p: p['InputMapping'].pop()),
        ('duplicate-raw-inputs', lambda p: p['InputMapping'][1].update(input=p['InputMapping'][0]['input'])),
        ('mapping-shapes-collapsed', lambda p: [m.update(effective=[1, 1]) for m in p['InputMapping'] if m['effective'] == [0, 0]]),
        ('missing-role-equivalence', lambda p: p.pop('EquivalenceComparisons')),
        ('cancelled-role-equivalence', lambda p: p.update(EquivalenceComparisons=[])),
        ('role-equivalence-without-resource', lambda p: p.update(EquivalenceComparisons=[c for c in p['EquivalenceComparisons'] if c['role'] != 'Resource'])),
        ('equivalence-digest-differs', lambda p: p['EquivalenceComparisons'][0].update(Digest='0' * 64)),
        ('missing-ci-variants', lambda p: p.pop('CiVariants')), ('removed-ci-comparison', lambda p: p['CiVariants'].pop()),
        ('missing-verify', lambda p: p.pop('VerifyChecks')), ('empty-verify', lambda p: p.update(VerifyChecks=[])),
        ('verify-no-steps', lambda p: p['VerifyChecks'][0].update(Steps=[])),
        ('verify-exit-not-propagated', lambda p: p['VerifyChecks'][0].update(ExitCode=0)),
        ('verify-continued-after-failure', lambda p: p['VerifyChecks'][0].update(Calls=3)),
        ('verify-unknown-failing-step', lambda p: p['VerifyChecks'][0].update(FailingStep='other')),
        ('verify-variant-unchecked', lambda p: (p['VerifyChecks'].pop(), p.update(VerifySignatures=1))),
        ('verify-signatures-differ', lambda p: p.update(VerifySignatures=3)),
        ('verify-key-differs-from-steps', lambda p: p['VerifyChecks'][0]['Key'].update(Steps=['other']))]
    for case, apply in control_cases:
        check('generation', shards(control=apply), False, 'control-' + case)
    for name in ('0', '1', '2', 'control'):
        check('generation', shards(**{name: None}), False, f'missing-shard-{name}')
        for case, apply in [('result-failure', lambda p: p.update(Result='failure')), ('result-cancelled', lambda p: p.update(Result='cancelled')),
                            ('foreign-candidate', lambda p: p.update(CandidateSha='a' * 40)), ('version', lambda p: p.update(Version=2)),
                            ('foreign-source', lambda p: p['TemplateSourceDigests'].update({'template/README.md': '0' * 64})),
                            ('missing-source', lambda p: p['TemplateSourceDigests'].pop('template/README.md')),
                            ('unknown-shard', lambda p: p.update(Shard='3'))]:
            check('generation', shards(**{name: apply}), False, f'shard-{name}-{case}')
    duplicate = shards(); duplicate.append(json.loads(json.dumps(generation['0'])))
    check('generation', duplicate, False, 'duplicate-shard')
    for case, apply in [('missing-shape', lambda p: p['Shapes'].pop()),
                        ('misassigned-shape', lambda p: p['Shapes'][0].update(Index=1)),
                        ('duplicate-shape-effective', lambda p: p['Shapes'][1].update(Effective=p['Shapes'][0]['Effective'])),
                        ('shape-digest-missing', lambda p: p['Shapes'][0].pop('Digest')),
                        ('shape-verify-key-unchecked', lambda p: p['Shapes'][0]['VerifyKey'].update(Script='f' * 64)),
                        ('empty-shard', lambda p: p.update(Shapes=[]))]:
        check('generation', shards(**{'0': apply}), False, case)
    check('generation', None, False, 'missing')
    for kind in ('postgresql', 'oidc'):
        check(kind, e2e, True, 'valid')
        for case, apply in [('zero-assertions', lambda p: p.update(Assertions=0)), ('missing-assertions', lambda p: p.pop('Assertions')),
                            ('text-assertions', lambda p: p.update(Assertions='40')), ('result-failure', lambda p: p.update(Result='failure')),
                            ('result-cancelled', lambda p: p.update(Result='cancelled')), ('foreign-candidate', lambda p: p.update(CandidateSha='a' * 40))]:
            value = json.loads(json.dumps(e2e)); apply(value)
            check(kind, value, False, case)
        check(kind, None, False, 'missing')
        job = 'postgresql-e2e' if kind == 'postgresql' else 'oidc-e2e'
        plan_file.write_text(json.dumps(dict(plan, Jobs=dict(plan['Jobs'], **{job: False}))), encoding='utf-8')
        check(kind, e2e, False, 'not-applicable-plan')
        plan_file.write_text(json.dumps(plan), encoding='utf-8')


def prove_scheduling(planner, scenarios):
    # Preserve registered allocation and order, including a partial logical group.
    sample = dict(Titles={'full':{'first':'first','second':'second','third':'third'}}, Containers=['heavy'])
    synthetic = {name:dict(sample, Slices={'full':group})
                 for name,group in [('heavy','first'),('b','first'),('a','second'),('c','third')]}
    groups = planner.execution_slices(synthetic,list(synthetic),'full','full',True)
    assert [g['Scenarios'] for g in groups] == [['heavy','b'],['a'],['c']]
    assert [g['Containers'] for g in groups] == [['heavy'],[],[]]
    assert all(any(name in group['title'] for name in group['Scenarios']) for group in groups)
    assert all(('含容器' in group['title']) == bool(group['Containers']) for group in groups)
    assert groups == planner.execution_slices(synthetic,list(reversed(synthetic)),'full','full',True)
    for mode in ('full','frontend','backend'):
        partial = planner.execution_slices(synthetic,['heavy','b'],'full',mode,False)
        assert len(partial) == 1 and partial[0]['Scenarios'] == ['heavy','b']
        assert partial[0]['Containers'] == []
        split = planner.execution_slices(synthetic,['b','c'],'full',mode,False)
        assert [g['key'] for g in split] == ['execution-01','execution-02']
        assert [g['Scenarios'] for g in split] == [['b'],['c']]
        # core: one scenario per group in registered order, whatever the logical group.
        single = planner.execution_slices(synthetic,['c','heavy','b'],'core',mode,mode == 'full')
        assert [g['Scenarios'] for g in single] == [['heavy'],['b'],['c']]
        assert [g['Containers'] for g in single] == ([['heavy'],[],[]] if mode == 'full' else [[],[],[]])
    assert planner.execution_slices(synthetic,[],'full','full',False) == []
    for tier in ('core','full'):
        selected = [n for n,info in scenarios.items() if tier in info['Slices']]
        groups = planner.execution_slices(scenarios,selected,tier,'full',True)
        titles = next(iter(scenarios.values()))['Titles'][tier]
        assert [g['Scenarios'] for g in groups] == [[n for n in selected if scenarios[n]['Slices'][tier] == logical] for logical in titles
                                                    if any(scenarios[n]['Slices'][tier] == logical for n in selected)]
        assert {n for g in groups for n in g['Scenarios']} == set(selected)
        assert sorted(n for g in groups for n in g['Containers']) == sorted(next(iter(scenarios.values()))['Containers'])
    print('PASS registered allocation: candidate containers, partial groups, one core scenario per group and full coverage',flush=True)


if __name__ == '__main__': main()
