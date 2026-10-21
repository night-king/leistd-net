#!/usr/bin/env pwsh

<#
.SYNOPSIS
    模板升级闸门：用上一个已发布版本生成项目、提交项目定制，再以生成项目自带的 scripts/sync-template.ps1 同步到候选包。

.DESCRIPTION
    从第二个模板版本起是模板发布的前置条件（release.yml 的 template 通道，存在上一个 template-v* tag 时执行）。
    每个代表场景（identity-all-features 含前端；resource-capabilities-03 纯 API）：
    1. 在隔离模板目录安装 -PreviousVersion，在仓库之外生成项目，git 提交；
    2. 提交项目定制：新增一个依赖（有前端时为 npm 依赖并更新锁文件，纯 API 为 NuGet 依赖）、一处源码修改、
       替换一个图片（生成产物没有图片的场景不适用，并核对它确实没有）。定制只落在上一版与候选之间没有变化的文件上；
    3. 三类验收分开执行：
       a. 无冲突：同步退出 0，标记更新为候选版本；同步结果与候选重建的差异恰好是这些定制文件，定制原样保留；verify 通过；
       b. 冲突：另一份副本在候选改动的同一行另做修改，同步退出 2，该文件是唯一的 git 未合并路径、保留双方内容与冲突标记，
          标记未更新，待完成的目标标记是候选版本；此时 -Complete 必须拒绝，只 git add 而留着冲突标记也必须拒绝；
       c. 按候选一侧解决冲突并 git add 后 -Complete 退出 0、标记更新、待完成的目标标记删除，verify 通过。
       候选与上一版在该场景没有可冲突的文本改动时，b、c 不适用并明确输出。
    verify 需要的 Leistd.* 包：给 -LocalFeedPath（framework/build/pack-local-feed.ps1 的输出）时只用这批候选包，
    并经 Directory.Build.rsp 把生成项目的 LeistdFrameworkVersion 指向候选版本；不给时按生成项目的默认版本从 nuget.org 还原。

.PARAMETER PreviousVersion
    上一个已发布的模板版本（template-v* tag 去掉前缀）。

.PARAMETER CandidatePackage
    候选 Leistd.Templates nupkg。

.PARAMETER PackageSource
    上一版所在的 NuGet 源（地址或本地目录），缺省为 nuget.org。

.PARAMETER Verify
    Full 运行生成项目的完整 verify；Fast 运行 verify -Fast（有前端时先 npm ci）。
#>
param(
    [Parameter(Mandatory)][string]$PreviousVersion,
    [Parameter(Mandatory)][string]$CandidatePackage,
    [string[]]$PackageSource = @('https://api.nuget.org/v3/index.json'),
    [string]$LocalFeedPath,
    [ValidateSet('Full', 'Fast')][string]$Verify = 'Full',
    [string[]]$Scenarios = @('identity-all-features', 'resource-capabilities-03'),
    [string]$WorkRoot,
    [switch]$KeepWorkRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repoRoot 'scripts' 'template-matrix-scenarios.ps1')
. (Join-Path $repoRoot 'template' 'scripts' 'template-marker.ps1')
. (Join-Path $repoRoot 'framework' 'build' 'candidate-feed.ps1')

$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:HUSKY = '0'
$probeText = 'PROJECT-CUSTOMIZATION-PROBE'
$conflictText = 'PROJECT-CONFLICT-PROBE'

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $output = & $Command @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$Command 失败（退出码 $LASTEXITCODE）：`n$(@($output | Select-Object -Last 40) -join "`n")" }
        return $output
    } finally { Pop-Location }
}

function Initialize-Repository([string]$Path) {
    Invoke-Checked 'git' @('init', '-q', '-b', 'main') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'user.email', 'upgrade-gate@example.invalid') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'user.name', 'Upgrade gate') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'core.autocrlf', 'false') $Path | Out-Null
    # 临时仓库不做后台维护：新版 git 提交后在后台把松散对象打包并删除，与紧接着的本地 clone 抢文件，
    # clone 报"failed to copy file … No such file or directory"
    Invoke-Checked 'git' @('config', 'maintenance.auto', 'false') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'gc.auto', '0') $Path | Out-Null
}

function Save-Commit([string]$Path, [string]$Message) {
    Invoke-Checked 'git' @('add', '-A') $Path | Out-Null
    Invoke-Checked 'git' @('commit', '-q', '-m', $Message) $Path | Out-Null
}

function Copy-Project([string]$Origin, [string]$Path) {
    Invoke-Checked 'git' @('clone', '-q', $Origin, $Path) (Split-Path -Parent $Path) | Out-Null
    Initialize-Repository $Path
}

# 同步并返回退出码与输出；不以退出码判失败（2 是"有冲突"的约定结果）
function Invoke-Sync([string]$ProjectPath, [string[]]$Arguments) {
    Write-Host "> sync-template.ps1 $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $ProjectPath
    try {
        $output = @(& pwsh -NoProfile -File (Join-Path $ProjectPath 'scripts/sync-template.ps1') @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    $output | ForEach-Object { Write-Host "  $_" }
    return [pscustomobject]@{ Code = $code; Output = ($output -join "`n") }
}

function Invoke-ProjectVerify([string]$ProjectPath) {
    $arguments = @('-NoProfile', '-File', (Join-Path $ProjectPath 'scripts/verify.ps1'))
    if ($Verify -eq 'Fast') {
        if (Test-Path -LiteralPath (Join-Path $ProjectPath 'frontend/package.json')) { Invoke-Checked 'npm' @('ci', '--no-audit', '--no-fund') (Join-Path $ProjectPath 'frontend') | Out-Null }
        $arguments += '-Fast'
    }
    Invoke-Checked 'pwsh' $arguments $ProjectPath | Out-Null
    if ($script:CandidateFeed) { Assert-CandidatePackages $script:PackagesRoot $script:CandidateFeed }
    Write-Host "  verify（$Verify）通过：$ProjectPath"
}

function Read-Marker([string]$ProjectPath) { return Read-TemplateMarker $ProjectPath }

function Read-Lines([string]$Path) { return , @([IO.File]::ReadAllText($Path).Split("`n") | ForEach-Object { $_.TrimEnd("`r") }) }

# 在 Lines[Index] 之前插入一行时，它与上一行在候选里仍原样相邻：插入点两侧都没被候选改动，三方合并不会冲突
function Test-StableInsertion([string[]]$Lines, [int]$Index, [string[]]$TargetLines) {
    if ($Index -le 0 -or $Index -ge $Lines.Count) { return $false }
    for ($j = 1; $j -lt $TargetLines.Count; $j++) {
        if ($TargetLines[$j] -ceq $Lines[$Index] -and $TargetLines[$j - 1] -ceq $Lines[$Index - 1]) { return $true }
    }
    return $false
}

# npm 按名称排序插入新依赖：选一个排序后两侧相邻依赖在候选中未变的包
function Select-NpmDependency([string]$Base, [string]$Target) {
    $baseDependencies = (Get-Content -LiteralPath (Join-Path $Base 'frontend/package.json') -Raw | ConvertFrom-Json -AsHashtable).dependencies
    $targetDependencies = (Get-Content -LiteralPath (Join-Path $Target 'frontend/package.json') -Raw | ConvertFrom-Json -AsHashtable).dependencies
    foreach ($candidate in 'is-number@7.0.0', 'base64-js@1.5.1', 'yocto-queue@1.1.1') {
        $name = $candidate.Substring(0, $candidate.LastIndexOf('@'))
        if ($baseDependencies.Contains($name) -or $targetDependencies.Contains($name)) { continue }
        $sorted = @(@($baseDependencies.Keys) + $name | Sort-Object -CaseSensitive)
        $position = [Array]::IndexOf($sorted, $name)
        $neighbors = @($sorted[[Math]::Max(0, $position - 1)], $sorted[[Math]::Min($sorted.Count - 1, $position + 1)] | Where-Object { $_ -cne $name })
        $targetSorted = @($targetDependencies.Keys | Sort-Object -CaseSensitive)
        $stable = @($neighbors | Where-Object { $targetDependencies[$_] -ceq $baseDependencies[$_] }).Count -eq $neighbors.Count
        if ($neighbors.Count -eq 2) { $stable = $stable -and ([Array]::IndexOf($targetSorted, $neighbors[1]) - [Array]::IndexOf($targetSorted, $neighbors[0])) -eq 1 }
        if ($stable) { return $candidate }
    }
    throw '候选改动了新增 npm 依赖可插入位置两侧的全部依赖，无法构造不冲突的依赖定制'
}

# 定制：只落在上一版与候选之间的稳定位置。返回 相对路径 → 期望字节（候选未改动的定制文件逐字节核对）
function Add-Customizations([string]$Path, [string]$Target, [string[]]$Unchanged, [hashtable]$Definition) {
    $expected = @{}
    if ($Definition.Frontend) {
        $package = Select-NpmDependency $Path $Target
        $name = $package.Substring(0, $package.LastIndexOf('@')); $version = $package.Substring($package.LastIndexOf('@') + 1)
        Invoke-Checked 'npm' @('install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund', '--save-exact', $package) (Join-Path $Path 'frontend') | Out-Null
        $script:DependencyCheck = {
            param($root)
            $manifest = Get-Content -LiteralPath (Join-Path $root 'frontend/package.json') -Raw | ConvertFrom-Json -AsHashtable
            $lock = Get-Content -LiteralPath (Join-Path $root 'frontend/package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
            if ($manifest.dependencies[$name] -cne $version -or $lock.packages['']['dependencies'][$name] -cne $version -or
                $lock.packages["node_modules/$name"].version -cne $version) { throw "$root 丢失了 npm 依赖定制 $package" }
            foreach ($section in 'dependencies', 'devDependencies') {
                $declared = @($manifest[$section].GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Sort-Object -CaseSensitive) -join ';'
                $locked = @($lock.packages[''][$section].GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Sort-Object -CaseSensitive) -join ';'
                if ($declared -cne $locked) { throw "$root 的锁文件 $section 与 package.json 不一致" }
            }
        }.GetNewClosure()
    } else {
        $props = 'backend/Directory.Packages.props'
        $lines = Read-Lines (Join-Path $Path $props)
        $targetLines = Read-Lines (Join-Path $Target $props)
        $index = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match '^\s*<PackageVersion Include=' -and (Test-StableInsertion $lines $_ $targetLines) })[0]
        if ($null -eq $index) { throw "候选改动了 $props 的全部插入位置，无法构造不冲突的 NuGet 依赖定制" }
        $indent = [regex]::Match($lines[$index], '^\s*').Value
        $lines = @($lines[0..($index - 1)]) + "$indent<PackageVersion Include=`"Humanizer.Core`" Version=`"2.14.1`" />" + @($lines[$index..($lines.Count - 1)])
        [IO.File]::WriteAllText((Join-Path $Path $props), $lines -join "`n")
        $project = $null
        foreach ($candidate in @(Get-ChildItem -LiteralPath (Join-Path $Path 'backend/src') -Recurse -File -Filter '*.csproj' | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($Path, $candidate.FullName).Replace('\', '/')
            if (-not (Test-Path -LiteralPath (Join-Path $Target $relative))) { continue }
            $projectLines = Read-Lines $candidate.FullName
            $close = [Array]::LastIndexOf($projectLines, '</Project>')
            if ($close -gt 0 -and (Test-StableInsertion $projectLines $close (Read-Lines (Join-Path $Target $relative)))) { $project = $relative; break }
        }
        if (-not $project) { throw '候选改动了全部后端项目文件的结尾，无法构造不冲突的 NuGet 依赖定制' }
        $projectLines = Read-Lines (Join-Path $Path $project)
        $close = [Array]::LastIndexOf($projectLines, '</Project>')
        $projectLines = @($projectLines[0..($close - 1)]) + @('  <ItemGroup>', '    <PackageReference Include="Humanizer.Core" />', '  </ItemGroup>', '') + @($projectLines[$close..($projectLines.Count - 1)])
        [IO.File]::WriteAllText((Join-Path $Path $project), $projectLines -join "`n")
        $script:DependencyCheck = {
            param($root)
            if (-not ([IO.File]::ReadAllText((Join-Path $root $props))).Contains('<PackageVersion Include="Humanizer.Core" Version="2.14.1" />') -or
                -not ([IO.File]::ReadAllText((Join-Path $root $project))).Contains('<PackageReference Include="Humanizer.Core" />')) { throw "$root 丢失了 NuGet 依赖定制 Humanizer.Core" }
        }.GetNewClosure()
    }
    $source = @($Unchanged | Where-Object { $_ -like 'backend/src/*.cs' } | Sort-Object -CaseSensitive)[0]
    if (-not $source) { throw '找不到候选未改动的后端源码文件' }
    $file = Join-Path $Path $source
    [IO.File]::WriteAllText($file, "// $probeText`n" + [IO.File]::ReadAllText($file))
    $expected[$source] = [IO.File]::ReadAllBytes($file)
    $imagePattern = '\.(png|webp|jpg|jpeg|gif|ico)$'
    $images = @($Unchanged | Where-Object { $_ -match $imagePattern } | Sort-Object -CaseSensitive)
    if ($images) {
        $image = Join-Path $Path $images[0]
        [IO.File]::WriteAllBytes($image, [IO.File]::ReadAllBytes($image) + [byte[]](0, 1, 2, 3, 4, 5, 6, 7))
        $expected[$images[0]] = [IO.File]::ReadAllBytes($image)
    } elseif (@(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object { $_.Name -match $imagePattern }).Count -gt 0) {
        Write-Host '  生成产物里的图片都被候选改动：图片定制不适用' -ForegroundColor Yellow
    } else { Write-Host '  该场景的生成产物没有图片：图片定制不适用' }
    return $expected
}

# 候选也改过的定制文件（锁文件除外）：候选的每一行都要出现在合并结果里
function Assert-TemplateLinesKept([string]$ProjectPath, [string]$Target, [string[]]$Paths) {
    foreach ($relative in $Paths) {
        if ($relative -like '*package-lock.json' -or -not (Test-Path -LiteralPath (Join-Path $Target $relative))) { continue }
        $merged = [IO.File]::ReadAllText((Join-Path $ProjectPath $relative))
        $targetLines = Read-Lines (Join-Path $Target $relative)
        $missing = @($targetLines | Where-Object { $_ -and -not $merged.Contains($_) })
        if ($missing) { throw "$ProjectPath 的 $relative 缺少候选的改动：$($missing | Select-Object -First 3)" }
    }
}

function Assert-Customizations([string]$ProjectPath, [hashtable]$Expected) {
    & $script:DependencyCheck $ProjectPath
    foreach ($entry in $Expected.GetEnumerator()) {
        $actual = Join-Path $ProjectPath $entry.Key
        if (-not (Test-Path -LiteralPath $actual) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($actual)) -cne [Convert]::ToBase64String($entry.Value)) {
            throw "$ProjectPath 的定制 $($entry.Key) 没有原样保留"
        }
    }
}

function Assert-Marker([string]$ProjectPath, [string]$Version) {
    $marker = Read-Marker $ProjectPath
    if ($marker.package.version -cne $Version) { throw "$ProjectPath 的来源标记版本是 $($marker.package.version)，应为 $Version" }
}

# 候选改动的第一个文本文件与行：项目在同一行另做修改，三方合并必然冲突
function Find-ConflictTarget([string]$Base, [string]$Target, [string[]]$Excluded) {
    foreach ($relative in (Compare-TemplateTree $Base $Target)) {
        if ($relative -cin $Excluded -or $relative -match '(^|/)(package(-lock)?\.json|[^/]+\.csproj)$' -or $relative -ceq '.leistd/template.json') { continue }
        $basePath = Join-Path $Base $relative; $targetPath = Join-Path $Target $relative
        if (-not (Test-Path -LiteralPath $basePath) -or -not (Test-Path -LiteralPath $targetPath)) { continue }
        $baseBytes = [IO.File]::ReadAllBytes($basePath); $targetBytes = [IO.File]::ReadAllBytes($targetPath)
        if ([Array]::IndexOf($baseBytes, [byte]0) -ge 0 -or [Array]::IndexOf($targetBytes, [byte]0) -ge 0) { continue }
        $baseLines = [IO.File]::ReadAllText($basePath).Split("`n"); $targetLines = [IO.File]::ReadAllText($targetPath).Split("`n")
        $index = 0
        while ($index -lt $baseLines.Count -and $index -lt $targetLines.Count -and $baseLines[$index] -ceq $targetLines[$index]) { $index++ }
        $index = [Math]::Min($index, $baseLines.Count - 1)
        return [pscustomobject]@{ Path = $relative; Line = $index }
    }
    return $null
}

# 每个冲突块取候选（目标）一侧：diff3 样式，HEAD 一侧是项目
function Resolve-TakeTemplate([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    $resolved = [regex]::Replace($text, '(?ms)^<{7} HEAD\r?\n.*?^\|{7} [^\n]*\n.*?^={7}\r?\n(.*?)^>{7} [^\n]*(?:\n|\z)', '$1')
    if ($resolved -ceq $text) { throw "没有在 $Path 找到可解决的冲突块" }
    [IO.File]::WriteAllText($Path, $resolved)
}

if (-not (Test-Path -LiteralPath $CandidatePackage -PathType Leaf)) { throw "候选模板包不存在：$CandidatePackage" }
$CandidatePackage = (Resolve-Path -LiteralPath $CandidatePackage).Path
if ([IO.Path]::GetFileName($CandidatePackage) -notmatch '^Leistd\.Templates\.(.+)\.nupkg$') { throw "无法从文件名得出候选版本：$CandidatePackage" }
$candidateVersion = $Matches[1]
if ($candidateVersion -ceq $PreviousVersion) { throw "候选版本与上一版相同：$candidateVersion" }
if (-not $WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) "leistd-template-upgrade-$([Guid]::NewGuid().ToString('N').Substring(0, 12))" }
if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在：$WorkRoot" }
$WorkRoot = (New-Item -ItemType Directory -Path $WorkRoot).FullName
if ($WorkRoot.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar)) { throw "工作目录不得在仓库内（会继承仓库根配置）：$WorkRoot" }
$sources = @($PackageSource) + (Split-Path -Parent $CandidatePackage)

try {
    # 生成项目都在 WorkRoot 之下：NuGet.Config 与 Directory.Build.rsp 按目录层级生效（verify 的 restore 不带 --configfile）
    $script:PackagesRoot = Join-Path $WorkRoot 'nuget-packages'
    $script:CandidateFeed = $null
    $feedEntry = ''
    if ($LocalFeedPath) {
        $script:CandidateFeed = Read-CandidateManifest $LocalFeedPath
        $feedEntry = "    <add key=`"candidate-framework`" value=`"$([Security.SecurityElement]::Escape($script:CandidateFeed.FeedPath))`" />`n"
        Set-CandidateVersionOverride $WorkRoot $script:CandidateFeed.CandidateVersion
        Write-Host "候选 Framework 包：$($script:CandidateFeed.CandidateVersion)"
    }
    [IO.File]::WriteAllText((Join-Path $WorkRoot 'NuGet.Config'), @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$([Security.SecurityElement]::Escape($script:PackagesRoot))" />
  </config>
  <packageSources>
    <clear />
$feedEntry    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@)

    $previousHive = Join-Path $WorkRoot 'hive-previous'
    $candidateHive = Join-Path $WorkRoot 'hive-candidate'
    Push-Location $WorkRoot
    try {
        Install-TemplatePackage "Leistd.Templates::$PreviousVersion" $previousHive $PackageSource
        Install-TemplatePackage $CandidatePackage $candidateHive
    } finally { Pop-Location }

    $summary = @()
    foreach ($scenario in $Scenarios) {
        $definition = $scenarioMap[$scenario]
        if (-not $definition) { throw "未知场景：$scenario" }
        Write-Host "== $scenario：$PreviousVersion → $candidateVersion" -ForegroundColor Cyan
        $root = Join-Path $WorkRoot $scenario
        New-Item -ItemType Directory -Path $root | Out-Null
        $origin = Join-Path $root 'origin'
        Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-n', (Get-ScenarioProjectName $scenario), '-o', $origin, '--debug:custom-hive', $previousHive) + $definition.Arguments) $root | Out-Null
        Assert-Marker $origin $PreviousVersion
        $candidateTree = Join-Path $root 'candidate'
        Push-Location $root
        try { New-TemplateRebuild (Read-Marker $origin) $candidateHive $candidateTree -AllowProjectChanges } finally { Pop-Location }
        $changed = (Compare-TemplateTree $origin $candidateTree)
        $unchanged = @(Get-ChildItem -LiteralPath $origin -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($origin, $_.FullName).Replace('\', '/') } |
            Where-Object { $_ -cnotin $changed })
        Initialize-Repository $origin
        Save-Commit $origin "chore: 由 Leistd.Templates $PreviousVersion 生成"
        $customized = Add-Customizations $origin $candidateTree $unchanged $definition
        Save-Commit $origin 'feat: 项目定制'
        $customizedPaths = @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'diff', '--name-only', 'HEAD~1', 'HEAD') $origin)

        # a. 无冲突：自动合并，定制保留，与候选重建的差异恰好是定制文件
        $clean = Join-Path $root 'clean'
        Copy-Project $origin $clean
        $result = Invoke-Sync $clean @('-To', $candidateVersion, '-PackageSource', ($sources -join ','))
        if ($result.Code -ne 0) { throw "$scenario 无冲突同步应退出 0，实际 $($result.Code)" }
        Assert-Marker $clean $candidateVersion
        if (Test-Path -LiteralPath (Join-Path $clean '.leistd/template.pending.json')) { throw "$scenario 无冲突同步不应留下待完成的目标标记" }
        Assert-Customizations $clean $customized
        Assert-TemplateLinesKept $clean $candidateTree $customizedPaths
        $residual = (Compare-TemplateTree $clean $candidateTree)
        if (($residual -join '|') -cne (@($customizedPaths | Sort-Object -Unique -CaseSensitive) -join '|')) {
            throw "$scenario 同步结果与候选重建的差异应恰好是定制文件：`n  实际：$($residual -join ', ')`n  定制：$($customizedPaths -join ', ')"
        }
        Invoke-ProjectVerify $clean
        $classes = '无冲突'

        # b. 冲突：在候选改动的同一行另做修改
        $target = Find-ConflictTarget $origin $candidateTree $customizedPaths
        if (-not $target) {
            Write-Host "  $scenario：候选与上一版没有可冲突的文本改动，冲突与 -Complete 验收不适用" -ForegroundColor Yellow
        } else {
            $conflicted = Join-Path $root 'conflict'
            Copy-Project $origin $conflicted
            $file = Join-Path $conflicted $target.Path
            $lines = [IO.File]::ReadAllText($file).Split("`n")
            $projectLine = $lines[$target.Line].TrimEnd("`r") + " $conflictText"
            $lines[$target.Line] = $projectLine
            [IO.File]::WriteAllText($file, $lines -join "`n")
            Save-Commit $conflicted 'feat: 与候选同一处的项目修改'
            $result = Invoke-Sync $conflicted @('-To', $candidateVersion, '-PackageSource', ($sources -join ','))
            if ($result.Code -ne 2) { throw "$scenario 在 $($target.Path) 第 $($target.Line + 1) 行冲突时同步应退出 2，实际 $($result.Code)" }
            Assert-Marker $conflicted $PreviousVersion
            $merged = [IO.File]::ReadAllText($file)
            $targetLines = [IO.File]::ReadAllText((Join-Path $candidateTree $target.Path)).Split("`n") | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ }
            $templateSide = @($targetLines | Where-Object { -not $merged.Contains($_) })
            if (-not $merged.Contains($projectLine) -or $templateSide.Count -gt 0 -or $merged -notmatch '(?m)^<{7} HEAD') {
                throw "$scenario 冲突文件 $($target.Path) 应保留双方内容与冲突标记"
            }
            $unmerged = @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'diff', '--name-only', '--diff-filter=U') $conflicted)
            if (($unmerged -join '|') -cne $target.Path) { throw "$scenario 未合并路径应只有 $($target.Path)：$($unmerged -join ', ')" }
            if ((Read-TemplateMarker $conflicted '.leistd/template.pending.json').package.version -cne $candidateVersion) { throw "$scenario 待完成的目标标记应是 $candidateVersion" }
            Assert-Customizations $conflicted $customized
            $result = Invoke-Sync $conflicted @('-Complete')
            if ($result.Code -eq 0 -or $result.Output -notmatch '未合并的路径') { throw "$scenario 冲突未解决时 -Complete 必须拒绝" }
            Assert-Marker $conflicted $PreviousVersion

            # 只 git add、没去掉冲突标记：未合并路径没了，-Complete 仍须按冲突标记拒绝
            Invoke-Checked 'git' @('add', '--', $target.Path) $conflicted | Out-Null
            $result = Invoke-Sync $conflicted @('-Complete')
            if ($result.Code -eq 0 -or $result.Output -notmatch '还有冲突标记') { throw "$scenario 暂存了带冲突标记的文件时 -Complete 必须拒绝" }
            Assert-Marker $conflicted $PreviousVersion

            # c. 解决（取候选一侧）并 git add 后 -Complete，verify 通过
            Resolve-TakeTemplate $file
            Invoke-Checked 'git' @('add', '--', $target.Path) $conflicted | Out-Null
            $result = Invoke-Sync $conflicted @('-Complete')
            if ($result.Code -ne 0) { throw "$scenario 解决冲突后 -Complete 应退出 0，实际 $($result.Code)" }
            Assert-Marker $conflicted $candidateVersion
            if (Test-Path -LiteralPath (Join-Path $conflicted '.leistd/template.pending.json')) { throw "$scenario -Complete 后待完成的目标标记应删除" }
            if ([IO.File]::ReadAllText($file).Contains($conflictText)) { throw "$scenario 按候选一侧解决后不应保留项目一侧的冲突行" }
            Assert-Customizations $conflicted $customized
            Invoke-ProjectVerify $conflicted
            $classes += "、冲突（$($target.Path):$($target.Line + 1)）、-Complete"
        }
        $summary += "$scenario：$classes"
    }
    Write-Host "✅ 模板升级闸门通过（$PreviousVersion → $candidateVersion，verify $Verify）：`n  $($summary -join "`n  ")" -ForegroundColor Green
} finally {
    if (-not $KeepWorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
