#!/usr/bin/env pwsh

<#
.SYNOPSIS
扫描已还原解决方案的 NuGet 漏洞公告（含传递依赖），有 high/critical 即失败。

.DESCRIPTION
构建时的 NuGet 审计（NU1901–NU1904）只告警，不阻断 PR（见 framework/common.props）；公告由定时检查用本脚本发现。
dotnet list package --vulnerable 查出漏洞时退出码仍为 0，所以按 JSON 输出的 severity 判定。
门槛与前端一致：high/critical 为 0；low/moderate 只列出。调用前先 restore（默认配置即可）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path
)

$ErrorActionPreference = 'Stop'
$blocking = @('High', 'Critical')
$found = [System.Collections.Generic.List[string]]::new()
foreach ($target in $Path) {
    $json = & dotnet list $target package --vulnerable --include-transitive --format json 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package 失败（退出码 $LASTEXITCODE）：$target`n$json" }
    $report = $json | ConvertFrom-Json
    $seen = @{}
    foreach ($project in @($report.projects)) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                foreach ($vulnerability in @($package.vulnerabilities)) {
                    if (-not $vulnerability) { continue }
                    $line = "$($package.id) $($package.resolvedVersion) [$($vulnerability.severity)] $($vulnerability.advisoryurl)"
                    if ($seen.ContainsKey($line)) { continue }
                    $seen[$line] = $true
                    Write-Host "  $line"
                    if ($vulnerability.severity -cin $blocking) { $found.Add("$target：$line") }
                }
            }
        }
    }
    Write-Host "已扫描 $target：$($seen.Count) 条公告。"
}
if ($found.Count -gt 0) {
    throw "NuGet 依赖有 high/critical 公告：`n$($found -join "`n")"
}
Write-Host '✅ NuGet 依赖无 high/critical 公告。'
