#!/usr/bin/env pwsh

<#
.SYNOPSIS
    安装模板前端单测用的 Playwright Chromium，版本取模板锁文件，与生成项目实际安装的一致。
.DESCRIPTION
    版本直接读模板源码的两份锁文件（默认与多语言变体）：去掉 //#if、//#endif 指令行即是合法 JSON，
    不必为读一个版本号先生成项目。两份版本不一致、出现 //#else 类指令（去掉后会留下重复键）或取不到版本时失败。

    -VersionOnly 只输出版本与 apt 安装包缓存键（写入 GITHUB_OUTPUT 的 version、cache-key）。
    缓存键含 runner 镜像版本（ImageOS、ImageVersion 是 runner 的环境变量，工作流表达式取不到）：
    镜像或 Playwright 升级时换键，不会把不匹配的系统包带进新环境。

    安装时 --with-deps 经 apt 装系统依赖（字体、xvfb 等），慢的是从 Ubuntu 镜像下载这批 .deb；
    给 -AptCachePath 时先把缓存里的包放进 apt 的本地缓存目录，装完再把下载的包存回去，由工作流的
    actions/cache 保存。apt 按版本与校验和取用本地包，过期的包会被忽略并重新下载。
    浏览器本体不缓存：Playwright 官方不推荐，恢复缓存与直接下载耗时相当。
#>
param(
    [switch]$VersionOnly,
    [string]$AptCachePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Get-LockedPlaywrightVersion([string]$Path) {
    $lines = Get-Content -LiteralPath $Path
    $branching = @($lines | Where-Object { $_ -match '^\s*//#(?:else|elseif|elif)\b' })
    if ($branching.Count -gt 0) {
        throw "$Path 含分支指令，去掉指令行后不是唯一的 JSON：$($branching[0].Trim())"
    }

    $json = ($lines | Where-Object { $_ -notmatch '^\s*//#(?:if|endif)\b' }) -join "`n"
    $package = ($json | ConvertFrom-Json -AsHashTable).packages['node_modules/playwright']
    if (-not $package -or -not $package.version) { throw "$Path 里没有 playwright" }
    return $package.version
}

$lockFiles = @(
    "template/frontend/package-lock.json",
    "template/.template.config/localization/frontend/package-lock.json"
)
$versions = @($lockFiles | ForEach-Object { Get-LockedPlaywrightVersion (Join-Path $repoRoot $_) } | Select-Object -Unique)
if ($versions.Count -ne 1) {
    throw "两份模板锁文件的 playwright 版本不一致：$($versions -join ', ')"
}
$version = $versions[0]

if ($VersionOnly) {
    $cacheKey = "playwright-apt-$($env:ImageOS)-$($env:ImageVersion)-$version"
    if ($env:GITHUB_OUTPUT) {
        "version=$version" | Out-File $env:GITHUB_OUTPUT -Append
        "cache-key=$cacheKey" | Out-File $env:GITHUB_OUTPUT -Append
    }
    Write-Host "playwright $version（缓存键 $cacheKey）"
    return
}

$aptArchives = "/var/cache/apt/archives"
$useAptCache = $AptCachePath -and $IsLinux
if ($useAptCache) {
    New-Item -ItemType Directory -Path $AptCachePath -Force | Out-Null
    # apt-get 默认保留下载的包；显式写明，避免镜像改了默认值后缓存静默变空
    'APT::Keep-Downloaded-Packages "true";' | sudo tee /etc/apt/apt.conf.d/99keep-downloaded-packages | Out-Null
    $cached = @(Get-ChildItem -LiteralPath $AptCachePath -Filter *.deb -File)
    if ($cached.Count -gt 0) {
        sudo cp -n @($cached.FullName) $aptArchives
        if ($LASTEXITCODE -ne 0) { throw "恢复 apt 安装包缓存失败" }
    }
    Write-Host "apt 安装包缓存：恢复 $($cached.Count) 个"
}

npx --yes "playwright@$version" install --with-deps chromium
if ($LASTEXITCODE -ne 0) { throw "Chromium 安装失败" }

if ($useAptCache) {
    $downloaded = @(Get-ChildItem -LiteralPath $aptArchives -Filter *.deb -File)
    foreach ($package in $downloaded) {
        Copy-Item -LiteralPath $package.FullName -Destination $AptCachePath -Force
    }
    Write-Host "apt 安装包缓存：保存 $($downloaded.Count) 个"
}
