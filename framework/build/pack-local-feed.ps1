#!/usr/bin/env pwsh

<#
.SYNOPSIS
    把 Framework 打包成候选包源（默认 .tmp/local-feed），产出前先清空，并写入 candidate.json。

.DESCRIPTION
    直接 `dotnet pack -o .tmp/local-feed` 是**往目录里追加**。组件被删除后，
    它的旧 .nupkg 会一直留在这个持久目录里，于是隔离消费验证消费到一个仓库里
    已经不存在的包——它的传递依赖告警（例如已删组件带进来的漏洞）会被当成当前
    代码的问题去排查。CI 每次跑在全新 runner 上，所以这个坑只在本地踩。

    feed 是构建产物，构建产物应当每次从零重建，而不是累积。因此清空由本脚本
    负责，而不是靠人记得多敲一条删除命令。

    候选版本为 <VERSION 的下一补丁>-<VersionSuffix>：带预发布后缀，永不等于已发布版本，
    消费方不会因为同版本号拿到 nuget.org 上的包或全局解包目录里的旧内容。
    candidate.json 记录候选版本与打包所用 SDK，消费方据此传入版本、断言来源（见 candidate-feed.ps1）。

.PARAMETER VersionSuffix
    预发布后缀，不带前导连字符。CI 传 ci.<run_id>.<attempt>；本地默认 local.<UTC 时间戳>。

.PARAMETER OutputPath
    包源目录，必须位于仓库 .tmp 下。矩阵与端到端脚本自行打包时传各自 run 目录下的路径。
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$')]
    [string]$VersionSuffix = "local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot "candidate-feed.ps1")
$feedRoot = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath, $repoRoot) } else { Join-Path $repoRoot ".tmp/local-feed" }

# 只允许动 .tmp 下的路径：删除是不可逆操作，断言的成本远低于一次误删
$resolvedFeed = [IO.Path]::GetFullPath($feedRoot)
$tempPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp")).TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $resolvedFeed.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to modify path outside .tmp: $resolvedFeed"
}

if (Test-Path -LiteralPath $resolvedFeed) {
    Remove-Item -LiteralPath $resolvedFeed -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedFeed -Force | Out-Null

$version = "$(Get-CandidateVersionPrefix $repoRoot)-$VersionSuffix"
# 在仓库根执行：SDK 按当前目录解析根 global.json，candidate.json 记录的就是这次打包用的 SDK
Push-Location $repoRoot
try {
    dotnet pack (Join-Path $repoRoot "framework/Leistd.Framework.slnx") -c $Configuration -o $resolvedFeed -p:Version=$version
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack 失败（退出码 $LASTEXITCODE）"
    }
    $candidate = Write-CandidateManifest $resolvedFeed $version
}
finally {
    Pop-Location
}

Write-Host "已打包 $($candidate.Count) 个 $version 包到 $resolvedFeed（SDK $($candidate.Sdk)）" -ForegroundColor Green
