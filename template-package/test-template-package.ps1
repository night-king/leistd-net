#!/usr/bin/env pwsh

<#
.SYNOPSIS
    打包或检查 Leistd.Templates 模板包，并从隔离安装的包验证生成、来源标记与重建确定性。

.DESCRIPTION
    1. 未给 -PackagePath 时按 -Version 与当前 HEAD 打包（template-package/Leistd.Templates.csproj）。
    2. 包内容：content/fullstack-app/ 下的文件集合与 git ls-files template 完全一致；包内 template.json
       只有两个身份符号（TemplatePackageVersion / TemplateSourceSha）被写成本次的版本与源提交，其余与源码逐字节一致。
    3. 把 nupkg 装进隔离的模板目录（--debug:custom-hive），在仓库之外生成两个代表场景：
       identity-all-features（含前端、可选能力全开）与 resource-capabilities-03（纯 API）。
       生成目录不在仓库下，不继承仓库根的 global.json、NuGet.Config 与 Directory.Build.*，与真实用户一致。
    4. 来源标记逐项核对（scripts/template-matrix-scenarios.ps1 的 Assert-ScenarioMarker），并做反例：
       标记被改动、UserSecretsId 与项目不符时必须失败。
    5. 重建确定性：按标记用 template/scripts/template-marker.ps1 的 New-TemplateRebuild 重建，与原产物零差异；
       反例：不做 UserSecretsId 归一化时差异恰好是两个项目文件与标记，改动原产物任一文件时差异被检出。
    6. -NuGetOrgRestore：两个生成项目都只用 nuget.org（无本地源、独立包缓存、不用 HTTP 缓存）还原默认的
       LeistdFrameworkVersion；框架正式版刚推送时 nuget.org 建索引有延迟，只对"找不到 Leistd.* 的默认版本"
       （NU1102/NU1103）与取不到源（NU1301）在 -RestoreTimeoutMinutes 内重试，包 ID 不存在（NU1101）等其他失败立即失败。

    用于 CI 的模板生成对照片、release.yml 的 template 通道与本地首版自举验收；不发布任何内容。

.PARAMETER PackagePath
    已打好的 Leistd.Templates nupkg；此时 -Version 与 -SourceSha 是期望写入包内的身份。

.PARAMETER Version
    模板版本。打包时缺省为 0.0.1-local.<UTC 时间戳>。

.PARAMETER SourceSha
    期望的源提交，缺省为当前 HEAD。
#>
param(
    [string]$PackagePath,
    [string]$Version,
    [string]$SourceSha,
    [switch]$NuGetOrgRestore,
    [int]$RestoreTimeoutMinutes = 40,
    [string]$WorkRoot,
    [switch]$KeepWorkRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repoRoot 'scripts' 'template-matrix-scenarios.ps1')
. (Join-Path $repoRoot 'template' 'scripts' 'template-marker.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $output = & $Command @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$Command 失败（退出码 $LASTEXITCODE）：`n$($output -join "`n")" }
        return $output
    } finally { Pop-Location }
}

# 还原失败是否值得重试：只认"索引延迟"与网络瞬断，按 NuGet 错误码判定。
#   NU1102 / NU1103：找不到期望的 Leistd.* 版本（NU1103 是"只有预发布版"，首个正式版刚推送、尚未建好索引时也是它）→
#     每条都指向 Leistd.* 且请求的正是默认版本才重试；
#   NU1301：取不到源（网络）→ 重试；
#   NU1101（包 ID 在 nuget.org 不存在）、其他错误码或没有错误码 → 立即失败。
function Test-IndexingDelay([string[]]$Log, [string]$FrameworkVersion) {
    $errors = @($Log | ForEach-Object { [regex]::Matches($_, '\berror (NU\d{4}): ([^\[]*)') } | ForEach-Object {
        [pscustomobject]@{ Code = $_.Groups[1].Value; Text = $_.Groups[2].Value } })
    if ($errors.Count -eq 0) { return $false }
    $expected = "package Leistd\.\S+ with version \(>= $([regex]::Escape($FrameworkVersion))\)"
    foreach ($entry in $errors) {
        if ($entry.Code -cin @('NU1102', 'NU1103')) {
            # 续行 "- Found N version(s) ..." 与首行同码，不含包名
            if ($entry.Text -match '^\s*- ') { continue }
            if ($entry.Text -notmatch $expected) { return $false }
        } elseif ($entry.Code -cne 'NU1301') {
            return $false
        }
    }
    return $true
}

function Assert-Throws([string]$Label, [scriptblock]$Action) {
    try { & $Action } catch { Write-Host "  反例已拒绝：$Label" -ForegroundColor DarkGray; return }
    throw "反例未被拒绝：$Label"
}

# 重试分类器的回归夹具（不联网，先于打包执行）：只有"找不到默认版本的 Leistd.*"与取不到源才重试
$restoreProject = '/src/backend/src/Probe.Api/Probe.Api.csproj'
$indexingCases = @(
    @{ Name = 'NU1102 Leistd.* 默认版本 → 重试'; Expect = $true; Log = @(
        "$restoreProject : error NU1102: Unable to find package Leistd.Core with version (>= 0.12.0)",
        "$restoreProject : error NU1102:   - Found 12 version(s) in nuget.org [ Nearest version: 0.11.0 ]") }
    @{ Name = 'NU1103 只有预发布版 → 重试'; Expect = $true; Log = @("$restoreProject : error NU1103: Unable to find a stable package Leistd.Auditing.Core with version (>= 0.12.0)") }
    @{ Name = 'NU1301 取不到源 → 重试'; Expect = $true; Log = @("$restoreProject : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.") }
    @{ Name = 'NU1103 请求的不是默认版本 → 失败'; Expect = $false; Log = @("$restoreProject : error NU1103: Unable to find a stable package Leistd.Core with version (>= 0.11.5)") }
    @{ Name = 'NU1102 非 Leistd 包 → 失败'; Expect = $false; Log = @("$restoreProject : error NU1102: Unable to find package Serilog with version (>= 99.0.0)") }
    @{ Name = 'NU1101 包 ID 不存在 → 立即失败'; Expect = $false; Log = @("$restoreProject : error NU1101: Unable to find package Leistd.Missing. No packages exist with this id in source(s): nuget.org") }
    @{ Name = 'NU1103 与 NU1101 混合 → 失败'; Expect = $false; Log = @(
        "$restoreProject : error NU1103: Unable to find a stable package Leistd.Core with version (>= 0.12.0)",
        "$restoreProject : error NU1101: Unable to find package Leistd.Missing. No packages exist with this id in source(s): nuget.org") }
    @{ Name = '没有错误码 → 失败'; Expect = $false; Log = @('Restore failed.', 'Build FAILED.') }
)
$classifierFailures = @($indexingCases | Where-Object { (Test-IndexingDelay $_.Log '0.12.0') -ne $_.Expect } | ForEach-Object { $_.Name })
if ($classifierFailures) { throw "还原重试分类器夹具失败：$($classifierFailures -join '；')" }
Write-Host "还原重试分类器：$($indexingCases.Count) 个夹具通过"

if (-not $SourceSha) { $SourceSha = (Invoke-Checked 'git' @('rev-parse', 'HEAD') | Select-Object -Last 1).Trim() }
if ($SourceSha -notmatch '^[0-9a-f]{40}$') { throw "SourceSha 不是 40 位提交 SHA：$SourceSha" }
if (-not $WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) "leistd-template-package-$([Guid]::NewGuid().ToString('N').Substring(0, 12))" }
if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在：$WorkRoot" }
$WorkRoot = (New-Item -ItemType Directory -Path $WorkRoot).FullName
$work = $WorkRoot
if ($work.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar)) { throw "工作目录不得在仓库内（会继承仓库根配置）：$work" }

try {
    if (-not $PackagePath) {
        if (-not $Version) { $Version = "0.0.1-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))" }
        Invoke-Checked 'dotnet' @('pack', (Join-Path $PSScriptRoot 'Leistd.Templates.csproj'), '-c', 'Release', '-o', (Join-Path $work 'package'),
            "-p:TemplateVersion=$Version", "-p:TemplateSourceSha=$SourceSha") | Out-Null
        $PackagePath = Join-Path $work 'package' "Leistd.Templates.$Version.nupkg"
    }
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "模板包不存在：$PackagePath" }
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
    if (-not $Version) {
        if ([IO.Path]::GetFileName($PackagePath) -notmatch '^Leistd\.Templates\.(.+)\.nupkg$') { throw "无法从文件名得出版本：$PackagePath" }
        $Version = $Matches[1]
    }

    # 2. 包内容与身份
    $prefix = 'content/fullstack-app/'
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName.EndsWith('.nuspec') })
        if ($nuspecEntry.Count -ne 1) { throw '模板包里应恰好有一个 nuspec' }
        $reader = [IO.StreamReader]::new($nuspecEntry[0].Open()); [xml]$nuspec = $reader.ReadToEnd(); $reader.Dispose()
        $metadata = $nuspec.package.metadata
        $types = @($metadata.packageTypes.packageType | ForEach-Object { $_.name })
        if ($metadata.id -cne 'Leistd.Templates' -or $metadata.version -cne $Version -or ($types -join ',') -cne 'Template' -or
            $metadata.repository.commit -cne $SourceSha) {
            throw "nuspec 身份不符：id=$($metadata.id) version=$($metadata.version) packageTypes=$($types -join ',') commit=$($metadata.repository.commit)"
        }
        $packed = @{}
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.StartsWith($prefix)) { continue }
            $stream = $entry.Open(); $memory = [IO.MemoryStream]::new(); $stream.CopyTo($memory); $stream.Dispose()
            $packed['template/' + $entry.FullName.Substring($prefix.Length)] = $memory.ToArray()
        }
    } finally { $archive.Dispose() }
    $tracked = @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'ls-files', '--cached', '--others', '--exclude-standard', '--', 'template') |
        Where-Object { Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf })
    $missing = @($tracked | Where-Object { -not $packed.ContainsKey($_) })
    $extra = @($packed.Keys | Where-Object { $_ -cnotin $tracked })
    if ($missing -or $extra) { throw "包内文件与 git ls-files template 不一致：`n  缺少：$($missing -join ', ')`n  多出：$($extra -join ', ')" }
    $configPath = 'template/.template.config/template.json'
    $source = [IO.File]::ReadAllText((Join-Path $repoRoot $configPath))
    $expectedConfig = $source.Replace('"value": "0.0.0-source"', "`"value`": `"$Version`"").Replace('"value": "0000000000000000000000000000000000000000"', "`"value`": `"$SourceSha`"")
    if ($expectedConfig -ceq $source -or [Text.Encoding]::UTF8.GetString($packed[$configPath]) -cne $expectedConfig) {
        throw '包内 template.json 应只把两个身份符号写成本次的版本与源提交'
    }
    foreach ($path in $tracked) {
        if ($path -ceq $configPath) { continue }
        $expectedBytes = [IO.File]::ReadAllBytes((Join-Path $repoRoot $path))
        if ([Convert]::ToBase64String($expectedBytes) -cne [Convert]::ToBase64String($packed[$path])) { throw "包内 $path 与源码不一致" }
    }
    Write-Host "包内容：$($tracked.Count) 个文件与 template/ 一致，身份 $Version @ $SourceSha"

    # 3. 隔离安装并在仓库外生成
    $hive = Join-Path $work 'hive'
    Install-TemplatePackage $PackagePath $hive
    $generated = Join-Path $work 'generated'
    $sdk = (Invoke-Checked 'dotnet' @('--version') $work | Select-Object -Last 1).Trim()
    $scenarios = @('identity-all-features', 'resource-capabilities-03')
    foreach ($scenario in $scenarios) {
        $name = Get-ScenarioProjectName $scenario
        $output = Join-Path $generated $scenario
        Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-n', $name, '-o', $output, '--debug:custom-hive', $hive) + $scenarioMap[$scenario].Arguments) $work | Out-Null
        if ((Test-Path -LiteralPath (Join-Path $output 'frontend')) -ne ($scenario -eq 'identity-all-features')) { throw "$scenario 前端目录的有无不符" }

        # 4. 来源标记与反例
        Assert-ScenarioMarker $output $scenario $name $Version $SourceSha $sdk
        $markerFile = Join-Path $output '.leistd/template.json'
        $original = [IO.File]::ReadAllText($markerFile)
        foreach ($mutation in @(@($Version, "$Version-tampered"), @('"Ci": "github"', '"Ci": "none"'), @("`"name`": `"$name`"", '"name": "Other.Name"'))) {
            [IO.File]::WriteAllText($markerFile, $original.Replace($mutation[0], $mutation[1]))
            Assert-Throws "$scenario 标记改动 $($mutation[1])" { Assert-ScenarioMarker $output $scenario $name $Version $SourceSha $sdk }
        }
        [IO.File]::WriteAllText($markerFile, $original)
        Remove-Item -LiteralPath $markerFile
        Assert-Throws "$scenario 缺少标记" { Read-TemplateMarker $output | Out-Null }
        [IO.File]::WriteAllText($markerFile, $original)

        # 5. 重建确定性与反例
        $marker = Read-TemplateMarker $output
        $rebuild = Join-Path $work 'rebuild' $scenario
        New-TemplateRebuild $marker $hive $rebuild
        $differences = Compare-TemplateTree $output $rebuild
        if ($differences.Count -ne 0) { throw "$scenario 同版本同输入重建后存在差异：$($differences -join ', ')" }
        $raw = Join-Path $work 'raw' $scenario
        Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-o', $raw, '--debug:custom-hive', $hive) + (Get-TemplateMarkerArguments $marker)) $work | Out-Null
        $rawDifferences = Compare-TemplateTree $output $raw
        $expectedRaw = @(@($marker.userSecretsIds.Keys) + '.leistd/template.json' | Sort-Object -Unique -CaseSensitive)
        if (($rawDifferences -join '|') -cne ($expectedRaw -join '|')) {
            throw "$scenario 未归一化的重建差异应恰好是 UserSecretsId 所在文件：$($rawDifferences -join ', ')"
        }
        $probe = Join-Path $rebuild 'README.md'
        Add-Content -LiteralPath $probe -Value 'probe'
        if ((Compare-TemplateTree $output $rebuild) -cnotcontains 'README.md') { throw "$scenario 重建比较没有检出内容改动" }
        Write-Host "$scenario：标记核对通过，重建零差异（未归一化时差异为 $($rawDifferences.Count) 个 UserSecretsId 文件）"
    }

    # 6. 只用 nuget.org 还原默认 LeistdFrameworkVersion（两个代表场景都还原）
    if ($NuGetOrgRestore) {
        $config = Join-Path $work 'nuget.org.config'
        [IO.File]::WriteAllText($config, @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
'@)
        $deadline = [DateTime]::UtcNow.AddMinutes($RestoreTimeoutMinutes)
        # 两个场景都还原完再判定，失败时一并列出
        $restoreFailures = @()
        foreach ($scenario in $scenarios) {
            $project = Join-Path $generated $scenario
            $props = [xml](Get-Content -LiteralPath (Join-Path $project 'backend/Directory.Build.props') -Raw)
            $frameworkVersion = @($props.Project.PropertyGroup | ForEach-Object { $_.LeistdFrameworkVersion } | Where-Object { $_ })[0]
            $solution = @(Get-ChildItem -LiteralPath (Join-Path $project 'backend') -Filter '*.sln' -File)[0].FullName
            $failure = $null
            for ($attempt = 1; ; $attempt++) {
                $packages = Join-Path $work "nuget-packages-$scenario-$attempt"
                $log = @(& dotnet restore $solution --configfile $config --packages $packages --no-http-cache 2>&1 | ForEach-Object { "$_" })
                if ($LASTEXITCODE -eq 0) { break }
                $codes = (@($log | ForEach-Object { [regex]::Matches($_, '\berror (NU\d{4})') } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ',')
                if (-not (Test-IndexingDelay $log $frameworkVersion)) { $failure = "$scenario：还原失败且不是索引延迟（$codes），不重试"; break }
                if ([DateTime]::UtcNow -ge $deadline) { $failure = "$scenario：$RestoreTimeoutMinutes 分钟内仍找不到 Leistd.* $frameworkVersion（$codes）"; break }
                Write-Host "$scenario 第 $attempt 次只用 nuget.org 还原失败（$codes：Leistd $frameworkVersion 尚未建好索引或网络瞬断），60 秒后重试"
                Start-Sleep -Seconds 60
            }
            if ($failure) {
                $missing = @($log | ForEach-Object { [regex]::Match($_, 'package (Leistd\.\S+) with version').Groups[1].Value } | Where-Object { $_ } | Sort-Object -Unique)
                $restoreFailures += "$failure；缺 $($missing.Count) 个包：$($missing -join ', ')"
                continue
            }
            $restored = @(Get-ChildItem -LiteralPath $packages -Directory -Filter 'leistd.*')
            if ($restored.Count -eq 0 -or @($restored | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.FullName $frameworkVersion)) }).Count -gt 0) {
                throw "$scenario 还原结果里的 Leistd.* 不全是默认版本 $frameworkVersion"
            }
            Write-Host "$scenario 只用 nuget.org 还原通过：$($restored.Count) 个 Leistd.* 包，版本 $frameworkVersion"
        }
        if ($restoreFailures) { throw "生成项目无法只用 nuget.org 还原默认的 LeistdFrameworkVersion：`n  $($restoreFailures -join "`n  ")" }
    }
    Write-Host "✅ 模板包 $Version 验证通过：包内容、隔离安装、$($scenarios.Count) 个场景的来源标记与重建确定性$(if ($NuGetOrgRestore) { '、nuget.org 还原' })。" -ForegroundColor Green
} finally {
    if (-not $KeepWorkRoot) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
