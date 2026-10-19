#!/usr/bin/env pwsh

<#
.SYNOPSIS
    模板开发工作区：把一个登记场景生成到常驻目录，改模板源码后增量同步，在其中构建和跑目标测试。

.DESCRIPTION
    工作区位于 .tmp/dev/<场景>/，生成项目在其下的 project/。场景名与生成参数取自
    scripts/template-matrix-scenarios.ps1，Leistd 包取自本地候选包源（framework/build/pack-local-feed.ps1 的产物），
    消费方式与矩阵相同：NuGet.Config 的 packageSourceMapping、上层 Directory.Build.rsp 传入候选版本，
    restore 之后执行 Assert-CandidatePackages。

    - 首次运行：生成、restore，有前端时 npm ci。
    - -Sync：从模板源重新生成到临时目录，再镜像进 project/。模板里已删除的文件在工作区同样删除；
      bin、obj、node_modules、.cache、.angular 目录（模板里没有文件时）不进入、不动；生成项目自己的
      .gitignore 忽略的本地文件（.env、DataProtection-Keys/、IDE 设置、日志等）不算修改、也不删除。
      判定忽略用 git check-ignore，需要 PATH 上有 git。
      package.json 或 package-lock.json 的摘要变化时 npm ci；候选包版本或 *.props、*.targets、*.csproj、
      解决方案文件变化时重新 restore。
    - 防丢改动：记录上次同步写入的每个文件的摘要。工作区里有与记录不符的修改（改动、删除或新增）时，
      -Sync 停止并列出这些文件；改动要保留就手工移回 template/，确认可以丢弃时加 -Discard。
      路径区分大小写：模板只改文件名大小写时，工作区里的文件随之改名。
      方向只有“模板源 → 工作区”，不提供反向复制。
    - 工作区在本工作树的 .tmp/ 下，每个工作树由一个会话使用，不另设锁；并行任务各用自己的工作树或场景。

.PARAMETER Scenario
    场景名，取自 scripts/template-matrix-scenarios.ps1。

.PARAMETER Sync
    从模板源同步到已有工作区；工作区不存在时等同首次运行。

.PARAMETER Discard
    与 -Sync 合用：丢弃工作区里与同步记录不符的修改。

.PARAMETER LocalFeedPath
    候选包源，默认 .tmp/local-feed，须位于 .tmp 下。

.PARAMETER TemplateRoot
    模板源，默认 template/。自检用它指向模板副本。

.PARAMETER WorkspaceRoot
    工作区的上级目录，默认 .tmp/dev，须位于 .tmp 下。自检用它与日常工作区隔离。

.EXAMPLE
    pwsh scripts/template-dev-workspace.ps1 -Scenario resource-capabilities-09
    pwsh scripts/template-dev-workspace.ps1 -Scenario resource-capabilities-09 -Sync
#>

param(
    [Parameter(Mandatory)][string]$Scenario,
    [switch]$Sync,
    [switch]$Discard,
    [string]$LocalFeedPath,
    [string]$TemplateRoot,
    [string]$WorkspaceRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "generated-project-checks.ps1")
. (Join-Path $PSScriptRoot "template-matrix-scenarios.ps1")
. (Join-Path $repoRoot "framework/build/candidate-feed.ps1")

$tempPrefix = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp")).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
function Resolve-TempPath([string]$Path, [string]$Label) {
    $full = [IO.Path]::GetFullPath($Path, $repoRoot)
    if (-not $full.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label 必须位于 .tmp 下：$full" }
    return $full
}

if ($Discard -and -not $Sync) { throw "-Discard 只能与 -Sync 合用。" }
if (-not $scenarioMap.Contains($Scenario)) {
    throw "未登记的场景：$Scenario。可用场景见 scripts/template-matrix-scenarios.ps1：$($AllScenarios -join ', ')"
}

$templateSource = if ($TemplateRoot) { [IO.Path]::GetFullPath($TemplateRoot, $repoRoot) } else { Join-Path $repoRoot "template" }
$workspacesRoot = Resolve-TempPath ($(if ($WorkspaceRoot) { $WorkspaceRoot } else { Join-Path $repoRoot ".tmp/dev" })) "-WorkspaceRoot"
$feedRoot = Resolve-TempPath ($(if ($LocalFeedPath) { $LocalFeedPath } else { Join-Path $repoRoot ".tmp/local-feed" })) "-LocalFeedPath"
$workspace = Join-Path $workspacesRoot $Scenario
$projectRoot = Join-Path $workspace "project"
$stagingRoot = Join-Path $workspace "staging"
$hiveRoot = Join-Path $workspace "template-hive"
$packagesRoot = Join-Path $workspace "nuget-packages"
$statePath = Join-Path $workspace "workspace.json"
$ignoreProbe = Join-Path $workspace "ignore-probe"

# 工作区一侧的构建、依赖与缓存目录：遍历时不进入（不枚举 node_modules），镜像时不删、不覆盖。
# 只对模板里没有文件的目录生效：模板自带的同名目录（如某个 dist/）照常镜像。
$preservedDirectoryNames = [Collections.Generic.HashSet[string]]::new([string[]]@('bin', 'obj', 'node_modules', '.cache', '.angular'), [StringComparer]::Ordinal)
# 这些文件变化时重新 restore；npm 依赖只看 package.json 与锁文件
$restoreInputPattern = '(\.props|\.targets|\.csproj|\.sln|\.slnx|(^|/)NuGet\.Config)$'
$npmInputs = @('frontend/package.json', 'frontend/package-lock.json')

# ── 文件清单与摘要 ──────────────────────────────────────────────────────────────

# 路径一律按序数、区分大小写比较：模板只改文件名大小写时，新旧两个名字是两项
function New-PathMap { return [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal) }

# 模板拥有的目录（记录或新生成的文件所在的各级目录）：这些目录即使与 $preservedDirectoryNames 同名也要进入
function Get-OwnedDirectories([object[]]$Maps) {
    $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($map in $Maps) {
        foreach ($relative in $map.Keys) {
            $index = $relative.LastIndexOf('/')
            while ($index -gt 0) { $relative = $relative.Substring(0, $index); [void]$owned.Add($relative); $index = $relative.LastIndexOf('/') }
        }
    }
    return , $owned
}

# 列出目录树的文件与摘要。$Owned 为 $null 时（staging）不跳过任何目录；否则跳过不属于模板的保留目录。
function Get-TreeDigests([string]$Root, $Owned) {
    $digests = New-PathMap
    if (-not (Test-Path -LiteralPath $Root)) { return , $digests }
    $prefixLength = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    $files = [Collections.Generic.List[string]]::new()
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
            $relative = $child.Substring($prefixLength).Replace('\', '/')
            if ($null -ne $Owned -and $preservedDirectoryNames.Contains([IO.Path]::GetFileName($child)) -and -not $Owned.Contains($relative)) { continue }
            $pending.Push($child)
        }
        foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { $files.Add($file) }
    }
    foreach ($file in $files) {
        $digests[$file.Substring($prefixLength).Replace('\', '/')] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return , $digests
}

# 按生成项目自己的 .gitignore（含各级子目录的 .gitignore）判定哪些路径是被忽略的本地文件
# （.env、DataProtection-Keys/、IDE 设置、日志、publish/ 等）。project/ 不是 Git 仓库，且位于本仓库被忽略的 .tmp 下，
# 所以用工作区里的空仓库作 git-dir、project/ 作 work-tree 求值；关掉全局 excludesFile，结果只取决于项目规则。
function Get-IgnoredPaths([string[]]$Paths) {
    $ignored = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($Paths.Count -eq 0) { return , $ignored }
    if (-not (Test-Path -LiteralPath (Join-Path $ignoreProbe ".git"))) {
        & git init -q $ignoreProbe
        if ($LASTEXITCODE -ne 0) { throw "无法创建忽略规则探测仓库：$ignoreProbe" }
    }
    $output = $Paths | & git --git-dir (Join-Path $ignoreProbe ".git") --work-tree $projectRoot -c core.excludesFile= -c core.quotePath=off check-ignore --no-index --stdin
    # 退出码 0 = 有路径被忽略，1 = 都不被忽略，其他 = 出错
    if ($LASTEXITCODE -gt 1) { throw "git check-ignore 失败（退出码 $LASTEXITCODE）" }
    foreach ($line in @($output)) { if ($line) { [void]$ignored.Add($line) } }
    return , $ignored
}

function Get-InputDigest($Digests, [scriptblock]$Selector, [string]$Extra = '') {
    $lines = @($Digests.Keys | Where-Object $Selector | Sort-Object -CaseSensitive | ForEach-Object { "$_=$($Digests[$_])" }) + $Extra
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

# 与同步记录比对：改动、删除、新增都算未同步的修改；项目 .gitignore 忽略的本地文件不算
function Get-Drift($Recorded, $Current) {
    $drift = [Collections.Generic.List[string]]::new()
    foreach ($relative in $Recorded.Keys) {
        if (-not $Current.ContainsKey($relative)) { $drift.Add("已删除  $relative") }
        elseif ($Current[$relative] -cne $Recorded[$relative]) { $drift.Add("已修改  $relative") }
    }
    $added = @($Current.Keys | Where-Object { -not $Recorded.ContainsKey($_) })
    $ignored = Get-IgnoredPaths $added
    foreach ($relative in $added) {
        if (-not $ignored.Contains($relative)) { $drift.Add("新增    $relative") }
    }
    return , $drift
}

# 删除镜像后留下的空目录；不进入不属于模板的保留目录（不枚举 node_modules）
function Remove-EmptyDirectories([string]$Root, $Owned) {
    $prefixLength = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1
    $directories = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        foreach ($child in [IO.Directory]::EnumerateDirectories($pending.Pop())) {
            $relative = $child.Substring($prefixLength).Replace('\', '/')
            if ($preservedDirectoryNames.Contains([IO.Path]::GetFileName($child)) -and -not $Owned.Contains($relative)) { continue }
            $directories.Add($child)
            $pending.Push($child)
        }
    }
    foreach ($directory in ($directories | Sort-Object { $_.Length } -Descending)) {
        if (-not [IO.Directory]::EnumerateFileSystemEntries($directory).GetEnumerator().MoveNext()) {
            [IO.Directory]::Delete($directory)
        }
    }
}

# ── 生成、镜像与依赖 ────────────────────────────────────────────────────────────

function Write-WorkspaceNuGetConfig {
    $config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$([Security.SecurityElement]::Escape($packagesRoot))" />
  </config>
  <packageSources>
    <clear />
    <add key="local-feed" value="$([Security.SecurityElement]::Escape($feedRoot))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-feed"><package pattern="Leistd.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
    # 生成项目在 project/ 下，按目录层级取到这份配置与 Directory.Build.rsp
    [IO.File]::WriteAllText((Join-Path $workspace "NuGet.Config"), $config, [Text.UTF8Encoding]::new($false))
}

function New-StagingProject {
    $null = Resolve-TempPath $stagingRoot "staging"
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
    # 每次同步都重新安装模板：template.json 的改动要进模板缓存才会生效
    Invoke-External "dotnet" @("new", "--debug:custom-hive", $hiveRoot, "install", $templateSource, "--force")
    $definition = $scenarioMap[$Scenario]
    Invoke-External "dotnet" (@("new", "--debug:custom-hive", $hiveRoot, "fullstack-app", "-n", (Get-ScenarioProjectName $Scenario),
            "-o", $stagingRoot, "--force") + $definition.Arguments)
}

# 模板登记的随机 GUID（template.json 的 guids，即 UserSecretsId）每次生成都不同，出现在 csproj 与来源标记里。
# 只差这一处时保留工作区里的值：否则每次同步都改写 csproj、触发 restore，用户机密的目录也随之变化。
# 判据与 test-template-generation.py 的 normalize 相同（标记里只有 UserSecretsId 是 GUID）。
function Test-SameExceptTemplateGuids([string]$Left, [string]$Right) {
    $pattern = if ($Left.EndsWith('.csproj')) { '<UserSecretsId>[^<]+</UserSecretsId>' } else { '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' }
    $normalize = { param($Path) [regex]::Replace([IO.File]::ReadAllText($Path), $pattern, 'TEMPLATE-GUID') }
    return (& $normalize $Left) -ceq (& $normalize $Right)
}

# $Recorded 是上次同步写入的文件。工作区里不在新模板中的文件：记录过的（模板已删）删除；
# 未记录且不被项目 .gitignore 忽略的只会在 -Discard 时到达这里，同样删除；被忽略的本地文件保留。
function Sync-FromStaging($Current, $Recorded) {
    $fresh = Get-TreeDigests $stagingRoot $null
    $written = 0; $removed = 0
    $obsolete = @($Current.Keys | Where-Object { -not $fresh.ContainsKey($_) })
    $ignored = Get-IgnoredPaths @($obsolete | Where-Object { -not $Recorded.ContainsKey($_) })
    # 先删后写：只改大小写的重命名在不区分大小写的文件系统上是同一个文件，先写新名再删旧名会把新文件删掉
    foreach ($relative in $obsolete) {
        if ($ignored.Contains($relative)) { continue }
        Remove-Item -LiteralPath (Join-Path $projectRoot $relative) -Force
        $removed++
    }
    foreach ($relative in @($fresh.Keys)) {
        if ($Current.ContainsKey($relative) -and $Current[$relative] -ceq $fresh[$relative]) { continue }
        $target = Join-Path $projectRoot $relative
        if ($Current.ContainsKey($relative) -and ($relative.EndsWith('.csproj') -or $relative -ceq '.leistd/template.json') -and
            (Test-SameExceptTemplateGuids $target (Join-Path $stagingRoot $relative))) {
            $fresh[$relative] = $Current[$relative]
            continue
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        # 只写内容变化的文件：未变的文件保留时间戳，增量构建与 lint 缓存照常命中
        Copy-Item -LiteralPath (Join-Path $stagingRoot $relative) -Destination $target -Force
        $written++
    }
    # 上次与本次模板拥有的目录都要检查：模板删掉的同名目录（如某个 .cache/）清空后也要删除
    Remove-EmptyDirectories $projectRoot (Get-OwnedDirectories @($fresh, $Recorded))
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    Write-Host "镜像完成：写入 $written 个文件，删除 $removed 个文件，共 $($fresh.Count) 个模板文件。"
    return , $fresh
}

function Update-Dependencies($Files, $State, $Candidate) {
    $restoreDigest = Get-InputDigest $Files { $_ -match $restoreInputPattern } $Candidate.CandidateVersion
    if ($State.CandidateVersion -cne $Candidate.CandidateVersion -or $State.RestoreDigest -cne $restoreDigest) {
        if ($State.CandidateVersion -and $State.CandidateVersion -cne $Candidate.CandidateVersion) {
            Write-Host "候选包版本变化：$($State.CandidateVersion) → $($Candidate.CandidateVersion)"
            # 解包目录只属于本工作区；清掉旧候选版本，来源断言才能要求“全部是当前候选”
            foreach ($stale in @(Get-ChildItem -LiteralPath $packagesRoot -Directory -Filter 'leistd.*' -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $stale.FullName -Recurse -Force
            }
        }
        # 工作区里会手工执行不带 --no-restore 的 dotnet build/test，需要隐式 restore 也用候选版本
        Set-CandidateVersionOverride $workspace $Candidate.CandidateVersion -ImplicitRestore
        $solution = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "backend") -File | Where-Object { $_.Extension -in '.sln', '.slnx' } | Select-Object -First 1)
        if ($solution.Count -eq 0) { throw "生成项目里没有后端解决方案：$projectRoot/backend" }
        Write-Host "==> restore（候选包 $($Candidate.CandidateVersion)）" -ForegroundColor Cyan
        Invoke-External "dotnet" @("restore", $solution[0].Name) (Join-Path $projectRoot "backend")
        Assert-CandidatePackages $packagesRoot $Candidate
        $State.CandidateVersion = $Candidate.CandidateVersion
        $State.RestoreDigest = $restoreDigest
    }
    else {
        Write-Host "restore 输入未变，跳过 restore。"
    }

    $frontend = Join-Path $projectRoot "frontend"
    if (Test-Path -LiteralPath $frontend) {
        $npmDigest = Get-InputDigest $Files { $npmInputs -contains $_ }
        if ($State.NpmDigest -cne $npmDigest -or -not (Test-Path -LiteralPath (Join-Path $frontend "node_modules"))) {
            Write-Host "==> npm ci" -ForegroundColor Cyan
            Invoke-External "npm" @("ci") $frontend
            $State.NpmDigest = $npmDigest
        }
        else {
            Write-Host "package.json 与锁文件未变，跳过 npm ci。"
        }
    }
}

# 同步记录用 System.Text.Json 读：ConvertFrom-Json -AsHashtable 的键不区分大小写，只差大小写的两个路径会冲突
function Read-State {
    if (-not (Test-Path -LiteralPath $statePath)) { return $null }
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($statePath))
    try {
        $root = $document.RootElement
        $text = { param($Name) $value = $root.GetProperty($Name); if ($value.ValueKind -eq 'Null') { $null } else { $value.ToString() } }
        $files = New-PathMap
        foreach ($property in $root.GetProperty('Files').EnumerateObject()) { $files[$property.Name] = $property.Value.GetString() }
        return [ordered]@{
            Version = $root.GetProperty('Version').GetInt32(); Scenario = & $text 'Scenario'; CandidateVersion = & $text 'CandidateVersion'
            RestoreDigest = & $text 'RestoreDigest'; NpmDigest = & $text 'NpmDigest'; Files = $files
        }
    }
    finally { $document.Dispose() }
}

function Save-State($State) {
    $sorted = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $State.Files.GetEnumerator()) { $sorted[$entry.Key] = $entry.Value }
    $copy = [ordered]@{}
    foreach ($key in $State.Keys) { $copy[$key] = if ($key -ceq 'Files') { $sorted } else { $State[$key] } }
    [IO.File]::WriteAllText($statePath, ($copy | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
}

# ── 主流程 ──────────────────────────────────────────────────────────────────────

New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$candidate = Read-CandidateManifest $feedRoot
$state = Read-State
if ($state -and ($state.Scenario -cne $Scenario -or $state.Version -ne 1)) { throw "工作区记录与场景不符，删除 $workspace 后重新生成。" }

if (-not $state) {
    if (Test-Path -LiteralPath $projectRoot) { throw "$projectRoot 已存在但没有同步记录；删除该目录后重新生成。" }
    Write-Host "首次生成工作区 $Scenario（模板源 $templateSource）" -ForegroundColor Cyan
    $state = [ordered]@{ Version = 1; Scenario = $Scenario; CandidateVersion = $null; RestoreDigest = $null; NpmDigest = $null; Files = (New-PathMap) }
}
elseif (-not $Sync) {
    Write-Host "工作区已存在：$projectRoot。改了模板源码后用 -Sync 同步。"
    exit 0
}
else {
    $current = Get-TreeDigests $projectRoot (Get-OwnedDirectories @($state.Files))
    $drift = Get-Drift $state.Files $current
    if ($drift.Count -gt 0 -and -not $Discard) {
        Write-Host "工作区里有 $($drift.Count) 处未同步的修改，-Sync 已停止，没有覆盖任何文件：" -ForegroundColor Red
        $drift | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "同步方向只有模板源 → 工作区：要保留的改动请手工移回 template/，确认可以丢弃时加 -Discard。" -ForegroundColor Red
        exit 1
    }
    if ($drift.Count -gt 0) { Write-Warning "按 -Discard 丢弃 $($drift.Count) 处未同步的修改。" }
}

Write-WorkspaceNuGetConfig
New-StagingProject
$current = Get-TreeDigests $projectRoot (Get-OwnedDirectories @($state.Files))
$state.Files = Sync-FromStaging $current $state.Files
# 文件已镜像：先记下，依赖步骤失败时下次 -Sync 不会把刚写入的文件误报为未同步的修改
Save-State $state
Update-Dependencies $state.Files $state $candidate
Save-State $state

Write-Host ""
Write-Host "工作区就绪：$projectRoot（候选包 $($state.CandidateVersion)）" -ForegroundColor Green
Write-Host "  目标测试的收窄命令见 $projectRoot/docs/standards/testing.md；快速检查：pwsh scripts/verify.ps1 -Fast"
