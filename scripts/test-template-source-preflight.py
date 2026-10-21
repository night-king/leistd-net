#!/usr/bin/env python3
"""Prove CI preflight replacement with real checkers in an isolated snapshot.

Run when changing source-preflight ownership, not for every business feature.
Logs and command durations remain under .tmp; no package/build/database work is needed.
"""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / '.tmp/source-preflight-evidence')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    records = []
    with tempfile.TemporaryDirectory(prefix='leistd-preflight-') as directory:
        repo = Path(directory)
        tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
        for name in filter(None, tracked):
            source = ROOT / name
            if source.is_file():
                target = repo / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        for command in (['git', 'init', '-q'], ['git', 'add', '.']):
            subprocess.run(command, cwd=repo, check=True)

        # Extract the actual functions with PowerShell's parser, not a test implementation.
        runner = repo / 'preflight-functions.ps1'
        runner.write_text('''#!/usr/bin/env pwsh
param([switch]$Skip)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts/test-template-matrix.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Matrix script parse failed: $errors" }
foreach ($name in @('Invoke-External', 'Get-Python3Command', 'Invoke-SourcePreflight')) {
    $fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $fn) { throw "Missing actual function: $name" }
    . ([scriptblock]::Create($fn.Extent.Text))
}
Invoke-SourcePreflight -Skip:$Skip
''', encoding='utf-8')

        def run(label, command, success, diagnostic=None, ci=False):
            env = dict(os.environ, GITHUB_ACTIONS='true' if ci else 'false')
            started = time.monotonic()
            result = subprocess.run(command, cwd=repo, env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
            log = result.stdout + result.stderr
            (output / (label + '.log')).write_text(log, encoding='utf-8')
            records.append({'label': label, 'command': command, 'seconds': round(time.monotonic() - started, 3),
                            'exit_code': result.returncode, 'expected_success': success})
            (output / 'results.json').write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding='utf-8')
            assert (result.returncode == 0) == success, (label, result.returncode, log[-2500:])
            if diagnostic:
                assert diagnostic in log, (label, 'expected real diagnostic missing', log[-2500:])
            print(f'PASS {label}: exit={result.returncode}', flush=True)
            return log

        entry = ['pwsh', '-NoProfile', '-File', str(runner)]
        for number in range(3):
            run(f'legal-default-{number + 1}', entry, True)
            log = run(f'legal-ci-skip-{number + 1}', entry + ['-Skip'], True, ci=True)
            assert '> pwsh' not in log and '> python' not in log, 'CI repeated production scans'
        run('manual-skip-rejected', entry + ['-Skip'], False, '-SkipSourcePreflight')
        run('legal-static', ['pwsh', '-NoProfile', '-File', 'scripts/check-all.ps1'], True)

        mutations = [
            ('symbols', 'template/backend/src/CompanyName.ProjectName.Api/QualityPreflightFixture.cs',
             '// #if (UnknownQualitySymbol)\n// #endif\n', 'UnknownQualitySymbol',
             ['pwsh', '-NoProfile', '-File', 'scripts/check-template-symbols.ps1']),
            ('using', 'template/backend/src/CompanyName.ProjectName.Api/QualityPreflightFixture.csproj',
             '<Project>\n<!--#if (IncludeLocalization)-->\n<ItemGroup>\n<InternalsVisibleTo Include="Fixture" />\n</ItemGroup>\n<!--#endif-->\n</Project>\n',
             'InternalsVisibleTo', [sys.executable, 'scripts/check-using-guards.py']),
            ('async', 'template/backend/src/CompanyName.ProjectName.Infrastructure/TenantConnections/QualityPreflightFixture.cs',
             'internal static class QualityPreflightFixture { internal static void Block() { System.Threading.Tasks.Task.Delay(1).GetAwaiter().GetResult(); } }\n',
             'GetAwaiter().GetResult()', [sys.executable, 'scripts/check-async-boundaries.py']),
        ]
        for name, path, content, diagnostic, original in mutations:
            target = repo / path
            assert not target.exists(), ('fixture would overwrite snapshot file', path)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding='utf-8')
            subprocess.run(['git', 'add', path], cwd=repo, check=True)
            try:
                run(name + '-original-checker', original, False, diagnostic)
                # Actual default matrix must fail before audit, pack, generation or build.
                log = run(name + '-manual-matrix', ['pwsh', '-NoProfile', '-File', 'scripts/test-template-matrix.ps1',
                                                  '-Scenarios', 'identity', '-SkipPack', '-SkipFrontend', '-SkipRuntime'], False, diagnostic)
                assert '> dotnet' not in log and '> npm' not in log, 'preflight did not fail before expensive work'
                run(name + '-replacement-static', ['pwsh', '-NoProfile', '-File', 'scripts/check-all.ps1'], False, diagnostic)
                # CI slice may continue; it must not pretend to have rescanned the source.
                log = run(name + '-ci-skip', entry + ['-Skip'], True, ci=True)
                assert '> pwsh' not in log and '> python' not in log
            finally:
                target.unlink()
                subprocess.run(['git', 'rm', '--cached', '-q', path], cwd=repo, check=True)
            run(name + '-restored', original, True)

        gates = repo / 'scripts/check-all.ps1'
        original_gates = gates.read_text(encoding='utf-8')
        for missing in ('check-template-symbols.ps1', 'check-using-guards.py', 'check-async-boundaries.py'):
            gates.write_text('\n'.join(line for line in original_gates.splitlines() if f'scripts/{missing}' not in line) + '\n', encoding='utf-8')
            run('missing-' + missing, entry + ['-Skip'], False, missing, ci=True)
        gates.write_text(original_gates, encoding='utf-8')
        run('restored-ci-list', entry + ['-Skip'], True, ci=True)
        run('restored-static', ['pwsh', '-NoProfile', '-File', 'scripts/check-all.ps1'], True)

    # Test the actual workflow aggregator: failed static scans cannot become CI green.
    spec = importlib.util.spec_from_file_location('workflow_scope', ROOT / 'scripts/test-workflow-change-scope.py')
    scopes = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(scopes)
    scopes.check_quality_aggregation()
    print(f'All preflight replacement and mutation evidence: {output}', flush=True)


if __name__ == '__main__':
    main()
