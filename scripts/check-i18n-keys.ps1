#!/usr/bin/env pwsh

<#
.SYNOPSIS
    i18n 词条一致性闸门。

.DESCRIPTION
    两段判据，各自只有一份实现：
      1. template/scripts/check-i18n.py —— 随模板分发、生成项目里同样可跑的项目判据：
         前端词条、静态引用与路由 scope 登记，后端资源、模板错误码与 DataAnnotations 键，写死的中文。
      2. scripts/check-i18n-repo.py —— 只在本仓成立的判据：框架组件译文与错误码、宿主不复制组件译文、
         不启用多语言时的英文表（两个分支只在模板源码里同时存在）。
    判据清单见两个脚本的文件头。退出码非 0 表示存在不一致，供 CI 阻断。

.PARAMETER SelfTest
    运行两段判据的正反例自检。
#>
[CmdletBinding()]
param(
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# 与 check-all.ps1 同一口径：3.10+。check-all 经环境变量把 EncodingWarning 设为错误传给这里起的子进程，
# 低于 3.10 的解释器会静默忽略该设置。
$python = $null
foreach ($candidate in @("python3", "python", "python3.14", "python3.13", "python3.12", "python3.11", "python3.10")) {
    $found = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { continue }
    $version = "$(& $found.Source -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null)".Trim()
    if ($LASTEXITCODE -eq 0 -and $version -and [version]$version -ge [version]"3.10") { $python = $found.Source; break }
}
if (-not $python) {
    throw "需要 Python 3.10 或更高版本（python3/python/python3.1x）。"
}

$checks = @(
    (Join-Path $repoRoot "template/scripts/check-i18n.py"),
    (Join-Path $PSScriptRoot "check-i18n-repo.py")
)
$failed = 0
foreach ($check in $checks) {
    if ($SelfTest) { & $python $check --self-test } else { & $python $check }
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
exit $failed
