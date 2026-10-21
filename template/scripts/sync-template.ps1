#!/usr/bin/env pwsh

<#
.SYNOPSIS
    把项目同步到指定版本的 Leistd.Templates 模板包：以相同输入重建旧版（基线）与新版（目标），由 git 三方合并进项目。

.DESCRIPTION
    来源标记 .leistd/template.json 记录项目所用的模板包版本与全部生成输入（读取与重建见 scripts/template-marker.ps1）。
    同步时在隔离的模板目录中分别安装旧版与新版模板包（dotnet new 的调试选项 --debug:custom-hive，
    不影响本机已安装的模板），按标记生成基线与目标，两者都把 UserSecretsId 换回标记记录的值。
    新版删除或改名了标记里的参数、或不再接受记录的取值时，在生成前失败并指向该版本 Release 的迁移说明。

    基线与目标在项目仓库里记成两个不挂在任何分支上的提交（目标以基线为父提交），再用
    git cherry-pick --no-commit 把 基线 → 目标 的变化合并进工作区与暂存区：文本、二进制、删除/修改、改名
    （含只改大小写）都按 git 的合并规则处理，冲突以 git 的未合并路径呈现，文本冲突标记为 diff3 样式。
    git 合并会直接覆盖被忽略的本地文件，因此目标新增的路径上已有被忽略的本地文件时，在合并前失败并列出这些文件。
    这项检查按文件名精确比较（区分大小写）：不区分大小写的文件系统上，与新增路径只差大小写的被忽略文件不会被检出。
    基线与目标按生成结果原样入库（git add -f），不受全局 core.excludesFile 与 .git/info/exclude 影响。
    依赖清单 package.json 合并成功后，在该目录执行 npm install --package-lock-only 重新生成锁文件 package-lock.json；
    清单本身冲突时先解决清单，再在该目录执行同一命令。NuGet 依赖由 Directory.Packages.props 统一管理、没有锁文件，
    合并后由 verify 的 restore 重新解析。
    来源标记不参与合并：没有冲突时写成目标版本的标记，以 0 退出；有冲突时以 2 退出，标记不更新，
    目标版本的标记先写在 .leistd/template.pending.json，解决冲突、git add 后执行 -Complete。
    脚本只改工作区与暂存区，不提交；完成后运行 scripts/verify.ps1，再提交（git 已按目标版本写好提交说明）。

    运行前提：git、.NET SDK（dotnet new）；合并到依赖清单变化时另需 npm。开始同步（-To、-Adopt）时 git 工作区必须干净。
    退出码：0 完成；2 有冲突待解决；1 失败（诊断见输出）。工作区、标记、参数、被忽略文件与存量基线的检查都在改动工作区之前，
    这些检查失败时工作区不变。放弃一次同步（含合并途中出错）：git reset --hard HEAD && git clean -fd（开始时工作区是干净的）。

.PARAMETER To
    目标模板包版本，即已发布的 Leistd.Templates 版本号（如 0.2.0）。

.PARAMETER PackageSource
    模板包所在的 NuGet 源：源地址或含 nupkg 的本地目录，可多个；不给时用 NuGet 配置里的源（通常是 nuget.org）。
    旧版与新版都从这里安装。

.PARAMETER Complete
    结束一次有冲突的同步：git 没有未合并路径，且本次改动的文件里没有冲突标记时，写入目标版本的标记并删除
    .leistd/template.pending.json。不核对锁文件与 package.json 是否一致：没按清单重新生成时，verify 的 npm ci 会失败。

.PARAMETER Adopt
    一次性接入没有来源标记的已有项目（由某个提交的模板源码生成，而非模板包）。以 -BaseSource 的源码按 -Name 与 -Params
    重建基线，先与项目生成时的提交（-BaselineCommit，缺省为唯一的根提交）比较：有差异时列出并失败，逐项确认都是项目自身的改动后加 -AcceptBaselineDiff 重跑；
    输入推断错了时差异会成片出现。项目改过的 docs/standards/ 文件迁到 docs/project/ 下同名文件，standards 恢复为基线后随模板更新。
    之后直接把 源码基线 → -To 指定的模板包 合并进项目，冲突与 -Complete 规则同上；只写入目标版本的标记，此后用 -To 正常同步。

.PARAMETER BaseSource
    -Adopt 的基线模板源码目录：项目生成时所用提交的模板目录（含 .template.config/）。

.PARAMETER Name
    -Adopt 时项目生成所用的名称（dotnet new -n 的值）。

.PARAMETER Params
    -Adopt 时项目生成所用的参数，写成 名称=取值，多个用逗号分隔（如 ServiceRole=Resource,IncludeFrontend=false）；
    未给的参数取基线源码的默认值。

.PARAMETER BaselineCommit
    -Adopt 时与基线比较的提交，即项目由模板生成的那次提交；缺省为唯一的根提交，有多个根提交时必须给出。

.PARAMETER AcceptBaselineDiff
    -Adopt 时确认基线与该提交的差异都是项目自身的改动。

.PARAMETER ProjectRoot
    项目根目录，必须是 git 工作树的根目录；缺省为当前目录所在工作树的根目录。

.EXAMPLE
    pwsh scripts/sync-template.ps1 -To 0.2.0
.EXAMPLE
    pwsh scripts/sync-template.ps1 -Complete
#>
[CmdletBinding(DefaultParameterSetName = 'Sync')]
param(
    [Parameter(ParameterSetName = 'Sync', Mandatory)]
    [Parameter(ParameterSetName = 'Adopt', Mandatory)]
    [string]$To,
    [Parameter(ParameterSetName = 'Sync')]
    [Parameter(ParameterSetName = 'Adopt')]
    [string[]]$PackageSource = @(),
    [Parameter(ParameterSetName = 'Complete', Mandatory)]
    [switch]$Complete,
    [Parameter(ParameterSetName = 'Adopt', Mandatory)]
    [switch]$Adopt,
    [Parameter(ParameterSetName = 'Adopt', Mandatory)]
    [string]$BaseSource,
    [Parameter(ParameterSetName = 'Adopt', Mandatory)]
    [string]$Name,
    [Parameter(ParameterSetName = 'Adopt')]
    [string[]]$Params = @(),
    [Parameter(ParameterSetName = 'Adopt')]
    [string]$BaselineCommit,
    [Parameter(ParameterSetName = 'Adopt')]
    [switch]$AcceptBaselineDiff,
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'template-marker.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

$PackageId = 'Leistd.Templates'
$MarkerRelative = '.leistd/template.json'
$PendingRelative = '.leistd/template.pending.json'
$StandardsPrefix = 'docs/standards/'
$ProjectRulesPrefix = 'docs/project/'
$AbandonHint = '放弃本次同步：git reset --hard HEAD && git clean -fd（开始时工作区是干净的）。'

# 命令行经 pwsh -File 传入时数组是一个逗号分隔的字符串
function Split-ListArgument([string[]]$Values) {
    return @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# 只返回 stdout（多处按路径解析）；stderr 另存，失败时并入诊断，也留给调用方读取（$script:GitError）
function Invoke-Git([string[]]$Arguments, [int[]]$AllowedExitCodes = @(0)) {
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $output = & git -C $script:Root -c core.quotepath=off --literal-pathspecs @Arguments 2> $errorFile
        $code = $LASTEXITCODE
        $script:GitError = [IO.File]::ReadAllText($errorFile).Trim()
    } finally { Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue }
    if ($code -notin $AllowedExitCodes) { throw "git $($Arguments -join ' ') 失败（退出码 $code）：`n$((@($output) + $script:GitError) -join "`n")" }
    $global:LASTEXITCODE = $code
    return @($output | ForEach-Object { "$_" })
}

function Resolve-ProjectRoot {
    if ($ProjectRoot) { $start = (Resolve-Path -LiteralPath $ProjectRoot).Path } else { $start = (Get-Location).Path }
    $top = & git -C $start rev-parse --show-toplevel 2>&1
    if ($LASTEXITCODE -ne 0) { throw "不在 git 仓库内：$start。同步依赖 git 记录项目改动，先 git init 并提交。" }
    $prefix = "$(& git -C $start rev-parse --show-prefix 2>&1)".Trim()
    if ($ProjectRoot -and $prefix) { throw "-ProjectRoot 必须是 git 工作树的根目录：$start 位于 $(("$top" | Select-Object -Last 1).Trim()) 之下（$prefix）。" }
    return [IO.Path]::GetFullPath(("$top" | Select-Object -Last 1).Trim())
}

function Get-ProjectPath([string]$Relative) { return Join-Path $script:Root $Relative }

function Get-UnmergedPaths { return @(Invoke-Git @('diff', '--name-only', '--diff-filter=U') | Sort-Object -Unique -CaseSensitive) }

function Write-JsonFile([string]$Path, $Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))
}

# 包内模板定义：参数（名称 → 类型与可选值）与仓库地址（Release 链接用）
function Read-TemplateDefinition([string]$TemplateJson, [string]$Version, [string]$RepositoryUrl) {
    $config = $TemplateJson | ConvertFrom-Json -AsHashtable
    $parameters = @{}
    foreach ($entry in $config.symbols.GetEnumerator()) {
        if ($entry.Value.type -cne 'parameter') { continue }
        $choices = @($entry.Value.choices | Where-Object { $_ } | ForEach-Object { $_.choice })
        $parameters[$entry.Key] = @{ DataType = $entry.Value.datatype; Choices = $choices; Default = $entry.Value.defaultValue }
    }
    return @{ Version = $Version; Parameters = $parameters; RepositoryUrl = $RepositoryUrl }
}

function Read-PackageDefinition([string]$HivePath, [string]$Version) {
    $packages = @(Get-ChildItem -LiteralPath (Join-Path $HivePath 'packages') -Filter '*.nupkg' -File -ErrorAction SilentlyContinue)
    if ($packages.Count -ne 1) { throw "隔离模板目录里应恰好有一个模板包：$HivePath" }
    $archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $configEntries = @($archive.Entries | Where-Object { $_.FullName -like 'content/*/.template.config/template.json' })
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName.EndsWith('.nuspec') })
        if ($configEntries.Count -ne 1 -or $nuspecEntries.Count -ne 1) { throw "模板包结构不符（应有一个模板定义与一个 nuspec）：$($packages[0].Name)" }
        $read = { param($entry) $reader = [IO.StreamReader]::new($entry.Open()); try { $reader.ReadToEnd() } finally { $reader.Dispose() } }
        [xml]$nuspec = & $read $nuspecEntries[0]
        $repository = "$($nuspec.package.metadata.repository.url)".Trim()
        if (-not $repository) { $repository = "$($nuspec.package.metadata.projectUrl)".Trim() }
        return Read-TemplateDefinition (& $read $configEntries[0]) $Version ($repository.TrimEnd('/') -replace '\.git$', '')
    } finally { $archive.Dispose() }
}

# 新版必须接受标记里的每个参数与取值：删除、改名或收窄取值都要按该版本 Release 的迁移说明调整，脚本不猜映射
function Assert-ParameterCompatibility([hashtable]$Parameters, [hashtable]$Definition) {
    $problems = @()
    foreach ($parameterName in @($Parameters.Keys | Sort-Object)) {
        $symbol = $Definition.Parameters[$parameterName]
        if (-not $symbol) { $problems += "参数 $parameterName 在 $PackageId $($Definition.Version) 中已删除或改名"; continue }
        $value = $Parameters[$parameterName]
        if ($symbol.DataType -ceq 'bool' -and $value -isnot [bool]) { $problems += "参数 $parameterName 在 $($Definition.Version) 中是布尔，记录的取值为 '$value'" }
        if ($symbol.DataType -ceq 'choice' -and "$value" -cnotin $symbol.Choices) {
            $problems += "参数 $parameterName 的取值 '$value' 在 $($Definition.Version) 中不再可选（可选：$($symbol.Choices -join ', ')）"
        }
    }
    if ($problems) {
        $release = if ($Definition.RepositoryUrl) { "$($Definition.RepositoryUrl)/releases/tag/template-v$($Definition.Version)" } else { "template-v$($Definition.Version) 的 Release" }
        throw "来源标记的参数与目标版本不兼容：`n  $($problems -join "`n  ")`n按 $release 正文里的迁移说明调整后再同步。"
    }
}

# 从模板源码生成存量接入的基线：旧源码不写来源标记，UserSecretsId 按项目文件路径换回项目里的现值
function New-SourceBaseline([hashtable]$Marker, [string]$HivePath, [string]$OutputPath) {
    Invoke-TemplateCli (@('new', $script:TemplateShortName, '-o', $OutputPath, '--debug:custom-hive', $HivePath) + (Get-TemplateMarkerArguments $Marker))
    foreach ($file in Get-ChildItem -LiteralPath $OutputPath -Recurse -File -Filter '*.csproj') {
        $relative = [IO.Path]::GetRelativePath($OutputPath, $file.FullName).Replace('\', '/')
        $text = [IO.File]::ReadAllText($file.FullName)
        $match = [regex]::Match($text, '<UserSecretsId>([^<]+)</UserSecretsId>')
        if ($match.Success -and $Marker.userSecretsIds.ContainsKey($relative)) {
            [IO.File]::WriteAllText($file.FullName, $text.Replace($match.Groups[1].Value, $Marker.userSecretsIds[$relative]), [Text.UTF8Encoding]::new($false))
        }
    }
}

# 把一棵生成目录记成项目仓库里的提交（不挂在任何分支上）：用临时索引，项目的暂存区与工作区不受影响。
# 来源标记由本脚本单独写入，不进提交
function New-TreeCommit([string]$Directory, [string]$Message, [string]$Parent) {
    $marker = Join-Path $Directory $MarkerRelative
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
    $env:GIT_INDEX_FILE = Join-Path $script:Work "index-$([Guid]::NewGuid().ToString('N'))"
    try {
        # -f：生成结果原样入库，不让全局 core.excludesFile、.git/info/exclude 或生成的 .gitignore 漏掉模板文件
        Invoke-Git @('-c', 'core.excludesFile=', '--work-tree', $Directory, 'add', '-A', '-f') | Out-Null
        $tree = (Invoke-Git @('write-tree') | Select-Object -Last 1).Trim()
    } finally { Remove-Item Env:GIT_INDEX_FILE }
    $arguments = @('-c', 'user.name=Leistd.Templates', '-c', 'user.email=templates@leistd.invalid', 'commit-tree', '--no-gpg-sign', $tree, '-m', $Message)
    if ($Parent) { $arguments += @('-p', $Parent) }
    return (Invoke-Git $arguments | Select-Object -Last 1).Trim()
}

function Get-ProjectUserSecretsIds {
    $ids = @{}
    foreach ($relative in @(Invoke-Git @('ls-files') | Where-Object { $_.EndsWith('.csproj') } | Sort-Object -CaseSensitive)) {
        $match = [regex]::Match([IO.File]::ReadAllText((Get-ProjectPath $relative)), '<UserSecretsId>([^<]+)</UserSecretsId>')
        if ($match.Success) { $ids[$relative] = $match.Groups[1].Value }
    }
    return $ids
}

# 存量接入：参数 名称=取值 按基线源码的定义转成标记里的类型，未给的取源码默认值
function ConvertTo-AdoptMarker([hashtable]$SourceDefinition) {
    $parameters = @{}
    $given = @{}
    foreach ($pair in (Split-ListArgument $Params)) {
        if ($pair -notmatch '^([A-Za-z][A-Za-z0-9]*)=(.*)$') { throw "-Params 的项应写成 名称=取值：'$pair'" }
        $given[$Matches[1]] = $Matches[2]
    }
    $unknown = @($given.Keys | Where-Object { -not $SourceDefinition.Parameters.ContainsKey($_) })
    if ($unknown) { throw "基线源码没有这些参数：$($unknown -join ', ')（可用：$(@($SourceDefinition.Parameters.Keys | Sort-Object) -join ', ')）" }
    foreach ($parameterName in @($SourceDefinition.Parameters.Keys | Sort-Object)) {
        $symbol = $SourceDefinition.Parameters[$parameterName]
        $raw = if ($given.ContainsKey($parameterName)) { $given[$parameterName] } else { "$($symbol.Default)" }
        if ($symbol.DataType -ceq 'bool') {
            if ($raw -notin @('true', 'false')) { throw "参数 $parameterName 是布尔，取值应为 true 或 false：'$raw'" }
            $parameters[$parameterName] = $raw -eq 'true'
        } else { $parameters[$parameterName] = $raw }
    }
    $ids = Get-ProjectUserSecretsIds
    if ($ids.Count -eq 0) { throw '项目里找不到带 UserSecretsId 的项目文件，无法与模板生成结果对齐。' }
    return @{ schemaVersion = 1; package = @{ id = $PackageId; version = 'source'; sourceSha = '' }; sdkVersion = ''; name = $Name; parameters = $parameters; userSecretsIds = $ids }
}

# 基线与项目生成时的提交比较：只列基线一侧新增或两侧内容不同的路径（提交多出的文件是项目新增，不列）
function Assert-AdoptBaseline([string]$BaseCommit) {
    if ($BaselineCommit) {
        $commit = (Invoke-Git @('rev-parse', '--verify', "$BaselineCommit^{commit}") | Select-Object -Last 1).Trim()
    } else {
        $roots = @(Invoke-Git @('rev-list', '--max-parents=0', 'HEAD'))
        if ($roots.Count -ne 1) { throw "项目历史有 $($roots.Count) 个根提交，无法确定生成时的提交：用 -BaselineCommit 指定。" }
        $commit = $roots[0].Trim()
    }
    $label = $commit.Substring(0, 12)
    $differences = @(Invoke-Git @('diff', '--no-renames', '--name-status', '--diff-filter=AM', $commit, $BaseCommit))
    if ($differences.Count -eq 0) { Write-Host "基线与提交 $label 一致。"; return }
    $listed = @($differences | Select-Object -First 100 | ForEach-Object {
        $status, $path = $_ -split "`t", 2
        if ($status -ceq 'A') { "  缺少：$path" } else { "  修改：$path" } })
    if ($differences.Count -gt 100) { $listed += "  …另 $($differences.Count - 100) 项" }
    $message = "基线与提交 $label 有 $($differences.Count) 处差异：`n$($listed -join "`n")"
    if (-not $AcceptBaselineDiff) {
        throw "$message`n逐项确认都是项目自身的改动后加 -AcceptBaselineDiff 重跑；成片出现时多半是 -BaseSource、-Name、-Params 与生成时不符，
或该提交不是生成项目的那次提交（用 -BaselineCommit 指定）。"
    }
    Write-Host "$message`n已按 -AcceptBaselineDiff 确认。"
}

# git 合并把被忽略的文件当作可丢弃，会直接覆盖：目标新增的路径上已有被忽略的本地文件时先停下
function Assert-NoIgnoredCollision([string]$BaseCommit, [string]$TargetCommit) {
    # 按文件名精确比较：不区分大小写的文件系统上，只改大小写的改名不算占用
    $existing = @(Invoke-Git @('diff', '--name-only', '--no-renames', '--diff-filter=A', $BaseCommit, $TargetCommit) | Where-Object {
        $directory = Split-Path -Parent (Get-ProjectPath $_)
        (Test-Path -LiteralPath $directory -PathType Container) -and
            @([IO.Directory]::EnumerateFileSystemEntries($directory) | ForEach-Object { [IO.Path]::GetFileName($_) }) -ccontains (Split-Path -Leaf $_) })
    if (-not $existing) { return }
    $tracked = @(Invoke-Git (@('ls-files', '--') + $existing))
    $collisions = @($existing | Where-Object { $_ -cnotin $tracked })
    if ($collisions) {
        throw "目标版本新增的路径上已有被忽略的本地文件，git 合并会直接覆盖它们：`n  $($collisions -join "`n  ")`n先移走或改名（需要时合并后再手工并回）后重跑。"
    }
}

# 依赖清单合并成功后重新生成锁文件；清单冲突的目录留给项目先解决清单
function Update-LockFiles([string]$BaseCommit, [string]$TargetCommit) {
    $directories = @(Invoke-Git @('diff', '--name-only', $BaseCommit, $TargetCommit) | Where-Object { $_ -match '(^|/)package(-lock)?\.json$' } |
        ForEach-Object { $_.Substring(0, $_.LastIndexOf('package')) } | Sort-Object -Unique -CaseSensitive)
    $unmerged = Get-UnmergedPaths
    $pending = @()
    foreach ($directory in $directories) {
        $manifest = "${directory}package.json"; $lock = "${directory}package-lock.json"
        Invoke-Git @('cat-file', '-e', "${TargetCommit}:$lock") @(0, 1, 128) | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Get-ProjectPath $manifest))) { continue }
        if ($manifest -cin $unmerged) { $pending += $lock; continue }
        if ($lock -cin $unmerged) { Invoke-Git @('checkout', '--theirs', '--', $lock) | Out-Null }
        else {
            Invoke-Git @('diff', '--quiet', $TargetCommit, '--', $manifest, $lock) @(0, 1) | Out-Null
            if ($LASTEXITCODE -eq 0) { continue }
        }
        if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "找不到 npm，无法按合并后的 $manifest 重新生成 $lock。" }
        Push-Location (Get-ProjectPath $directory)
        try { $output = & npm install --package-lock-only --ignore-scripts --no-audit --no-fund 2>&1 }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw "在 $directory 执行 npm install --package-lock-only 失败（退出码 $LASTEXITCODE）：$(@($output | Select-Object -Last 5) -join ' ')" }
        Invoke-Git @('add', '--', $lock) | Out-Null
        Write-Host "已按合并后的 $manifest 重新生成 $lock"
    }
    return , $pending
}

function Invoke-Complete {
    $pendingPath = Get-ProjectPath $PendingRelative
    if (-not (Test-Path -LiteralPath $pendingPath -PathType Leaf)) { throw "没有待完成的同步（缺少 $PendingRelative）：-Complete 只用于结束一次有冲突的同步。" }
    $target = Read-TemplateMarker $script:Root $PendingRelative
    $unmerged = Get-UnmergedPaths
    if ($unmerged) { throw "还有未合并的路径，来源标记未更新（解决后 git add 或 git rm）：`n  $($unmerged -join "`n  ")" }
    # 冲突标记行：<<<<<<< 、||||||| （diff3 基线段）与 >>>>>>> 后跟标签；======= 单独成行在 Markdown 里另有含义，不作判据
    # 只看本次同步改动过的文件（相对 HEAD），项目原有文件里的同形文本不算
    $changed = [Collections.Generic.HashSet[string]]::new([string[]]@(Invoke-Git @('diff', '--name-only', 'HEAD')), [StringComparer]::Ordinal)
    $markers = @(Invoke-Git @('grep', '-n', '-I', '-E', '^(<{7}|[|]{7}|>{7})( |$)') @(0, 1) | Where-Object { $changed.Contains(($_ -split ':', 2)[0]) })
    if ($markers) { throw "还有冲突标记，来源标记未更新：`n  $(@($markers | Select-Object -First 20) -join "`n  ")" }
    Write-JsonFile (Get-ProjectPath $MarkerRelative) $target
    Remove-Item -LiteralPath $pendingPath
    # 用 git add -A 解决冲突时待完成标记也被暂存了：一并移出暂存区，不能随同步结果提交
    Invoke-Git @('rm', '--cached', '--quiet', '--ignore-unmatch', '--', $PendingRelative) | Out-Null
    Invoke-Git @('add', '--', $MarkerRelative) | Out-Null
    Write-Host "✅ 已同步到 $PackageId $($target.package.version)。运行 pwsh scripts/verify.ps1 验证后提交。" -ForegroundColor Green
    return 0
}

function Invoke-TemplateSync {
    $PackageSource = Split-ListArgument $PackageSource
    if (Test-Path -LiteralPath (Get-ProjectPath $PendingRelative)) { throw "有未完成的同步（$PendingRelative）：先解决冲突并执行 -Complete。" }
    $status = @(Invoke-Git @('status', '--porcelain', '--untracked-files=all'))
    if ($status) { throw "工作区有未提交的修改，先提交或移走再同步（同步结果要能与项目改动区分开）：`n  $($status -join "`n  ")" }
    $hasMarker = Test-Path -LiteralPath (Get-ProjectPath $MarkerRelative) -PathType Leaf
    if ($Adopt -and $hasMarker) { throw "项目已有来源标记 $MarkerRelative，用 -To 同步，不需要 -Adopt。" }
    if (-not $Adopt) {
        $marker = Read-TemplateMarker $script:Root
        if ($marker.package.version -ceq '0.0.0-source') {
            throw "来源标记的版本是 0.0.0-source：项目由模板源码目录生成，不是已发布的模板包，无法按版本重建基线。改用 -Adopt -BaseSource <生成所用的模板源码目录>。"
        }
        if ($marker.package.version -ceq $To) { Write-Host "来源标记已是 $PackageId $To，无需同步。"; return 0 }
    }

    New-Item -ItemType Directory -Path $script:Work | Out-Null
    $baseRoot = Join-Path $script:Work 'base'
    $targetRoot = Join-Path $script:Work 'target'
    $targetHive = Join-Path $script:Work 'hive-target'
    $baseHive = Join-Path $script:Work 'hive-base'
    Install-TemplatePackage "${PackageId}::$To" $targetHive $PackageSource
    $targetDefinition = Read-PackageDefinition $targetHive $To
    if ($Adopt) {
        $BaseSource = (Resolve-Path -LiteralPath $BaseSource).Path
        $sourceConfig = Join-Path $BaseSource '.template.config/template.json'
        if (-not (Test-Path -LiteralPath $sourceConfig -PathType Leaf)) { throw "-BaseSource 不是模板源码目录（缺少 .template.config/template.json）：$BaseSource" }
        $marker = ConvertTo-AdoptMarker (Read-TemplateDefinition ([IO.File]::ReadAllText($sourceConfig)) 'source' '')
        Assert-ParameterCompatibility $marker.parameters $targetDefinition
        Install-TemplatePackage $BaseSource $baseHive
        New-SourceBaseline $marker $baseHive $baseRoot
        $baseLabel = 'template source'
    } else {
        Assert-ParameterCompatibility $marker.parameters $targetDefinition
        Install-TemplatePackage "${PackageId}::$($marker.package.version)" $baseHive $PackageSource
        New-TemplateRebuild $marker $baseHive $baseRoot
        $baseLabel = "$PackageId $($marker.package.version)"
    }
    New-TemplateRebuild $marker $targetHive $targetRoot -AllowProjectChanges
    $targetMarker = Read-TemplateMarker $targetRoot
    if ($targetMarker.package.version -cne $To) { throw "目标模板包写入的版本是 $($targetMarker.package.version)，不是 $To" }

    $baseCommit = New-TreeCommit $baseRoot $baseLabel
    $targetCommit = New-TreeCommit $targetRoot "chore: 同步模板 $baseLabel → $PackageId $To" $baseCommit
    $migrations = @()
    if ($Adopt) {
        Assert-AdoptBaseline $baseCommit
        # 项目改过的模板规范迁到 docs/project/：standards 恢复为基线，随模板整体更新
        $migrations = @(Invoke-Git @('diff', '--name-only', '--no-renames', '--diff-filter=M', 'HEAD', $baseCommit, '--', $StandardsPrefix))
        $occupied = @($migrations | ForEach-Object { $ProjectRulesPrefix + $_.Substring($StandardsPrefix.Length) } | Where-Object { Test-Path -LiteralPath (Get-ProjectPath $_) })
        if ($occupied) { throw "要迁入的项目规则文件已存在，先手工合并：$($occupied -join ', ')" }
    }
    Assert-NoIgnoredCollision $baseCommit $targetCommit

    # 以下开始改动工作区
    try {
        foreach ($relative in $migrations) {
            $destination = $ProjectRulesPrefix + $relative.Substring($StandardsPrefix.Length)
            New-Item -ItemType Directory -Path (Split-Path -Parent (Get-ProjectPath $destination)) -Force | Out-Null
            Copy-Item -LiteralPath (Get-ProjectPath $relative) -Destination (Get-ProjectPath $destination)
            Copy-Item -LiteralPath (Join-Path $baseRoot $relative) -Destination (Get-ProjectPath $relative) -Force
            Invoke-Git @('add', '--', $destination, $relative) | Out-Null
            Write-Host "规范迁移：$relative 的项目改动迁到 $destination（只保留项目规则后登记到 docs/README.md）"
        }
        $output = Invoke-Git @('-c', 'merge.conflictStyle=diff3', 'cherry-pick', '--no-commit', $targetCommit) @(0, 1)
        $picked = $LASTEXITCODE
        $unmerged = Get-UnmergedPaths
        if ($picked -ne 0 -and -not $unmerged) { throw "git cherry-pick 失败：`n$((@($output) + $script:GitError) -join "`n")" }
        $lockPending = Update-LockFiles $baseCommit $targetCommit
    } catch {
        throw "$($_.Exception.Message)`n工作区已部分改动，来源标记未更新。$AbandonHint"
    }

    if (-not $unmerged) {
        Write-JsonFile (Get-ProjectPath $MarkerRelative) $targetMarker
        Invoke-Git @('add', '--', $MarkerRelative) | Out-Null
        Write-Host "✅ 已同步到 $PackageId $To，没有冲突（结果已暂存）。运行 pwsh scripts/verify.ps1 验证后提交。" -ForegroundColor Green
        return 0
    }
    Write-JsonFile (Get-ProjectPath $PendingRelative) $targetMarker
    Write-Host "同步到 $PackageId $To 有 $($unmerged.Count) 个未合并路径，来源标记未更新：" -ForegroundColor Yellow
    Invoke-Git (@('status', '--short', '--') + $unmerged) | ForEach-Object { Write-Host "  $_" }
    Write-Host @"
逐项解决后 git add（删除/修改冲突里决定删除的用 git rm）：
  - 文本冲突：编辑掉冲突标记（HEAD 一侧是项目，||||||| 段是基线，另一侧是目标版本）；
  - 二进制冲突：git checkout --ours（项目）或 --theirs（目标版本）-- <路径>；
  - 删除/修改冲突（DU/UD）：保留就 git add <路径>，删除就 git rm <路径>。
"@
    foreach ($lock in $lockPending) { Write-Host "  - 依赖清单冲突：解决 $($lock.Substring(0, $lock.Length - 'package-lock.json'.Length))package.json 后在该目录执行 npm install --package-lock-only，再 git add $lock。" }
    Write-Host "全部解决后执行 pwsh scripts/sync-template.ps1 -Complete。$AbandonHint"
    return 2
}

$exitCode = 1
$script:Work = Join-Path ([IO.Path]::GetTempPath()) "leistd-sync-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
try {
    $script:Root = Resolve-ProjectRoot
    $exitCode = if ($Complete) { Invoke-Complete } else { Invoke-TemplateSync }
} catch {
    Write-Host "同步失败：$($_.Exception.Message)" -ForegroundColor Red
} finally {
    Remove-Item -LiteralPath $script:Work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $exitCode
