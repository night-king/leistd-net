#!/usr/bin/env python3
"""Maintain selection/receipt contracts with real Git inputs and template generation.

Not a per-feature gate. Logs and generation evidence stay under .tmp.
"""
import argparse
import hashlib
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

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]


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
        for path in (p for p in paths if p and not p.startswith("docs/reports/")):
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
        registered = {name for name, info in scenarios.items() if 'pr' in info['Slices']}
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
            plan = planner.create_plan('pr', base, 'pull_request', '', container_smoke=label == 'cross-layer')
            assert plan['Mode'] == expected_mode, (label, plan)
            assert plan['Version'] == 3 and (label != 'cross-layer' or plan['ContainerSmoke'])
            assigned = [name for group in plan['Slices'] for name in group['Scenarios']]
            assert len(assigned) == len(set(assigned)) and set(assigned) == set(plan['Scenarios'])
            assert len(plan['Slices']) == len({scenarios[name]['Slices']['pr'] for name in plan['Scenarios']})
            assert all(group['Scenarios'] for group in plan['Slices'])
            assert all(any(name in group['title'] for name in group['Scenarios']) for group in plan['Slices'])
            assert plan['Slices'] == planner.execution_slices(scenarios, plan['Scenarios'], 'pr', plan['Mode'], plan['ContainerSmoke'])
            if expected_mode in ('frontend', 'backend'):
                try:
                    planner.create_plan('pr', base, 'pull_request', '', container_smoke=True)
                except ValueError as error:
                    assert 'Container scope requires' in str(error)
                else:
                    raise AssertionError('Conflicting container responsibility must fail explicitly')
            if label in ('frontend-feature','frontend-roles-15','backend-signing-9','backend-roles-18'):
                expected_counts = {'frontend-feature':8,'frontend-roles-15':15,'backend-signing-9':9,'backend-roles-18':18}
                assert len(plan['Scenarios']) == expected_counts[label], (label, plan['Scenarios'])
            local = planner.local_scenarios(base)
            assert local['Kind'] == 'local-template-scenarios' and local['HeadSha'] == git('rev-parse', 'HEAD')
            assert 'Version' not in local and 'Mode' not in local and 'CandidateSha' not in local
            if expected_mode in ('frontend', 'backend'):
                assert local['Selection'] == 'source-products', (label, local)
                assert local['Scenarios'] == plan['Scenarios']
            elif label == 'cross-layer':
                # Independently validated single-side PR plans, excluding full-only products.
                expected = set(json.loads((out / 'frontend-feature-plan.json').read_text(encoding='utf-8'))['Scenarios'])
                expected |= set(json.loads((out / 'backend-api-plan.json').read_text(encoding='utf-8'))['Scenarios'])
                assert local['Selection'] == 'source-products' and set(local['Scenarios']) == expected, local
            else:
                assert local['Selection'] == 'complete-pr' and set(local['Scenarios']) == registered
            (out / (label + '-local.json')).write_text(json.dumps(local, indent=2), encoding='utf-8')
            if expected_mode in ('frontend', 'backend'):
                assert plan['FrameworkTests'] is False and plan['ConsumerProjects'] == []
                assert plan['FrameworkTestProjects'] == [] and plan['FrameworkTestSelection'] == 'none'
                assert {'identity', 'identity-all-features'} <= set(plan['Scenarios']) <= registered
            elif expected_mode == 'documentation':
                assert not plan['FrameworkTests'] and not plan['Scenarios'] and plan['ConsumerProjects'] == []
                assert plan['PackageDocumentation'] == (label == 'packaged-doc')
                assert plan['GeneratedDocumentation'] == (label == 'generated-doc')
            elif label == 'framework-source':
                assert plan['ConsumerProjects'] == ['Leistd.Email.Smtp'] and plan['FrameworkTests']
                assert plan['FrameworkTestProjects'] == [EMAIL_TESTS] and plan['FrameworkTestSelection'] == 'affected', plan
            else:
                assert plan['ConsumerProjects'] is None and plan['FrameworkTests'] and set(plan['Scenarios']) == registered
                assert plan['FrameworkTestSelection'] == 'all' and plan['FrameworkTestProjects'] == planner.all_framework_tests('HEAD'), plan
            for tier, event, baseline, candidate in [('full','pull_request',base,''), ('pr','workflow_dispatch',base,''),
                ('pr','pull_request','0'*40,''), ('pr','pull_request',git('rev-parse','HEAD'),'')]:
                conservative = planner.create_plan(tier, baseline, event, candidate)
                assert conservative['Mode'] == 'full' and conservative['FrameworkTests'] and conservative['ConsumerProjects'] is None
                assert conservative['FrameworkTestSelection'] == 'all' and conservative['FrameworkTestProjects'] == planner.all_framework_tests('HEAD')
            (out / (label + '-plan.json')).write_text(json.dumps(plan, indent=2), encoding='utf-8')
            print('PASS complete-input plan:', label, flush=True)
            if label in ('frontend-feature', 'backend-api'):
                prove_generation(repo, base, plan, scenarios, out, run, git)
                prove_receipts(repo, plan, scenarios, out, run)
            elif label == 'cross-layer':
                prove_generation(repo, base, dict(plan, Mode='local-mixed', Scenarios=local['Scenarios']), scenarios, out, run, git)
                prove_receipts(repo, plan, scenarios, out, run)
        # Deletion, move across boundaries and later docs commit must keep the
        # original source impact. Evaluate old/new paths, never just HEAD^.
        git('reset', '--hard', base); git('rm', front); git('commit', '-qm', 'delete frontend input')
        assert planner.create_plan('pr',base,'pull_request','')['Mode'] == 'frontend'
        assert planner.local_scenarios(base)['Selection'] == 'source-products'
        git('reset', '--hard', base); git('mv',front,'docs/moved-source.ts'); git('commit','-qm','cross boundary move')
        (repo / 'docs/later.md').write_text('later documentation', encoding='utf-8')
        git('add','.'); git('commit','-qm','later doc change')
        assert planner.create_plan('pr',base,'pull_request','')['Mode'] == 'full'
        assert planner.local_scenarios(base)['Selection'] == 'complete-pr'
        git('reset', '--hard', base)
        (repo / front).write_text((repo / front).read_text(encoding='utf-8') + '\n// Uncommitted edit\n', encoding='utf-8')
        git('add', front); git('commit', '-qm', 'committed frontend input')
        assert planner.create_plan('pr', base, 'pull_request', '')['Mode'] == 'frontend'
        (repo / back).write_text((repo / back).read_text(encoding='utf-8') + '\n// Uncommitted backend edit\n', encoding='utf-8')
        dirty = planner.create_plan('pr', base, 'pull_request', '')
        assert dirty['Mode'] == 'full' and dirty['FrameworkTests'] and dirty['ConsumerProjects'] is None and 'working tree' in dirty['Reason']
        assert dirty['FrameworkTestSelection'] == 'all' and dirty['FrameworkTestProjects'] == planner.all_framework_tests('HEAD')
        assert planner.local_scenarios(base)['Selection'] == 'complete-pr'
        git('reset', '--hard', base)
        (repo / 'untracked-input.ts').write_text('untracked', encoding='utf-8')
        assert planner.local_scenarios(base)['Selection'] == 'complete-pr'
        (repo / 'untracked-input.ts').unlink()
        for invalid in ('', '0'*40, git('rev-parse','HEAD')):
            assert planner.local_scenarios(invalid)['Selection'] == 'complete-pr'
        git('reset', '--hard', base)
        print('PASS deletion, cross-boundary rename and multi-commit conservative fallback', flush=True)
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
            guarded = planner.create_plan('pr', rule_base, 'pull_request', '')
            assert guarded['Mode'] == 'full' and guarded['FrameworkTests'] and guarded['ConsumerProjects'] is None
            assert set(guarded['Scenarios']) == registered and 'proof unavailable' in guarded['Reason']
            assert planner.local_scenarios(rule_base)['Selection'] == 'complete-pr'
            (out / (label + '-plan.json')).write_text(json.dumps(guarded, indent=2), encoding='utf-8')
            print('PASS unknown engine rule conservative fallback:', label, flush=True)
        prove_local_products(repo, base, planner, scenarios, out, run, git)
        prove_framework_tests(repo, base, planner, out, run, git)
    print('Selection and receipt evidence:', out, flush=True)


EMAIL_TESTS = 'framework/tests/components/email/Leistd.Email.Tests/Leistd.Email.Tests.csproj'


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
        plan = planner.create_plan('pr', case_base, 'pull_request', '')
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
    run('framework-local-cli', [sys.executable, 'scripts/plan-quality-checks.py', '--local-framework-tests', '--tier', 'pr',
                                '--base', both['BaseSha'], '--output', str(local_file)], repo)
    local = json.loads(local_file.read_text(encoding='utf-8'))
    assert local['Kind'] == 'local-framework-tests' and local['Projects'] == both['FrameworkTestProjects'] and local['Selection'] == 'affected'
    run('framework-local-both-modes', [sys.executable, 'scripts/plan-quality-checks.py', '--local-framework-tests', '--local-scenarios',
                                       '--tier', 'pr', '--base', base, '--output', str(local_file)], repo, success=False)

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


def prove_local_products(repo, base, planner, scenarios, out, run, git):
    """Prove conditional products, unchanged omissions and separate CI schema."""
    git('reset', '--hard', base)
    source = 'template/frontend/src/app/app.spec.ts'
    path = repo / source
    path.write_text(path.read_text(encoding='utf-8') + '\n//#if (IncludeNotifications)\n// local branch fixture\n//#endif\n', encoding='utf-8')
    git('add', '.'); git('commit', '-qm', 'local conditional source')
    selected = planner.local_scenarios(base)
    assert selected['Selection'] == 'source-products', selected
    config = json.loads((repo / 'template/.template.config/template.json').read_text(encoding='utf-8'))
    symbols = {name: planner.coverage.symbol_values(config, planner.coverage.parse_cli_arguments(config, info['Arguments']))
               for name, info in scenarios.items() if name in selected['Scenarios']}
    assert all(values['SpaFrontend'] for values in symbols.values()), selected
    assert any(values['SpaFrontend'] and values['IncludeNotifications'] for values in symbols.values())
    assert any(values['SpaFrontend'] and not values['IncludeNotifications'] for values in symbols.values())
    plan = planner.create_plan('pr',base,'pull_request','')
    assert plan['Scenarios'] == selected['Scenarios']
    prove_generation(repo, base, dict(plan, Mode='local-conditional'), scenarios, out, run, git)
    local_file = out / 'conditional-local.json'
    run('local-cli', [sys.executable,'scripts/plan-quality-checks.py','--local-scenarios','--tier','pr',
                     '--base',base,'--output',str(local_file)], repo)
    assert json.loads(local_file.read_text(encoding='utf-8')) == selected
    for flags in (['--tier','full'], ['--tier','pr','--github-output'],
                  ['--tier','pr','--docs-only','true'], ['--tier','pr','--candidate-input','a'*40]):
        run('local-invalid-' + str(len(flags)) + '-' + flags[-1],
            [sys.executable,'scripts/plan-quality-checks.py','--local-scenarios',*flags,
             '--output',str(out/'invalid-local.json')], repo, success=False)
    run('local-not-ci-plan', ['pwsh','-NoProfile','-Command',
        "$ErrorActionPreference='Stop'; . ./scripts/quality-validation-plan.ps1; . ./scripts/template-matrix-scenarios.ps1; "
        f"Read-QualityValidationPlan -Path '{local_file}' -ExpectedTier pr"], repo, success=False)
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
        assert raced['Selection'] == 'complete-pr' and 'HEAD changed' in raced['Reason']
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
        assert raced['Selection'] == 'complete-pr' and 'working tree' in raced['Reason']
    finally:
        planner.source_producers = original_producers
        untracked.unlink(missing_ok=True)
    print('PASS local conditional/mixed products, snapshot races and separate CI contract', flush=True)


def prove_generation(repo, base, plan, scenarios, out, run, git):
    """Actually generate every PR product on both sides and compare stage inputs.

    Only generated UserSecretsId values are normalized: the template explicitly
    randomizes that property at creation, while its XML structure stays checked.
    """
    import io
    import tarfile
    label = plan['Mode']
    old = out / (label + '-base-template')
    old.mkdir(exist_ok=True)
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
            if 'pr' not in info['Slices']: continue
            target = out / 'generated' / label / version / name
            run(f'{label}-{version}-{name}', ['dotnet','new','--debug:custom-hive',str(hive),'fullstack-app','-n','Quality.Scope','-o',str(target),'--force',*info['Arguments']],repo)
            files = {}
            for path in sorted(target.rglob('*')):
                if not path.is_file(): continue
                content = path.read_bytes()
                if path.suffix == '.csproj':
                    content = re.sub(rb'<UserSecretsId>[^<]+</UserSecretsId>',b'<UserSecretsId>GENERATED</UserSecretsId>',content)
                files[path.relative_to(target).as_posix()] = hashlib.sha256(content).hexdigest()
            digests[version,name] = files
    omitted = 'backend/' if label == 'frontend' else ('frontend/' if label == 'backend' else None)
    selected = set(plan['Scenarios'])
    for name in {name for version,name in digests}:
        old_files, new_files = digests['base',name],digests['head',name]
        delta = {path for path in old_files.keys() | new_files.keys() if old_files.get(path) != new_files.get(path)}
        assert omitted is None or not any(path.startswith(omitted) for path in delta), (label,name,'omitted stage input changed',delta)
        assert name in selected or not delta, (label,name,'omitted scenario output changed',delta)
    (out / (label + '-generation-digests.json')).write_text(json.dumps({f'{version}/{name}': files for (version,name),files in digests.items()},indent=2), encoding='utf-8')
    print('PASS actual generation: omitted inputs and unselected products unchanged:', label, flush=True)


def prove_receipts(repo, plan, scenarios, out, run):
    label = plan['Mode']
    # Scenarios whose full stages run through the generated project's own verify.ps1.
    verified = set(json.loads(subprocess.check_output(['pwsh', '-NoProfile', '-Command',
        ". ./scripts/template-matrix-scenarios.ps1; @($AllScenarios | Where-Object { $scenarioMap[$_].Verify }) | ConvertTo-Json -AsArray"],
        cwd=repo, text=True, encoding='utf-8')))
    assert verified, 'no scenario registered with Verify'
    expected_file = out / (label + '-expected.json')
    expected_file.write_text(json.dumps(plan), encoding='utf-8')
    receipt_dir = out / (label + '-receipts'); receipt_dir.mkdir(exist_ok=True)
    receipts = {}
    for group in plan['Slices']:
        receipt = receipts[group['key']] = dict(Version=2,Tier='pr',Slice=group['key'],CandidateSha=plan['CandidateSha'],Mode=label,Results=[])
        for name in group['Scenarios']:
            result = dict(Scenario=name, Container='pass' if name in group['Containers'] else 'skipped',
                          Verify='pass' if label == 'full' and name in verified else 'not-run')
            for stage in ['Backend','Runtime','Lint','Frontend','Test']:
                omitted = (label == 'frontend' and stage in ['Backend','Runtime']) or ((label == 'backend' or not scenarios[name].get('Frontend', True)) and stage in ['Lint','Frontend','Test'])
                result[stage] = 'not-applicable' if omitted else 'pass'
            receipt['Results'].append(result)
    def write():
        for key, receipt in receipts.items(): (receipt_dir / f'matrix-{key}.json').write_text(json.dumps(receipt), encoding='utf-8')
    write()
    command=['pwsh','-NoProfile','-File','scripts/check-template-matrix-results.ps1','-ResultsPath',str(receipt_dir),'-Tier','pr','-ValidationPlanPath',str(expected_file)]
    run(label+'-receipts-valid',command,repo)
    first = next(iter(receipts.values()))
    for field,bad in [('Version',1),('CandidateSha','b'*40),('Mode','backend' if label == 'full' else 'full'),('Tier','full'),('Slice','unregistered')]:
        saved=first[field];first[field]=bad;write();run(label+'-reject-'+field,command,repo,False);first[field]=saved
    result=first['Results'][0]
    for stage in ['Backend','Runtime','Lint','Frontend','Test']:
        saved=result[stage]
        for bad in ['skipped','failure','', 'pass' if saved=='not-applicable' else 'not-applicable']:
            result[stage]=bad;write();run(f'{label}-reject-{stage}-{bad or "missing"}',command,repo,False)
        result[stage]=saved
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
    assert label != 'full' or any(r['Verify'] == 'pass' for r in samples), 'full fixture lacks a Verify scenario'
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
    for mutation,apply in mutations.items():
        invalid = json.loads(json.dumps(plan));apply(invalid);expected_file.write_text(json.dumps(invalid), encoding='utf-8')
        run(label+'-reject-plan-'+mutation,command,repo,False)
        if mutation in ('reordered-members', 'moved-plan-members'):
            assert 'preserve registered group members and order' in (out/(label+'-reject-plan-'+mutation+'.log')).read_text(encoding='utf-8')
    expected_file.write_text(json.dumps(plan), encoding='utf-8')
    if label == 'full':
        pure_api = next(result for receipt in receipts.values() for result in receipt['Results'] if not scenarios[result['Scenario']].get('Frontend', True))
        for stage in ['Lint', 'Frontend', 'Test']:
            assert pure_api[stage] == 'not-applicable'
            pure_api[stage] = 'pass'; write()
            run(f'{label}-reject-pure-api-{stage}-claimed-pass', command, repo, False)
            pure_api[stage] = 'not-applicable'
    write();run(label+'-receipts-restored',command,repo)
    # Old full checker/entry must reject manual/claimed skips too.
    run(label+'-manual-contract-rejects-ci-groups',command[:-2],repo,False)


def prove_scheduling(planner, scenarios):
    # Preserve registered allocation and order, including a partial logical group.
    sample = dict(Titles={'pr':{'first':'first','second':'second','third':'third'}}, Containers=['heavy'])
    synthetic = {name:dict(sample, Slices={'pr':group})
                 for name,group in [('heavy','first'),('b','first'),('a','second'),('c','third')]}
    groups = planner.execution_slices(synthetic,list(synthetic),'pr','full',True)
    assert [g['Scenarios'] for g in groups] == [['heavy','b'],['a'],['c']]
    assert [g['Containers'] for g in groups] == [['heavy'],[],[]]
    assert all(any(name in group['title'] for name in group['Scenarios']) for group in groups)
    assert all(('含容器' in group['title']) == bool(group['Containers']) for group in groups)
    assert groups == planner.execution_slices(synthetic,list(reversed(synthetic)),'pr','full',True)
    for mode in ('full','frontend','backend'):
        partial = planner.execution_slices(synthetic,['heavy','b'],'pr',mode,False)
        assert len(partial) == 1 and partial[0]['Scenarios'] == ['heavy','b']
        assert partial[0]['Containers'] == []
        split = planner.execution_slices(synthetic,['b','c'],'pr',mode,False)
        assert [g['key'] for g in split] == ['execution-01','execution-02']
        assert [g['Scenarios'] for g in split] == [['b'],['c']]
    assert planner.execution_slices(synthetic,[],'pr','full',False) == []
    for tier in ('pr','full'):
        selected = [n for n,info in scenarios.items() if tier in info['Slices']]
        groups = planner.execution_slices(scenarios,selected,tier,'full',True)
        titles = next(iter(scenarios.values()))['Titles'][tier]
        assert [g['Scenarios'] for g in groups] == [[n for n in selected if scenarios[n]['Slices'][tier] == logical] for logical in titles]
        assert {n for g in groups for n in g['Scenarios']} == set(selected)
    print('PASS registered allocation: candidate containers, partial groups, no empty groups and full coverage',flush=True)


if __name__ == '__main__': main()
