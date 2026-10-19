#!/usr/bin/env pwsh

<#
.SYNOPSIS
    模板同步（template/scripts/sync-template.ps1）与升级闸门的本地验收：用本地打包的几个模板版本覆盖全部验收类与反例。

.DESCRIPTION
    在仓库之外的临时目录里把当前 template/ 的副本打成几个本地版本（不改动仓库里的模板源码）：
      0.1.0  当前模板，作为上一版；
      0.1.1  在 0.1.0 上改 README 一行、替换一个图片、删除 CLAUDE.md、新增一个文档、改前端包版本（依赖清单与锁文件）；
      0.1.2  在 0.1.0 上把两个后端文件只改文件名大小写（其中一个同时改内容）；
      0.2.0  把参数 IncludeEmail 改名（旧标记不兼容）。
    然后执行：
    1. 升级闸门 test-template-upgrade.ps1（0.1.0 → 0.1.1，两个代表场景：无冲突、文本冲突、-Complete）；
    2. 诊断反例：工作区不干净、缺少标记、源码目录生成（0.0.0-source）、参数不兼容、已是目标版本、
       没有待完成的同步时 -Complete、-ProjectRoot 不是工作树根目录；失败的都以 1 退出、输出诊断且不改动工作区；
    3. git 原生冲突：二进制、删除/修改（两个方向）都成为未合并路径，未解决时 -Complete 被拒绝，按 git 的方式解决后完成，verify 通过；
       新版新增路径上有被忽略的本地文件时在合并前失败、不覆盖，移走后同步完成；全局与项目的排除规则不让模板文件漏合；
       只改大小写的改名（git 索引记录新名）；
    4. 存量接入：-BaseSource 取首个模板包之前的真实提交（缺省 origin/develop~20）的 template/，在只含 README 的初始提交之后
       生成无标记项目并提交源码与规范定制（缺省比较根提交被拒，用 -BaselineCommit 指定生成提交）；脚本从 0.1.0 模板包里取出（与真实接入一致）。输入错误时基线差异被拒绝；正确输入接入到 0.1.0，规范改动迁到
       docs/project/，移走临时源码目录后同步到 0.1.1，定制保留，verify 通过。

.PARAMETER LocalFeedPath
    verify 用的候选 Framework 包源（framework/build/pack-local-feed.ps1 的输出），缺省 .tmp/local-feed。

.PARAMETER Verify
    传给闸门与各用例的 verify 模式：Fast（缺省，本地）或 Full。

.PARAMETER AdoptBaseRef
    存量接入用例的基线提交。
#>
param(
    [string]$LocalFeedPath,
    [ValidateSet('Full', 'Fast')][string]$Verify = 'Fast',
    [string]$AdoptBaseRef = 'origin/develop~20',
    # 只跑部分用例（逗号分隔）：gate、diagnostics、conflicts、ignored、excludes、case-rename、adopt
    [string[]]$Cases = @('gate', 'diagnostics', 'conflicts', 'ignored', 'excludes', 'case-rename', 'adopt'),
    [string]$WorkRoot,
    [switch]$KeepWorkRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repoRoot 'scripts' 'template-matrix-scenarios.ps1')
. (Join-Path $repoRoot 'template' 'scripts' 'template-marker.ps1')
. (Join-Path $repoRoot 'framework' 'build' 'candidate-feed.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:HUSKY = '0'
if (-not $LocalFeedPath) { $LocalFeedPath = Join-Path $repoRoot '.tmp/local-feed' }
# pwsh -File 传入的数组是一个逗号分隔的字符串
$Cases = @($Cases | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknownCases = @($Cases | Where-Object { $_ -cnotin @('gate', 'diagnostics', 'conflicts', 'ignored', 'excludes', 'case-rename', 'adopt') })
if ($unknownCases) { throw "未知用例：$($unknownCases -join ', ')" }

function Invoke-Checked([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $output = & $Command @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$Command 失败（退出码 $LASTEXITCODE）：`n$(@($output | Select-Object -Last 40) -join "`n")" }
        return $output
    } finally { Pop-Location }
}

function Initialize-Repository([string]$Path, [string]$Message) {
    Invoke-Checked 'git' @('init', '-q', '-b', 'main') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'user.email', 'template-sync@example.invalid') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'user.name', 'Template sync test') $Path | Out-Null
    Invoke-Checked 'git' @('config', 'core.autocrlf', 'false') $Path | Out-Null
    Save-Commit $Path $Message
}

function Save-Commit([string]$Path, [string]$Message) {
    Invoke-Checked 'git' @('add', '-A') $Path | Out-Null
    Invoke-Checked 'git' @('commit', '-q', '-m', $Message) $Path | Out-Null
}

function Get-Status([string]$Path) { return @(Invoke-Checked 'git' @('status', '--porcelain', '--untracked-files=all') $Path) }

function Invoke-Sync([string]$ProjectPath, [string[]]$Arguments, [string]$Script) {
    if (-not $Script) { $Script = Join-Path $ProjectPath 'scripts/sync-template.ps1' }
    Write-Host "> sync-template.ps1 $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $ProjectPath
    try {
        $output = @(& pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    $output | ForEach-Object { Write-Host "  $_" }
    return [pscustomobject]@{ Code = $code; Output = ($output -join "`n") }
}

function Assert-SyncFails([string]$Label, [string]$ProjectPath, [string[]]$Arguments, [string]$Expected) {
    $before = (Get-Status $ProjectPath) -join "`n"
    $result = Invoke-Sync $ProjectPath $Arguments
    if ($result.Code -ne 1 -or -not $result.Output.Contains($Expected)) { throw "反例未按预期失败：$Label（退出码 $($result.Code)，应含「$Expected」）" }
    if (((Get-Status $ProjectPath) -join "`n") -cne $before) { throw "反例失败后工作区被改动：$Label" }
    Write-Host "  反例已拒绝：$Label" -ForegroundColor DarkGray
}

function Assert-Marker([string]$ProjectPath, [string]$Version) {
    $marker = Read-TemplateMarker $ProjectPath
    if ($marker.package.version -cne $Version) { throw "$ProjectPath 的来源标记版本是 $($marker.package.version)，应为 $Version" }
}

function Get-Unmerged([string]$ProjectPath) { return @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'diff', '--name-only', '--diff-filter=U') $ProjectPath | Sort-Object -CaseSensitive) }

function Invoke-ProjectVerify([string]$ProjectPath) {
    $arguments = @('-NoProfile', '-File', (Join-Path $ProjectPath 'scripts/verify.ps1'))
    if ($Verify -eq 'Fast') {
        if (Test-Path -LiteralPath (Join-Path $ProjectPath 'frontend/package.json')) { Invoke-Checked 'npm' @('ci', '--no-audit', '--no-fund') (Join-Path $ProjectPath 'frontend') | Out-Null }
        $arguments += '-Fast'
    }
    Invoke-Checked 'pwsh' $arguments $ProjectPath | Out-Null
    Assert-CandidatePackages $script:PackagesRoot $script:CandidateFeed
    Write-Host "  verify（$Verify）通过：$ProjectPath"
}

function New-Project([string]$Scenario, [string]$Hive, [string]$Path) {
    Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-n', (Get-ScenarioProjectName $Scenario), '-o', $Path, '--debug:custom-hive', $Hive) + $scenarioMap[$Scenario].Arguments) $script:Work | Out-Null
    Initialize-Repository $Path 'chore: 生成'
}

# 把 template/ 副本（git 跟踪与未忽略的文件）按 Mutate 修改后打成指定版本，放进本地模板包源
function New-TemplateVersion([string]$Version, [scriptblock]$Mutate) {
    $source = Join-Path $script:Work "template-$Version"
    $template = Join-Path $source 'template'
    foreach ($relative in @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'ls-files', '--cached', '--others', '--exclude-standard', '--', 'template') $repoRoot)) {
        $from = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue }
        $to = Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
        [IO.File]::Copy($from, $to, $true)
    }
    New-Item -ItemType Directory -Path (Join-Path $source 'template-package') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Leistd.Templates.csproj') -Destination (Join-Path $source 'template-package')
    if ($Mutate) { & $Mutate $template }
    Invoke-Checked 'dotnet' @('pack', (Join-Path $source 'template-package/Leistd.Templates.csproj'), '-c', 'Release', '-o', $script:Feed,
        "-p:TemplateVersion=$Version", "-p:TemplateSourceSha=$script:SourceSha") $source | Out-Null
    Write-Host "本地模板包 $Version"
}

function Edit-Text([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    $index = $text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "夹具锚点不存在：$Path 中的「$Old」" }
    [IO.File]::WriteAllText($Path, $text.Substring(0, $index) + $New + $text.Substring($index + $Old.Length))
}

function Install-Version([string]$Version) {
    $hive = Join-Path $script:Work "hive-$Version"
    if (-not (Test-Path -LiteralPath $hive)) { Push-Location $script:Work; try { Install-TemplatePackage "Leistd.Templates::$Version" $hive @($script:Feed) } finally { Pop-Location } }
    return $hive
}

if (-not $WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) "leistd-template-sync-$([Guid]::NewGuid().ToString('N').Substring(0, 12))" }
if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在：$WorkRoot" }
$script:Work = (New-Item -ItemType Directory -Path $WorkRoot).FullName
if ($script:Work.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar)) { throw "工作目录不得在仓库内：$($script:Work)" }
$script:Feed = Join-Path $script:Work 'template-feed'
$script:SourceSha = (Invoke-Checked 'git' @('rev-parse', 'HEAD') $repoRoot | Select-Object -Last 1).Trim()

try {
    # verify 的还原：候选 Framework 包 + nuget.org，生成项目经上层 Directory.Build.rsp 用候选版本
    $script:CandidateFeed = Read-CandidateManifest $LocalFeedPath
    $script:PackagesRoot = Join-Path $script:Work 'nuget-packages'
    Set-CandidateVersionOverride $script:Work $script:CandidateFeed.CandidateVersion
    [IO.File]::WriteAllText((Join-Path $script:Work 'NuGet.Config'), @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$([Security.SecurityElement]::Escape($script:PackagesRoot))" />
  </config>
  <packageSources>
    <clear />
    <add key="candidate-framework" value="$([Security.SecurityElement]::Escape($script:CandidateFeed.FeedPath))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@)

    New-TemplateVersion '0.1.0' $null
    New-TemplateVersion '0.1.1' {
        param($template)
        Edit-Text (Join-Path $template 'README.md') '组件构建的应用，' '组件构建的应用（0.1.1），'
        $image = Join-Path $template 'frontend/public/images/beian-icon.png'
        [IO.File]::WriteAllBytes($image, [IO.File]::ReadAllBytes($image) + [byte[]](9, 9, 0, 9))
        Remove-Item -LiteralPath (Join-Path $template 'CLAUDE.md')
        [IO.File]::WriteAllText((Join-Path $template 'docs/sync-probe.md'), "# 同步探针`n`n0.1.1 新增的文档。`n")
        foreach ($variant in 'frontend', '.template.config/localization/frontend') {
            Edit-Text (Join-Path $template "$variant/package.json") '"version": "1.0.2"' '"version": "1.0.3"'
            $lock = Join-Path $template "$variant/package-lock.json"
            Edit-Text $lock '"version": "1.0.2"' '"version": "1.0.3"'
            Edit-Text $lock '"name": "companyname-projectname-web",
      "version": "1.0.2"' '"name": "companyname-projectname-web",
      "version": "1.0.3"'
        }
    }
    $caseRenames = @(
        @{ From = 'backend/src/CompanyName.ProjectName.Domain/Users/Constants/AdminConstant.cs'; To = 'backend/src/CompanyName.ProjectName.Domain/Users/Constants/adminConstant.cs'; Edit = $false }
        @{ From = 'backend/src/CompanyName.ProjectName.Domain/DependencyInjection.cs'; To = 'backend/src/CompanyName.ProjectName.Domain/dependencyInjection.cs'; Edit = $true }
    )
    New-TemplateVersion '0.1.2' {
        param($template)
        foreach ($rename in $caseRenames) {
            $text = [IO.File]::ReadAllText((Join-Path $template $rename.From))
            Remove-Item -LiteralPath (Join-Path $template $rename.From)
            if ($rename.Edit) { $text = "// 0.1.2 改名并修改`n" + $text }
            [IO.File]::WriteAllText((Join-Path $template $rename.To), $text)
        }
    }
    New-TemplateVersion '0.2.0' {
        param($template)
        Edit-Text (Join-Path $template '.template.config/template.json') '"IncludeEmail": {' '"IncludeMail": {'
    }
    $summary = @()

    if ('gate' -in $Cases) {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-template-upgrade.ps1') -PreviousVersion '0.1.0' `
            -CandidatePackage (Join-Path $script:Feed 'Leistd.Templates.0.1.1.nupkg') -PackageSource $script:Feed `
            -LocalFeedPath $script:CandidateFeed.FeedPath -Verify $Verify
        if ($LASTEXITCODE -ne 0) { throw "升级闸门失败（退出码 $LASTEXITCODE）" }
        $summary += '升级闸门 0.1.0 → 0.1.1：两个代表场景的无冲突、文本冲突、-Complete 三类验收'
    }

    $hive010 = Install-Version '0.1.0'
    $api = 'resource-capabilities-03'

    if ('diagnostics' -in $Cases) {
        $project = Join-Path $script:Work 'diagnostics'
        New-Project $api $hive010 $project
        [IO.File]::AppendAllText((Join-Path $project 'README.md'), "dirty`n")
        Assert-SyncFails '工作区不干净' $project @('-To', '0.1.1', '-PackageSource', $script:Feed) '未提交的修改'
        Invoke-Checked 'git' @('checkout', '--', 'README.md') $project | Out-Null
        Assert-SyncFails '没有待完成的同步时 -Complete' $project @('-Complete') '没有待完成的同步'
        Assert-SyncFails '参数不兼容（新版改名 IncludeEmail）' $project @('-To', '0.2.0', '-PackageSource', $script:Feed) '参数 IncludeEmail 在 Leistd.Templates 0.2.0 中已删除或改名'
        $result = Invoke-Sync $project @('-To', '0.2.0', '-PackageSource', $script:Feed)
        if (-not $result.Output.Contains('releases/tag/template-v0.2.0')) { throw '参数不兼容的诊断没有指向该版本的 Release' }
        $result = Invoke-Sync $project @('-To', '0.1.0', '-PackageSource', $script:Feed)
        if ($result.Code -ne 0 -or -not $result.Output.Contains('无需同步') -or (Get-Status $project).Count -ne 0) { throw '已是目标版本时应以 0 退出且不改动工作区' }
        Remove-Item -LiteralPath (Join-Path $project '.leistd/template.json')
        Save-Commit $project 'chore: 删除标记'
        Assert-SyncFails '-ProjectRoot 不是工作树根目录' $project @('-To', '0.1.1', '-PackageSource', $script:Feed, '-ProjectRoot', (Join-Path $project 'backend')) '-ProjectRoot 必须是 git 工作树的根目录'
        Assert-SyncFails '缺少来源标记' $project @('-To', '0.1.1', '-PackageSource', $script:Feed) '缺少来源标记 .leistd/template.json'
        $sourceHive = Join-Path $script:Work 'hive-source'
        Push-Location $script:Work; try { Install-TemplatePackage (Join-Path $repoRoot 'template') $sourceHive } finally { Pop-Location }
        $fromSource = Join-Path $script:Work 'from-source'
        New-Project $api $sourceHive $fromSource
        Assert-SyncFails '源码目录生成的项目（0.0.0-source）' $fromSource @('-To', '0.1.1', '-PackageSource', $script:Feed) '0.0.0-source'
        $summary += '诊断反例：工作区不干净、没有待完成同步的 -Complete、参数不兼容（指向 Release）、已是目标版本、-ProjectRoot 不是工作树根目录、缺少标记、源码生成'
    }

    if ('conflicts' -in $Cases) {
        # 二进制、删除/修改两个方向：都交给 git，成为未合并路径
        $project = Join-Path $script:Work 'conflicts'
        New-Project 'identity-all-features' $hive010 $project
        $image = Join-Path $project 'frontend/public/images/beian-icon.png'
        $custom = [IO.File]::ReadAllBytes($image) + [byte[]](7, 7, 0, 7)
        [IO.File]::WriteAllBytes($image, $custom)
        [IO.File]::AppendAllText((Join-Path $project 'CLAUDE.md'), "`n项目自己的 AI 约定。`n")
        Remove-Item -LiteralPath (Join-Path $project 'README.md')
        Save-Commit $project 'feat: 替换备案图标、改 CLAUDE.md、删 README.md'
        $result = Invoke-Sync $project @('-To', '0.1.1', '-PackageSource', $script:Feed)
        if ($result.Code -ne 2) { throw "二进制与删除/修改冲突时应退出 2，实际 $($result.Code)" }
        $unmerged = Get-Unmerged $project
        $expected = @('CLAUDE.md', 'README.md', 'frontend/public/images/beian-icon.png') | Sort-Object -CaseSensitive
        if (($unmerged -join '|') -cne ($expected -join '|')) { throw "未合并路径不符：$($unmerged -join ', ')" }
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($image)) -cne [Convert]::ToBase64String($custom)) { throw '二进制冲突时工作区应保留项目版本' }
        Assert-Marker $project '0.1.0'
        if ((Read-TemplateMarker $project '.leistd/template.pending.json').package.version -cne '0.1.1') { throw '待完成的目标标记应是 0.1.1' }
        $result = Invoke-Sync $project @('-Complete')
        if ($result.Code -ne 1 -or -not $result.Output.Contains('未合并的路径')) { throw '有未合并路径时 -Complete 必须拒绝' }
        # 按 git 的方式取舍：图片保留项目版本，CLAUDE.md 保留项目修改，README.md 接受项目的删除；
        # 其余一次 git add -A（待完成标记也会被暂存，-Complete 须把它移出暂存区）
        Invoke-Checked 'git' @('checkout', '--ours', '--', 'frontend/public/images/beian-icon.png') $project | Out-Null
        Invoke-Checked 'git' @('rm', '-q', '--', 'README.md') $project | Out-Null
        Invoke-Checked 'git' @('add', '-A') $project | Out-Null
        $result = Invoke-Sync $project @('-Complete')
        if ($result.Code -ne 0) { throw "解决后 -Complete 应退出 0，实际 $($result.Code)" }
        Assert-Marker $project '0.1.1'
        if (Test-Path -LiteralPath (Join-Path $project '.leistd/template.pending.json')) { throw '-Complete 后待完成的目标标记应删除' }
        $leftover = @(Get-Status $project | Where-Object { $_.Contains('template.pending.json') }) + @(Invoke-Checked 'git' @('ls-files', '--', '.leistd/template.pending.json') $project)
        if ($leftover) { throw "git add -A 解决后，待完成标记不应留在暂存区或状态里：$($leftover -join ', ')" }
        if (-not (Test-Path -LiteralPath (Join-Path $project 'docs/sync-probe.md'))) { throw '无冲突的新增应已合并' }
        Invoke-ProjectVerify $project
        $summary += '二进制与删除/修改（两个方向）冲突：git 未合并路径，未解决时 -Complete 拒绝；git checkout --ours / rm / add -A 取舍后完成，待完成标记不留在暂存区，verify 通过'
    }

    if ('ignored' -in $Cases) {
        # 新版新增的路径上已有被忽略的本地文件：git 合并会覆盖它，必须在合并前失败
        $project = Join-Path $script:Work 'ignored'
        New-Project $api $hive010 $project
        [IO.File]::AppendAllText((Join-Path $project '.gitignore'), "`ndocs/sync-probe.md`n")
        Save-Commit $project 'chore: 忽略本地探针文档'
        $local = Join-Path $project 'docs/sync-probe.md'
        $localText = "# 本地笔记`n`nPROJECT-LOCAL-PROBE`n"
        [IO.File]::WriteAllText($local, $localText)
        if ((Get-Status $project).Count -ne 0) { throw '被忽略的本地文件不应让工作区变脏' }
        Assert-SyncFails '新版新增路径上有被忽略的本地文件' $project @('-To', '0.1.1', '-PackageSource', $script:Feed) '被忽略的本地文件'
        if ([IO.File]::ReadAllText($local) -cne $localText) { throw '被忽略的本地文件应原样保留' }
        Move-Item -LiteralPath $local -Destination (Join-Path $script:Work 'sync-probe.local.md')
        $result = Invoke-Sync $project @('-To', '0.1.1', '-PackageSource', $script:Feed)
        if ($result.Code -ne 0 -or -not [IO.File]::ReadAllText($local).Contains('0.1.1 新增的文档。')) { throw "移走本地文件后应无冲突完成，实际 $($result.Code)" }
        $summary += '新版新增路径上有被忽略的本地文件：合并前失败、本地文件不变；移走后同步完成'
    }

    if ('excludes' -in $Cases) {
        # 全局 core.excludesFile 与项目 .git/info/exclude 只管项目自己的未跟踪文件，不能让基线或目标漏掉模板文件
        $project = Join-Path $script:Work 'excludes'
        New-Project $api $hive010 $project
        $globalConfig = Join-Path $script:Work 'excludes-global.gitconfig'
        $globalExcludes = Join-Path $script:Work 'excludes-global.ignore'
        [IO.File]::WriteAllText($globalExcludes, ".vscode/`ndocs/sync-probe.md`n")
        [IO.File]::WriteAllText($globalConfig, "[core]`n`texcludesFile = $($globalExcludes.Replace('\', '/'))`n")
        [IO.File]::AppendAllText((Join-Path $project '.git/info/exclude'), "README.md`n")
        $saved = $env:GIT_CONFIG_GLOBAL
        $env:GIT_CONFIG_GLOBAL = $globalConfig
        try { $result = Invoke-Sync $project @('-To', '0.1.1', '-PackageSource', $script:Feed) }
        finally { $env:GIT_CONFIG_GLOBAL = $saved }
        if ($result.Code -ne 0) { throw "全局与项目排除规则下同步应无冲突，实际 $($result.Code)" }
        $staged = @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'diff', '--cached', '--name-only') $project)
        if (-not [IO.File]::ReadAllText((Join-Path $project 'README.md')).Contains('（0.1.1）') -or 'README.md' -cnotin $staged) { throw '.git/info/exclude 匹配的 README.md 也应合入 0.1.1 的改动' }
        if (-not (Test-Path -LiteralPath (Join-Path $project 'docs/sync-probe.md')) -or 'docs/sync-probe.md' -cnotin $staged) { throw '全局排除规则匹配的 docs/sync-probe.md 也应作为模板新增合入' }
        Assert-Marker $project '0.1.1'
        $summary += '全局 core.excludesFile（.vscode/、docs/sync-probe.md）与 .git/info/exclude（README.md）不影响基线与目标入库：改动与新增照常合入并暂存'
    }

    if ('case-rename' -in $Cases) {
        $project = Join-Path $script:Work 'case-rename'
        New-Project $api $hive010 $project
        # 平台默认的 core.ignorecase（macOS、Windows 为 true）：git 索引里也要换成新文件名
        $result = Invoke-Sync $project @('-To', '0.1.2', '-PackageSource', $script:Feed)
        if ($result.Code -ne 0) { throw "只改大小写的改名应无冲突，实际 $($result.Code)" }
        $name = Get-ScenarioProjectName $api
        $tracked = @(Invoke-Checked 'git' @('-c', 'core.quotepath=off', 'ls-files') $project)
        foreach ($rename in $caseRenames) {
            $to = $rename.To.Replace('CompanyName.ProjectName', $name)
            $directory = Join-Path $project (Split-Path -Parent $to)
            $names = @(Get-ChildItem -LiteralPath $directory -File | ForEach-Object Name)
            $leaf = Split-Path -Leaf $to
            $old = Split-Path -Leaf $rename.From
            if ($leaf -cnotin $names -or $old -cin $names) { throw "改名后文件名应为 $leaf：实际 $($names -join ', ')" }
            if ($to -cnotin $tracked -or $rename.From.Replace('CompanyName.ProjectName', $name) -cin $tracked) { throw "git 索引应记录新文件名 $to" }
            $content = [IO.File]::ReadAllText((Join-Path $directory $leaf))
            if ($rename.Edit -ne $content.StartsWith('// 0.1.2 改名并修改')) { throw "$leaf 的内容应与 0.1.2 一致" }
        }
        Invoke-ProjectVerify $project
        $summary += "只改大小写的改名（有无内容变化各一，core.ignorecase=$(@("$(& git -C $project config --bool core.ignorecase)".Trim(), 'false') | Where-Object { $_ } | Select-Object -First 1)）：工作区与 git 索引都换成新文件名，内容与新版一致；verify 通过"
    }

    if ('adopt' -in $Cases) {
        $baseSha = (Invoke-Checked 'git' @('rev-parse', $AdoptBaseRef) $repoRoot | Select-Object -Last 1).Trim()
        if (Invoke-Checked 'git' @('ls-tree', '--name-only', $baseSha, 'template/.leistd/') $repoRoot) { throw "$AdoptBaseRef 已有来源标记，不是首个模板包之前的提交" }
        $zip = Join-Path $script:Work 'base-source.zip'
        Invoke-Checked 'git' @('archive', '--format=zip', '-o', $zip, $baseSha, 'template') $repoRoot | Out-Null
        $extracted = Join-Path $script:Work 'base-source'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
        $baseSource = Join-Path $extracted 'template'
        $baseHive = Join-Path $script:Work 'hive-base-source'
        Push-Location $script:Work; try { Install-TemplatePackage $baseSource $baseHive } finally { Pop-Location }
        # 生成之前先有一个只含 README 的初始提交：根提交不是生成那次提交，须用 -BaselineCommit 指定
        $project = Join-Path $script:Work 'adopt'
        New-Item -ItemType Directory -Path $project | Out-Null
        [IO.File]::WriteAllText((Join-Path $project 'README.md'), "# 初始`n")
        Initialize-Repository $project 'Initial commit'
        Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-n', (Get-ScenarioProjectName $api), '-o', $project, '--force', '--debug:custom-hive', $baseHive) + $scenarioMap[$api].Arguments) $script:Work | Out-Null
        Save-Commit $project 'chore: 由模板生成'
        $generationCommit = (Invoke-Checked 'git' @('rev-parse', 'HEAD') $project | Select-Object -Last 1).Trim()
        if (Test-Path -LiteralPath (Join-Path $project '.leistd')) { throw '基线源码不应生成来源标记' }

        # 定制：候选（0.1.0）未改动的一个后端源码文件、一条项目规范
        $current = Join-Path $script:Work 'adopt-current'
        Push-Location $script:Work
        try { Invoke-Checked 'dotnet' (@('new', 'fullstack-app', '-n', (Get-ScenarioProjectName $api), '-o', $current, '--debug:custom-hive', $hive010) + $scenarioMap[$api].Arguments) $script:Work | Out-Null }
        finally { Pop-Location }
        $changed = (Compare-TemplateTree $project $current)
        $source = @(Get-ChildItem -LiteralPath (Join-Path $project 'backend/src') -Recurse -File -Filter '*.cs' | ForEach-Object { [IO.Path]::GetRelativePath($project, $_.FullName).Replace('\', '/') } |
            Where-Object { $_ -cnotin $changed } | Sort-Object -CaseSensitive)[0]
        $sourceFile = Join-Path $project $source
        [IO.File]::WriteAllText($sourceFile, "// PROJECT-ADOPT-PROBE`n" + [IO.File]::ReadAllText($sourceFile))
        $sourceBytes = [IO.File]::ReadAllBytes($sourceFile)
        $rule = "`n## 项目规则`n`n- PROJECT-STANDARDS-PROBE：本项目的集成测试另需 mailpit。`n"
        [IO.File]::AppendAllText((Join-Path $project 'docs/standards/testing.md'), $rule)
        Save-Commit $project 'feat: 项目定制'

        # 与真实接入一致：脚本从目标版本模板包里取出，在项目外运行
        $tools = Join-Path $script:Work 'adopt-tools'
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $script:Feed 'Leistd.Templates.0.1.0.nupkg'))
        try {
            foreach ($name in 'sync-template.ps1', 'template-marker.ps1') {
                $entry = $archive.GetEntry("content/fullstack-app/scripts/$name")
                New-Item -ItemType Directory -Path $tools -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $tools $name))
            }
        } finally { $archive.Dispose() }
        $adoptScript = Join-Path $tools 'sync-template.ps1'
        $params = 'ServiceRole=Resource,IncludeFrontend=false,IncludeMultiTenancy=false,IncludeRealTime=false,IncludeEmail=false,IncludeOperationRecords=false,IncludeNotifications=false,IncludeExternalLogin=false,IncludeLocalization=false'
        $name = Get-ScenarioProjectName $api
        $rootOnly = Invoke-Sync $project @('-Adopt', '-BaseSource', $baseSource, '-Name', $name, '-Params', $params, '-To', '0.1.0', '-PackageSource', $script:Feed) $adoptScript
        if ($rootOnly.Code -ne 1 -or -not $rootOnly.Output.Contains('-BaselineCommit') -or (Get-Status $project).Count -ne 0) { throw '根提交不是生成提交时，缺省比较应以基线差异失败并提示 -BaselineCommit' }
        $wrong = Invoke-Sync $project @('-Adopt', '-BaseSource', $baseSource, '-Name', $name, '-Params', $params.Replace('IncludeMultiTenancy=false', 'IncludeMultiTenancy=true'), '-To', '0.1.0', '-PackageSource', $script:Feed, '-BaselineCommit', $generationCommit) $adoptScript
        if ($wrong.Code -ne 1 -or -not $wrong.Output.Contains('-AcceptBaselineDiff') -or (Get-Status $project).Count -ne 0) { throw '输入推断错误时接入必须以基线差异失败且不改动工作区' }
        $result = Invoke-Sync $project @('-Adopt', '-BaseSource', $baseSource, '-Name', $name, '-Params', $params, '-To', '0.1.0', '-PackageSource', $script:Feed, '-BaselineCommit', $generationCommit) $adoptScript
        if ($result.Code -ne 0 -or -not $result.Output.Contains("基线与提交 $($generationCommit.Substring(0, 12)) 一致")) { throw "存量接入应无冲突完成，实际退出码 $($result.Code)" }
        Assert-Marker $project '0.1.0'
        $standards = [IO.File]::ReadAllText((Join-Path $project 'docs/standards/testing.md'))
        $migrated = Join-Path $project 'docs/project/testing.md'
        if ($standards.Contains('PROJECT-STANDARDS-PROBE') -or -not (Test-Path -LiteralPath $migrated) -or -not [IO.File]::ReadAllText($migrated).Contains('PROJECT-STANDARDS-PROBE')) {
            throw '项目改过的规范应迁到 docs/project/，standards 随模板更新'
        }
        if ($standards -cne [IO.File]::ReadAllText((Join-Path $current 'docs/standards/testing.md'))) { throw 'docs/standards/testing.md 应与 0.1.0 一致' }
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sourceFile)) -cne [Convert]::ToBase64String($sourceBytes)) { throw '接入后源码定制应保留' }
        if (-not (Test-Path -LiteralPath (Join-Path $project 'scripts/sync-template.ps1'))) { throw '接入后项目应带上同步脚本' }
        Save-Commit $project 'chore: 接入 Leistd.Templates 0.1.0'
        Remove-Item -LiteralPath $extracted -Recurse -Force

        $result = Invoke-Sync $project @('-To', '0.1.1', '-PackageSource', $script:Feed)
        if ($result.Code -ne 0) { throw "接入后同步到 0.1.1 应无冲突，实际 $($result.Code)" }
        Assert-Marker $project '0.1.1'
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sourceFile)) -cne [Convert]::ToBase64String($sourceBytes) -or
            -not [IO.File]::ReadAllText($migrated).Contains('PROJECT-STANDARDS-PROBE')) { throw '同步到 0.1.1 后定制应保留' }
        if (-not [IO.File]::ReadAllText((Join-Path $project 'README.md')).Contains('（0.1.1）')) { throw '0.1.1 的改动应已合并' }
        Invoke-ProjectVerify $project
        $summary += "存量接入（基线 $($baseSha.Substring(0, 12))）：根提交不是生成提交时缺省比较被拒、用 -BaselineCommit 指定生成提交；错误输入被基线差异拒绝；接入 0.1.0 迁移规范、只写目标标记；移走源码后同步到 0.1.1，定制保留，verify 通过"
    }
    Write-Host "✅ 模板同步验收通过（verify $Verify）：`n  $($summary -join "`n  ")" -ForegroundColor Green
} finally {
    if (-not $KeepWorkRoot) { Remove-Item -LiteralPath $script:Work -Recurse -Force -ErrorAction SilentlyContinue }
}
