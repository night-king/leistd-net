#!/usr/bin/env pwsh

<#
.SYNOPSIS
    以真实 NuGet restore 验证候选包身份的正反例（candidate-feed.ps1）。

.DESCRIPTION
    用手工生成的同 ID、同版本、内容不同的两个包分别充当候选包源与 nuget.org（第二个本地目录源），
    不访问网络、不发布任何包：
      - 正例：外层 Directory.Build.rsp 的候选版本覆盖项目内的字面版本，restore 只从候选源取包，来源断言通过；
      - 候选源缺包：packageSourceMapping 让 restore 失败，不回落到「nuget.org」；去掉 mapping 的对照组会回落；
      - 解包目录里有非候选版本或来自其他源的 Leistd 包时断言拒绝；没有 Leistd 包时断言不得空过；
      - 包源缺少 candidate.json 时读取即失败。
    修改 candidate-feed.ps1、各消费脚本的 NuGet 配置或版本覆盖方式时运行。
#>

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
. (Join-Path $PSScriptRoot "candidate-feed.ps1")
$tempRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp"))
$root = Join-Path $tempRoot ("candidate-feed-selftest/{0}-{1}" -f $PID, (Get-Date -Format "yyyyMMddHHmmssfff"))
if (-not $root.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to use path outside .tmp: $root" }
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$packageId = "Leistd.CandidateFixture"
$version = "0.0.1-local.selftest"
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0

function New-FixturePackage([string]$Directory, [string]$Marker) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $path = Join-Path $Directory "$packageId.$version.nupkg"
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry("$packageId.nuspec")
        $writer = [IO.StreamWriter]::new($entry.Open(), $utf8)
        try {
            $writer.Write("<?xml version=`"1.0`" encoding=`"utf-8`"?><package xmlns=`"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd`"><metadata>" +
                "<id>$packageId</id><version>$version</version><authors>fixture</authors><description>$Marker</description></metadata></package>")
        }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

function New-NuGetConfig([string]$Name, [string]$PackagesRoot, [hashtable]$Sources, [switch]$Mapping) {
    $sourceLines = foreach ($key in $Sources.Keys | Sort-Object) { "    <add key=`"$key`" value=`"$([Security.SecurityElement]::Escape($Sources[$key]))`" />" }
    $mappingBlock = if ($Mapping) {
        "  <packageSourceMapping>`n    <packageSource key=`"local-feed`"><package pattern=`"Leistd.*`" /></packageSource>`n" +
        "    <packageSource key=`"nuget.org`"><package pattern=`"*`" /></packageSource>`n  </packageSourceMapping>`n"
    } else { "" }
    $path = Join-Path $root "$Name.NuGet.Config"
    [IO.File]::WriteAllText($path, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config><add key="globalPackagesFolder" value="$([Security.SecurityElement]::Escape($PackagesRoot))" /></config>
  <packageSources>
    <clear />
$($sourceLines -join "`n")
  </packageSources>
$mappingBlock</configuration>
"@, $utf8)
    return $path
}

# 与生成项目同形：项目内 Directory.Build.props 写死旧的字面版本，候选版本只能经外层 Directory.Build.rsp 进来
function New-Consumer([string]$Name, [switch]$NoReference) {
    $directory = Join-Path $root "consumers/$Name/backend"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory "Directory.Build.props"),
        "<Project><PropertyGroup><LeistdFrameworkVersion>0.0.0</LeistdFrameworkVersion></PropertyGroup></Project>`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $directory "Consumer.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>$(if (-not $NoReference) { "<PackageReference Include=`"$packageId`" Version=`"`$(LeistdFrameworkVersion)`" />" })</ItemGroup>
</Project>
"@, $utf8)
    Set-CandidateVersionOverride (Join-Path $root "consumers/$Name") $version
    return $directory
}

# 与 verify.ps1 一样在项目目录里起子进程，不传版本参数
function Invoke-Restore([string]$Directory, [string]$Config) {
    Push-Location $Directory
    try {
        $output = (& dotnet restore Consumer.csproj --configfile $Config --force 2>&1 | Out-String)
        return [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally { Pop-Location }
}

function Assert-Throws([string]$Label, [scriptblock]$Action, [string]$Expected) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Expected)) { throw "${Label}：失败原因不对，期望包含「$Expected」，实际：$($_.Exception.Message)" }
        Write-Host "PASS $Label（$Expected）" -ForegroundColor Green
        $script:passed++
        return
    }
    throw "${Label}：本应失败却通过"
}

function Write-Pass([string]$Label) {
    Write-Host "PASS $Label" -ForegroundColor Green
    $script:passed++
}

try {
    $feed = Join-Path $root "candidate-feed"
    $online = Join-Path $root "fake-nuget-org"
    New-FixturePackage $feed "candidate"
    New-FixturePackage $online "published"
    Write-CandidateManifest $feed $version | Out-Null
    $manifest = Read-CandidateManifest $feed

    # 正例：候选版本经外层 rsp 到达 restore，包来自候选源
    $packages = Join-Path $root "packages-positive"
    $config = New-NuGetConfig "positive" $packages @{ "local-feed" = $feed; "nuget.org" = $online } -Mapping
    $consumer = New-Consumer "positive"
    $restore = Invoke-Restore $consumer $config
    if ($restore.ExitCode -ne 0) { throw "正例 restore 失败：`n$($restore.Output)" }
    $assets = Get-Content (Join-Path $consumer "obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    if (-not $assets.libraries.ContainsKey("$packageId/$version")) { throw "外层 Directory.Build.rsp 的候选版本没有覆盖项目内的字面版本" }
    Assert-CandidatePackages $packages $manifest
    Write-Pass "候选版本经外层 Directory.Build.rsp 覆盖、restore 只取候选源、来源断言通过"
    $stale = Join-Path $packages "leistd.stale/0.0.1"
    New-Item -ItemType Directory -Path $stale -Force | Out-Null
    Assert-Throws "解包目录里有非候选版本的 Leistd 包" { Assert-CandidatePackages $packages $manifest } "不是候选版本"
    Remove-Item -LiteralPath (Split-Path -Parent $stale) -Recurse -Force

    # 反例 1：候选源缺包时 restore 失败，不回落到「nuget.org」
    $emptyFeed = Join-Path $root "empty-feed"
    New-Item -ItemType Directory -Path $emptyFeed -Force | Out-Null
    $packages = Join-Path $root "packages-missing"
    $config = New-NuGetConfig "missing" $packages @{ "local-feed" = $emptyFeed; "nuget.org" = $online } -Mapping
    $consumer = New-Consumer "missing"
    $restore = Invoke-Restore $consumer $config
    if ($restore.ExitCode -eq 0 -or $restore.Output -notmatch 'NU110[12]') { throw "候选源缺包时 restore 应以 NU1101/NU1102 失败：`n$($restore.Output)" }
    if (Test-Path (Join-Path $packages $packageId.ToLowerInvariant())) { throw "候选源缺包时仍从「nuget.org」解出了包" }
    Write-Pass "候选源缺包：mapping 让 restore 失败，没有回落到「nuget.org」"
    # 对照组：同样的源去掉 mapping 就会回落，证明上面的失败来自 mapping 而不是夹具
    $packages = Join-Path $root "packages-unmapped"
    $config = New-NuGetConfig "unmapped" $packages @{ "local-feed" = $emptyFeed; "nuget.org" = $online }
    $unmappedConsumer = New-Consumer "unmapped"
    $restore = Invoke-Restore $unmappedConsumer $config
    $metadata = Join-Path $packages "$($packageId.ToLowerInvariant())/$version/.nupkg.metadata"
    if ($restore.ExitCode -ne 0 -or -not (Test-Path $metadata) -or
        (Get-NormalizedPath (Get-Content $metadata -Raw | ConvertFrom-Json).source) -cne (Get-NormalizedPath $online)) {
        throw "对照组无效：没有 mapping 时 restore 应回落到「nuget.org」：`n$($restore.Output)"
    }
    Write-Pass "对照组：去掉 mapping 时 restore 回落到「nuget.org」"

    # 反例 2：解包目录里预置来自「nuget.org」的同版本包（mapping 被绕过），来源断言拒绝
    $packages = Join-Path $root "packages-preseeded"
    $seedConfig = New-NuGetConfig "seed" $packages @{ "nuget.org" = $online }
    $restore = Invoke-Restore (New-Consumer "seed") $seedConfig
    if ($restore.ExitCode -ne 0) { throw "预置「nuget.org」包失败：`n$($restore.Output)" }
    $config = New-NuGetConfig "preseeded" $packages @{ "local-feed" = $feed; "nuget.org" = $online } -Mapping
    $restore = Invoke-Restore (New-Consumer "preseeded") $config
    if ($restore.ExitCode -ne 0) { throw "预置同版本包后 restore 应照常成功（mapping 被绕过），实际失败：`n$($restore.Output)" }
    Assert-Throws "解包目录预置同版本的「nuget.org」包" { Assert-CandidatePackages $packages $manifest } "不是候选包源"

    $emptyPackages = Join-Path $root "packages-empty"
    $config = New-NuGetConfig "noreference" $emptyPackages @{ "local-feed" = $feed; "nuget.org" = $online } -Mapping
    $restore = Invoke-Restore (New-Consumer "noreference" -NoReference) $config
    if ($restore.ExitCode -ne 0) { throw "无包引用的对照项目 restore 失败：`n$($restore.Output)" }
    Assert-Throws "解包目录没有 Leistd 包" { Assert-CandidatePackages $emptyPackages $manifest } "无从核对"

    Assert-Throws "包源缺少 candidate.json" { Read-CandidateManifest $online } "缺少 candidate.json"
    # 候选前缀取 VERSION 的下一补丁
    $versionRoot = Join-Path $root "version-fixture"
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $versionRoot "VERSION"), "0.12.9`n", $utf8)
    if ((Get-CandidateVersionPrefix $versionRoot) -cne "0.12.10") { throw "候选前缀不是 VERSION 的下一补丁" }
    Write-Pass "候选前缀为 VERSION 的下一补丁"
    [IO.File]::WriteAllText((Join-Path $versionRoot "VERSION"), "0.12", $utf8)
    Assert-Throws "VERSION 不是 x.y.z" { Get-CandidateVersionPrefix $versionRoot } "必须是 x.y.z"

    Write-Host "候选包身份正反例全部通过（$passed 例）。" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
