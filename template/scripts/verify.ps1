#!/usr/bin/env pwsh

<#
.SYNOPSIS
    完整回归的唯一入口：本地与 CI 执行同一组步骤。

.DESCRIPTION
    步骤与 docs/standards/testing.md §1.1 的完整回归一致：随包静态检查、后端还原与构建（警告即错误）、
    各测试项目（集成测试经 Testcontainers 启动 PostgreSQL，需要可用的 Docker），以及前端生产依赖审计、安装、lint、测试与构建。
    按顺序执行，任一步失败即停止，并以该步的退出码退出；依赖审计查询走网络，失败时有限重试。
    运行前提：.NET SDK、Python 3、Docker；有前端时另需 Node.js 与 Playwright Chromium（npx playwright install chromium）。

.PARAMETER List
    只输出步骤清单（每行“步骤名<TAB>工作目录<TAB>命令”），不执行。
#>
param(
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = if ($IsWindows) { 'py' } else { 'python3' }

function Step([string]$Name, [string]$Directory, [string]$Command, [string[]]$Arguments, [int]$Attempts = 1) {
    [PSCustomObject]@{ Name = $Name; Directory = $Directory; Command = $Command; Arguments = $Arguments; Attempts = $Attempts }
}

$steps = [Collections.Generic.List[object]]::new()
$steps.Add((Step 'check-error-codes' '.' $python @('scripts/check-error-codes.py')))
#if (IncludeLocalization)
$steps.Add((Step 'check-i18n' '.' $python @('scripts/check-i18n.py')))
#endif
#if (SpaFrontend && IncludeOperationRecords)
$steps.Add((Step 'check-operation-action-i18n' '.' $python @('scripts/check-operation-action-i18n.py')))
#endif
$steps.Add((Step 'check-test-diagnostics' '.' $python @('scripts/check-test-diagnostics.py')))
$steps.Add((Step 'backend-restore' 'backend' 'dotnet' @('restore', 'CompanyName.ProjectName.sln')))
$steps.Add((Step 'backend-build' 'backend' 'dotnet' @('build', 'CompanyName.ProjectName.sln', '-c', 'Release', '--no-restore', '-p:TreatWarningsAsErrors=true')))
# 单元测试先于集成测试：前者秒级，失败时不必再等容器。新增的测试项目自动纳入。
$testProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'backend/tests') -Filter '*.csproj' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Sort-Object @{ Expression = { $_.BaseName -notlike '*.UnitTests' } }, BaseName
)
if ($testProjects.Count -eq 0) { throw 'No test project found under backend/tests.' }
foreach ($project in $testProjects) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $root 'backend'), $project.FullName).Replace('\', '/')
    $steps.Add((Step "backend-test:$($project.BaseName)" 'backend' 'dotnet' @('test', $relative, '-c', 'Release', '--no-build')))
}
#if (SpaFrontend)
# 门槛是生产依赖无 high/critical；审计查询走网络，失败时有限重试（退出码分不出网络错误与漏洞，真有漏洞时重试照样失败）。
$steps.Add((Step 'frontend-audit' 'frontend' 'npm' @('audit', '--omit=dev', '--audit-level=high', '--package-lock-only') 3))
$steps.Add((Step 'frontend-install' 'frontend' 'npm' @('ci')))
$steps.Add((Step 'frontend-lint' 'frontend' 'npm' @('run', 'lint')))
$steps.Add((Step 'frontend-test' 'frontend' 'npm' @('test', '--', '--watch=false')))
$steps.Add((Step 'frontend-build' 'frontend' 'npm' @('run', 'build')))
#endif

if ($List) {
    foreach ($step in $steps) {
        "{0}`t{1}`t{2} {3}" -f $step.Name, $step.Directory, $step.Command, ($step.Arguments -join ' ')
    }
    exit 0
}

$started = [Diagnostics.Stopwatch]::StartNew()
foreach ($step in $steps) {
    Write-Host "==> [$($step.Name)] $($step.Command) $($step.Arguments -join ' ')" -ForegroundColor Cyan
    for ($attempt = 1; $attempt -le $step.Attempts; $attempt++) {
        Push-Location -LiteralPath (Join-Path $root $step.Directory)
        try {
            & $step.Command @($step.Arguments)
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        if ($exitCode -eq 0 -or $attempt -eq $step.Attempts) { break }
        Write-Host "Step '$($step.Name)' failed (exit code $exitCode); retrying ($($attempt + 1)/$($step.Attempts))." -ForegroundColor Yellow
        Start-Sleep -Seconds (5 * $attempt)
    }
    if ($exitCode -ne 0) {
        Write-Host "Verification failed at step '$($step.Name)' (exit code $exitCode); remaining steps were not run." -ForegroundColor Red
        exit $exitCode
    }
}
Write-Host ("Verification passed: {0} steps in {1:hh\:mm\:ss}." -f $steps.Count, $started.Elapsed) -ForegroundColor Green
