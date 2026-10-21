#!/usr/bin/env pwsh
param([Parameter(Mandatory)][string]$ProjectsPath)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'generated-project-checks.ps1')
$projects = @(Get-Content -LiteralPath $ProjectsPath -Raw | ConvertFrom-Json)
if ($projects.Count -eq 0) { throw 'No generated documentation projects.' }
foreach ($project in $projects) { Assert-GeneratedDocumentation $project }
Write-Host "Generated documentation contracts passed: $($projects.Count) projects."
