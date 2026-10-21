# 候选包身份：本地包源里的 Leistd.* 包与同目录的 candidate.json（候选版本与打包所用 SDK）。
# 由 pack-local-feed.ps1 与发布作业写入；矩阵、PostgreSQL/OIDC 端到端、test-package-consumption.ps1、模板开发工作区、
# 模板同步验收与升级闸门点源本文件读取与断言。
#
# 候选版本号带唯一的预发布后缀（<VERSION 下一补丁>-ci.<run>.<attempt> 或 -local.<UTC 时间戳>），
# nuget.org 与之前的运行里都不会有同号包（发布作业的最终版本在全新的解包目录里消费）；packageSourceMapping 让 Leistd.* 只走本地源，缺包时 restore 失败
# 而不是回落到 nuget.org。restore 之后再核对一次解包目录里的 Leistd.* 都是候选版本、来自候选包源（Assert-CandidatePackages）。

$CandidateManifestName = 'candidate.json'

# 候选版本前缀：VERSION（已发布或正在发布的基准 x.y.z）的下一补丁。
# 预发布后缀排在该补丁的正式版之前、当前基准之后。
function Get-CandidateVersionPrefix([Parameter(Mandatory)][string]$RepoRoot) {
    $base = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -Raw).Trim()
    if ($base -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "VERSION 必须是 x.y.z：'$base'" }
    return '{0}.{1}.{2}' -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
}

function Get-NormalizedPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

# 记录候选版本与打包时实际使用的 SDK 版本。包源里只能有这一个版本的包。
function Write-CandidateManifest {
    param(
        [Parameter(Mandatory)][string]$FeedPath,
        [Parameter(Mandatory)][string]$Version
    )
    $feed = Get-NormalizedPath $FeedPath
    $files = @(Get-ChildItem -LiteralPath $feed -File -Filter '*.nupkg')
    if ($files.Count -eq 0) { throw "包源里没有 nupkg：$feed" }
    $foreign = @($files | Where-Object { -not $_.Name.EndsWith(".$Version.nupkg", [StringComparison]::OrdinalIgnoreCase) })
    if ($foreign.Count -gt 0) { throw "包版本与候选版本 $Version 不符：$($foreign.Name -join ', ')" }
    # SDK 按目录解析 global.json：在包源所在目录取版本，与仓库内打包所用的一致
    Push-Location $feed
    try { $sdk = "$(& dotnet --version)".Trim() } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or -not $sdk) { throw '无法读取 dotnet --version' }
    $manifest = [ordered]@{ CandidateVersion = $Version; Sdk = $sdk }
    [IO.File]::WriteAllText((Join-Path $feed $CandidateManifestName), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    return [PSCustomObject]@{ CandidateVersion = $Version; Sdk = $sdk; Count = $files.Count }
}

function Read-CandidateManifest([Parameter(Mandatory)][string]$FeedPath) {
    $feed = Get-NormalizedPath $FeedPath
    $path = Join-Path $feed $CandidateManifestName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "包源缺少 $CandidateManifestName：$feed。用 framework/build/pack-local-feed.ps1 打包，或下载完整的候选包源。"
    }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ([string]::IsNullOrWhiteSpace($manifest.CandidateVersion) -or [string]::IsNullOrWhiteSpace($manifest.Sdk)) { throw "无效的 ${CandidateManifestName}：$path" }
    return [PSCustomObject]@{ FeedPath = $feed; CandidateVersion = [string]$manifest.CandidateVersion; Sdk = [string]$manifest.Sdk }
}

# 生成项目不改模板源：在生成目录的上层放 Directory.Build.rsp，MSBuild 对其下每次
# restore/build/test（包括生成项目 scripts/verify.ps1 起的子进程）自动带上这个全局属性，
# 覆盖生成项目 Directory.Build.props 里的 LeistdFrameworkVersion。
# -ImplicitRestore：调用方会跑不带 --no-restore 的 dotnet build/test/publish（工作区手工命令、Dockerfile）时使用。
# SDK 给隐式 restore 传 --restoreProperty，restore 阶段只认它、不认 -p，所以另补一行。默认不补：
# 用 --configfile 显式 restore 的调用方补了它反而会让 --configfile 失效（它也是以 -p 传入的）。
function Set-CandidateVersionOverride {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Version,
        [switch]$ImplicitRestore
    )
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $content = "-p:LeistdFrameworkVersion=$Version`n"
    if ($ImplicitRestore) { $content += "-restoreProperty:LeistdFrameworkVersion=$Version`n" }
    [IO.File]::WriteAllText((Join-Path $Directory 'Directory.Build.rsp'), $content, [Text.UTF8Encoding]::new($false))
}

# restore 之后核对本次隔离的全局解包目录：每个 Leistd.* 都必须是候选版本、来自候选包源（读 .nupkg.metadata）。
# 一个 Leistd 包都没有时判据失效，同样失败。
function Assert-CandidatePackages {
    param(
        [Parameter(Mandatory)][string]$PackagesRoot,
        [Parameter(Mandatory)][object]$Manifest
    )
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $directories = @(Get-ChildItem -LiteralPath $PackagesRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -like 'leistd.*')
    if ($directories.Count -eq 0) { throw "全局解包目录里没有 Leistd.* 包，来源断言无从核对：$PackagesRoot" }
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($versionDirectory in @($directories | Get-ChildItem -Directory)) {
        $label = "$($versionDirectory.Parent.Name) $($versionDirectory.Name)"
        if ($versionDirectory.Name -ine $Manifest.CandidateVersion) { $problems.Add("$label 不是候选版本 $($Manifest.CandidateVersion)"); continue }
        $metadataPath = Join-Path $versionDirectory.FullName '.nupkg.metadata'
        $source = if (Test-Path -LiteralPath $metadataPath) { [string](Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json).source } else { '' }
        if (-not $source -or -not [string]::Equals((Get-NormalizedPath $source), $Manifest.FeedPath, $comparison)) {
            $problems.Add("$label 来源是 '$source'，不是候选包源 '$($Manifest.FeedPath)'")
        }
    }
    if ($problems.Count -gt 0) { throw "候选包来源断言失败：`n  $($problems -join "`n  ")" }
    Write-Host "候选包来源断言通过：$($directories.Count) 个 Leistd.* 包均为 $($Manifest.CandidateVersion)，来自 $($Manifest.FeedPath)。" -ForegroundColor Green
}
