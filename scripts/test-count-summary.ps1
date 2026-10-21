# 从测试输出的汇总行读出最终通过的用例数，供矩阵写入回执（BrowserTests / E2ETests）。
# 被 test-template-matrix.ps1 dot-source；判据自检 Test-TestCountSummary 由 check-all 执行。

# 一行去掉 ANSI 颜色后是哪种汇总、最终通过多少个；都不是时返回 $null。
# Vitest：`Tests  12 passed (12)`，前面可能有 `1 failed | ` 之类的分段。
# Playwright list 报告：`4 passed (13.9s)`，时长按 playwright-core 的 msToString 取 ms、s、m、h 之一；
# 重试后才通过的用例另起一行 `1 flaky`（全部 flaky 时没有 passed 行）。failed、interrupted、skipped、did not run 不计。
function Get-TestCountSummary([string]$Line) {
    $plain = $Line -replace "`e\[[0-9;?]*[A-Za-z]", ''
    if ($plain -match '^\s*Tests\s+(?:\d+ \w+ \| )*(\d+) passed\b') { return [PSCustomObject]@{ Kind = 'vitest'; Passed = [int]$Matches[1] } }
    if ($plain -match '^\s*(\d+) passed \([\d.]+(?:ms|s|m|h)\)\s*$') { return [PSCustomObject]@{ Kind = 'playwright'; Passed = [int]$Matches[1] } }
    if ($plain -match '^\s*(\d+) flaky\s*$') { return [PSCustomObject]@{ Kind = 'playwright-flaky'; Passed = [int]$Matches[1] } }
    return $null
}

# 一次运行的全部输出 → 前端单测数与端到端用例数。退出码非零先失败，计数不能掩盖失败。
# 前端单测汇总必须恰好一条且 > 0。E2ETests 是最终通过的逻辑用例数：passed 加 flaky，每个用例只算一次（不按尝试次数）；
# 期望端到端时两种行各至多一条、合计 > 0，不期望时一条都不能有。
function Get-BrowserTestCounts([string[]]$Lines, [int]$ExitCode, [bool]$ExpectE2E) {
    if ($ExitCode -ne 0) { throw "Command failed with exit code $ExitCode." }
    $summaries = @($Lines | ForEach-Object { Get-TestCountSummary $_ } | Where-Object { $_ })
    $browser = @($summaries | Where-Object Kind -ceq 'vitest')
    $passed = @($summaries | Where-Object Kind -ceq 'playwright')
    $flaky = @($summaries | Where-Object Kind -ceq 'playwright-flaky')
    if ($browser.Count -ne 1 -or $browser[0].Passed -le 0) {
        throw "Frontend tests reported no executed test count (summaries: $($browser.Passed -join ', '))."
    }
    if (-not $ExpectE2E) {
        if ($passed.Count + $flaky.Count -ne 0) { throw "Unexpected E2E summary in a run without E2E tests: $(($passed + $flaky).Passed -join ', ')." }
        return [PSCustomObject]@{ Browser = $browser[0].Passed; E2E = 0; Flaky = 0 }
    }
    $e2e = ($passed + $flaky | Measure-Object -Property Passed -Sum).Sum
    if ($passed.Count -gt 1 -or $flaky.Count -gt 1 -or $e2e -le 0) {
        throw "E2E tests reported no executed test count (passed: $($passed.Passed -join ', '); flaky: $($flaky.Passed -join ', '))."
    }
    return [PSCustomObject]@{ Browser = $browser[0].Passed; E2E = [int]$e2e; Flaky = @($flaky.Passed + 0)[0] }
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
        @{ Line = '  1 flaky'; Kind = 'playwright-flaky'; Passed = 1 }
        @{ Line = "`e[33m  2 flaky`e[39m"; Kind = 'playwright-flaky'; Passed = 2 }
        @{ Line = '  1 failed'; Kind = $null }
        @{ Line = '  2 skipped'; Kind = $null }
        @{ Line = '    [chromium] › e2e/login.e2e.ts:5:1 › flaky login'; Kind = $null }
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
    # 整次运行的计数：E2E 是最终通过的逻辑用例数（passed + flaky），失败、跳过与非零退出不能变成计数
    $vitest = '      Tests  12 passed (12)'
    $runs = @(
        @{ Name = '普通成功'; Lines = @($vitest, '  4 passed (13.9s)'); Exit = 0; E2E = 4 }
        @{ Name = 'passed 与 flaky 混合'; Lines = @($vitest, '  1 flaky', '    [chromium] › e2e/login.e2e.ts:5:1 › login', '  3 passed (20.1s)'); Exit = 0; E2E = 4 }
        @{ Name = '全部 flaky'; Lines = @($vitest, '  2 flaky', '    [chromium] › e2e/a.e2e.ts:3:1 › a', '    [chromium] › e2e/b.e2e.ts:3:1 › b'); Exit = 0; E2E = 2 }
        @{ Name = '重试耗尽'; Lines = @($vitest, '  1 failed', '    [chromium] › e2e/login.e2e.ts:5:1 › login', '  3 passed (40.2s)'); Exit = 1; E2E = $null }
        @{ Name = '零用例'; Lines = @($vitest, 'Error: No tests found'); Exit = 1; E2E = $null }
        @{ Name = '全部跳过'; Lines = @($vitest, '  5 skipped'); Exit = 0; E2E = $null }
        @{ Name = '重复汇总'; Lines = @($vitest, '  2 passed (1.0s)', '  2 passed (1.0s)'); Exit = 0; E2E = $null }
    )
    foreach ($run in $runs) {
        $counted = try { (Get-BrowserTestCounts $run.Lines $run.Exit $true).E2E } catch { $null }
        if ($counted -ne $run.E2E) { $failures += "运行「$($run.Name)」计为 $(if ($null -eq $counted) { '失败' } else { $counted })" }
    }
    $unexpected = try { Get-BrowserTestCounts @($vitest, '  1 flaky') 0 $false } catch { $null }
    if ($unexpected) { $failures += '不期望端到端时出现 flaky 汇总未被拒绝' }
    if ($failures) { $failures | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 测试汇总行解析自检通过（$($cases.Count) 行、$($runs.Count + 1) 次运行）。" -ForegroundColor Green
    return $true
}
