# 从测试输出的汇总行读出通过的用例数，供矩阵写入回执（BrowserTests / E2ETests）。
# 被 test-template-matrix.ps1 dot-source；判据自检 Test-TestCountSummary 由 check-all 执行。

# 一行去掉 ANSI 颜色后是哪种汇总、通过多少个；都不是时返回 $null。
# Vitest：`Tests  12 passed (12)`，前面可能有 `1 failed | ` 之类的分段。
# Playwright list 报告：`4 passed (13.9s)`，时长按 playwright-core 的 msToString 取 ms、s、m、h 之一。
function Get-TestCountSummary([string]$Line) {
    $plain = $Line -replace "`e\[[0-9;?]*[A-Za-z]", ''
    if ($plain -match '^\s*Tests\s+(?:\d+ \w+ \| )*(\d+) passed\b') { return [PSCustomObject]@{ Kind = 'vitest'; Passed = [int]$Matches[1] } }
    if ($plain -match '^\s*(\d+) passed \([\d.]+(?:ms|s|m|h)\)\s*$') { return [PSCustomObject]@{ Kind = 'playwright'; Passed = [int]$Matches[1] } }
    return $null
}

function Test-TestCountSummary {
    $cases = @(
        @{ Line = '      Tests  1283 passed (1283)'; Kind = 'vitest'; Passed = 1283 }
        @{ Line = "`e[2m      Tests `e[22m `e[1m`e[32m1283 passed`e[39m`e[22m`e[90m (1283)`e[39m"; Kind = 'vitest'; Passed = 1283 }
        @{ Line = '      Tests  1 failed | 12 passed (13)'; Kind = 'vitest'; Passed = 12 }
        @{ Line = '  4 passed (13.9s)'; Kind = 'playwright'; Passed = 4 }
        @{ Line = '  4 passed (850ms)'; Kind = 'playwright'; Passed = 4 }
        @{ Line = '  37 passed (1.2m)'; Kind = 'playwright'; Passed = 37 }
        @{ Line = '  120 passed (1.1h)'; Kind = 'playwright'; Passed = 120 }
        @{ Line = ' Test Files  120 passed (120)'; Kind = $null }
        @{ Line = 'Passed!  - Failed:     0, Passed:   223, Skipped:     0, Total:   223'; Kind = $null }
        @{ Line = '  3 passed (13.9s) and more'; Kind = $null }
    )
    $failures = @()
    foreach ($case in $cases) {
        $result = Get-TestCountSummary $case.Line
        $ok = if ($null -eq $case.Kind) { $null -eq $result } else { $result -and $result.Kind -ceq $case.Kind -and $result.Passed -eq $case.Passed }
        if (-not $ok) { $failures += "「$($case.Line -replace "`e", '\e')」解析为 $(if ($result) { "$($result.Kind) $($result.Passed)" } else { '无' })" }
    }
    if ($failures) { $failures | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 测试汇总行解析自检通过（$($cases.Count) 例）。" -ForegroundColor Green
    return $true
}
