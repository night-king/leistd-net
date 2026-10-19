#!/usr/bin/env pwsh

<#
.SYNOPSIS
    以真实生成、restore 与 npm ci 验证模板开发工作区（template-dev-workspace.ps1）的同步规则。

.DESCRIPTION
    在模板副本上逐项改动，再对同一个工作区执行 -Sync，核对：
      - 首次运行生成并安装依赖；无改动的 -Sync 不写文件、不 restore、不 npm ci；
      - 不带 --no-restore 的 dotnet build（隐式 restore）在工作区里取到候选版本；
      - 新增、移除 npm 依赖时执行 npm ci，并实际装上、移除；*.props 变化时只 restore；
      - 更换候选包源后重新 restore，rsp 与解包目录都换成新候选版本，来源断言通过；
      - 模板里删除的文件在工作区同样删除；
      - 工作区里有未同步的修改（改动、新增）时 -Sync 停止并列出、不覆盖；构建目录里的文件不算修改；-Discard 才覆盖。
    -Timing 另在预热后的工作区里各计时 5 次后端与前端的局部改动（同步加目标测试），输出中位数。
    第二个候选包源由 framework/build/pack-local-feed.ps1 打出，第一个默认取 .tmp/local-feed。
    修改 template-dev-workspace.ps1 时运行；需要 .NET SDK 与 Node.js，-Timing 另需 Playwright Chromium。
#>

param(
    [string]$Scenario = "resource-capabilities-09",
    [string]$LocalFeedPath,
    [switch]$Timing
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $repoRoot "framework/build/candidate-feed.ps1")
. (Join-Path $PSScriptRoot "template-matrix-scenarios.ps1")
$tempRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".tmp"))
$root = Join-Path $tempRoot ("dev-workspace-selftest/{0}-{1}" -f $PID, (Get-Date -Format "yyyyMMddHHmmssfff"))
if (-not $root.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to use path outside .tmp: $root" }
if (-not $scenarioMap[$Scenario].Frontend) { throw "自检要覆盖 npm 依赖，场景必须带前端：$Scenario" }

$workspaceScript = Join-Path $PSScriptRoot "template-dev-workspace.ps1"
$templateCopy = Join-Path $root "template"
$workspacesRoot = Join-Path $root "workspaces"
$workspace = Join-Path $workspacesRoot $Scenario
$project = Join-Path $workspace "project"
$feedA = if ($LocalFeedPath) { [IO.Path]::GetFullPath($LocalFeedPath, $repoRoot) } else { Join-Path $tempRoot "local-feed" }
$feedB = Join-Path $root "feed-b"
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0

function Invoke-Workspace([string[]]$Arguments, [string]$Feed = $feedA) {
    $call = @("-NoProfile", "-File", $workspaceScript, "-Scenario", $Scenario, "-TemplateRoot", $templateCopy,
        "-WorkspaceRoot", $workspacesRoot, "-LocalFeedPath", $Feed) + $Arguments
    $output = (& pwsh @call 2>&1 | ForEach-Object { "$_" }) -join "`n"
    return [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Assert-Run($Run, [string]$Label, [int]$ExitCode = 0, [string[]]$Contains = @(), [string[]]$Excludes = @()) {
    $problems = @()
    if ($Run.ExitCode -ne $ExitCode) { $problems += "退出码 $($Run.ExitCode)，期望 $ExitCode" }
    foreach ($text in $Contains) { if (-not $Run.Output.Contains($text)) { $problems += "输出缺少「$text」" } }
    foreach ($text in $Excludes) { if ($Run.Output.Contains($text)) { $problems += "输出不应包含「$text」" } }
    if ($problems) { throw "${Label}：$($problems -join '；')`n--- 输出 ---`n$($Run.Output)" }
}

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function Write-Pass([string]$Label) {
    Write-Host "PASS $Label" -ForegroundColor Green
    $script:passed++
}

function Edit-Text([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "夹具失效：$Path 里找不到「$Old」" }
    $index = $text.IndexOf($Old)
    [IO.File]::WriteAllText($Path, $text.Substring(0, $index) + $New + $text.Substring($index + $Old.Length), $utf8)
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    return $sorted[[int][Math]::Floor($sorted.Count / 2)]
}

$restoreMark = "==> restore"
$npmMark = "==> npm ci"
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $null = Read-CandidateManifest $feedA
    Copy-Item -LiteralPath (Join-Path $repoRoot "template") -Destination $templateCopy -Recurse
    $projectName = Get-ScenarioProjectName $Scenario
    $unitTests = "backend/tests/$projectName.UnitTests"

    # 首次运行与无改动同步
    $run = Invoke-Workspace @()
    Assert-Run $run "首次运行" -Contains @($restoreMark, $npmMark, "工作区就绪")
    Assert-True (Test-Path -LiteralPath (Join-Path $project "frontend/node_modules")) "首次运行后没有 node_modules"
    $run = Invoke-Workspace @("-Sync")
    Assert-Run $run "无改动同步" -Contains @("写入 0 个文件，删除 0 个文件", "跳过 restore", "跳过 npm ci") -Excludes @($restoreMark, $npmMark)
    Write-Pass "首次运行生成并安装依赖；无改动的 -Sync 不写文件、不 restore、不 npm ci"

    # 隐式 restore：工作区里手工构建不带 --no-restore
    Push-Location $project
    try { $build = (& dotnet build $unitTests 2>&1 | Out-String); $buildExit = $LASTEXITCODE } finally { Pop-Location }
    Assert-True ($buildExit -eq 0) "工作区里 dotnet build（隐式 restore）失败：`n$build"
    Write-Pass "工作区里不带 --no-restore 的 dotnet build 取到候选版本"

    # 新增 npm 依赖：本地 file: 依赖，不访问网络；两份 package 文件（默认版与本地化版）同改
    $packageFiles = @("frontend/package.json", ".template.config/localization/frontend/package.json")
    $lockFiles = @("frontend/package-lock.json", ".template.config/localization/frontend/package-lock.json")
    $originals = @{}
    foreach ($relative in $packageFiles + $lockFiles) {
        $path = Join-Path $templateCopy $relative
        if (Test-Path -LiteralPath $path) { $originals[$relative] = [IO.File]::ReadAllText($path) }
    }
    Assert-True ($originals.Count -ge 2) "夹具失效：模板副本里没有 package.json 与锁文件"
    foreach ($relative in $packageFiles) {
        if ($originals.Contains($relative)) { Edit-Text (Join-Path $templateCopy $relative) '"dependencies": {' "`"dependencies`": {`n    `"leistd-selftest-dep`": `"file:selftest-dep`"," }
    }
    foreach ($relative in $lockFiles) {
        if (-not $originals.Contains($relative)) { continue }
        $lock = Join-Path $templateCopy $relative
        Edit-Text $lock '"dependencies": {' "`"dependencies`": {`n        `"leistd-selftest-dep`": `"file:selftest-dep`","
        Edit-Text $lock '"packages": {' ("`"packages`": {`n    `"node_modules/leistd-selftest-dep`": { `"resolved`": `"selftest-dep`", `"link`": true },`n" +
            "    `"selftest-dep`": { `"name`": `"leistd-selftest-dep`", `"version`": `"1.0.0`" },")
    }
    $dependencyRoot = Join-Path $templateCopy "frontend/selftest-dep"
    New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $dependencyRoot "package.json"), "{ `"name`": `"leistd-selftest-dep`", `"version`": `"1.0.0`" }`n", $utf8)
    $run = Invoke-Workspace @("-Sync")
    Assert-Run $run "新增 npm 依赖" -Contains @($npmMark, "跳过 restore") -Excludes @($restoreMark)
    Assert-True (Test-Path -LiteralPath (Join-Path $project "frontend/node_modules/leistd-selftest-dep/package.json")) "新增的依赖没有装进 node_modules"
    Write-Pass "新增 npm 依赖：package 文件摘要变化，执行 npm ci 并装上新依赖"

    # 移除 npm 依赖：还原 package 文件并删掉依赖目录（也是“模板删除文件”的一例）
    foreach ($entry in $originals.GetEnumerator()) { [IO.File]::WriteAllText((Join-Path $templateCopy $entry.Key), $entry.Value, $utf8) }
    Remove-Item -LiteralPath $dependencyRoot -Recurse -Force
    $run = Invoke-Workspace @("-Sync")
    Assert-Run $run "移除 npm 依赖" -Contains @($npmMark) -Excludes @($restoreMark)
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $project "frontend/node_modules/leistd-selftest-dep"))) "移除的依赖仍在 node_modules"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $project "frontend/selftest-dep"))) "模板里删除的依赖目录仍在工作区"
    Write-Pass "移除 npm 依赖：执行 npm ci，依赖与模板里删掉的目录都从工作区消失"

    # *.props 变化只 restore
    Edit-Text (Join-Path $templateCopy "backend/Directory.Build.props") "<Project>" "<Project>`n  <!-- selftest -->"
    $run = Invoke-Workspace @("-Sync")
    Assert-Run $run "props 变化" -Contains @($restoreMark, "跳过 npm ci") -Excludes @($npmMark)
    Write-Pass "*.props 变化：重新 restore，不 npm ci"

    # 更换候选包源
    & pwsh -NoProfile -File (Join-Path $repoRoot "framework/build/pack-local-feed.ps1") -OutputPath $feedB | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "打包第二个候选包源失败" }
    $versionA = (Read-CandidateManifest $feedA).CandidateVersion
    $versionB = (Read-CandidateManifest $feedB).CandidateVersion
    Assert-True ($versionA -cne $versionB) "两个候选包源的版本相同：$versionA"
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "更换候选包源" -Contains @("候选包版本变化：$versionA → $versionB", $restoreMark, "候选包来源断言通过", "跳过 npm ci")
    $rsp = [IO.File]::ReadAllText((Join-Path $workspace "Directory.Build.rsp"))
    Assert-True ($rsp.Contains("-p:LeistdFrameworkVersion=$versionB") -and $rsp.Contains("-restoreProperty:LeistdFrameworkVersion=$versionB") -and -not $rsp.Contains($versionA)) "rsp 没有换成新候选版本：$rsp"
    $staleVersions = @(Get-ChildItem -LiteralPath (Join-Path $workspace "nuget-packages") -Directory -Filter 'leistd.*' | Get-ChildItem -Directory | Where-Object Name -ine $versionB)
    Assert-True ($staleVersions.Count -eq 0) "解包目录里残留旧候选版本：$($staleVersions.FullName -join ', ')"
    Write-Pass "更换候选包源：重新 restore，rsp 与解包目录只剩新候选版本，来源断言通过"

    # 模板删除文件
    $deleted = "docs/standards/testing.md"
    Remove-Item -LiteralPath (Join-Path $templateCopy $deleted) -Force
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "模板删除文件" -Contains @("删除 1 个文件")
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $project $deleted))) "模板里删除的 $deleted 仍在工作区"
    $state = Get-Content -LiteralPath (Join-Path $workspace "workspace.json") -Raw | ConvertFrom-Json -AsHashtable
    Assert-True (-not $state.Files.Contains($deleted)) "同步记录里仍有已删除的 $deleted"
    Write-Pass "模板删除文件：工作区与同步记录里同样删除"

    # 只改大小写的重命名：工作区文件随之改名，记录里只有新名字
    $oldName = "docs/standards/api.md"; $newName = "docs/standards/API.md"
    Assert-True (Test-Path -LiteralPath (Join-Path $templateCopy $oldName)) "夹具失效：模板副本里没有 $oldName"
    Move-Item -LiteralPath (Join-Path $templateCopy $oldName) -Destination (Join-Path $templateCopy "$oldName.tmp")
    Move-Item -LiteralPath (Join-Path $templateCopy "$oldName.tmp") -Destination (Join-Path $templateCopy $newName)
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "只改大小写的重命名" -Contains @("写入 1 个文件，删除 1 个文件")
    $names = @(Get-ChildItem -LiteralPath (Join-Path $project "docs/standards") -File | ForEach-Object Name)
    Assert-True (($names -ccontains "API.md") -and -not ($names -ccontains "api.md")) "工作区里的文件名没有随模板改成 API.md：$($names -join ', ')"
    $files = (Get-Content -LiteralPath (Join-Path $workspace "workspace.json") -Raw | ConvertFrom-Json).Files.PSObject.Properties.Name
    Assert-True (($files -ccontains $newName) -and -not ($files -ccontains $oldName)) "同步记录没有换成新文件名"
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "改名后的无改动同步" -Contains @("写入 0 个文件，删除 0 个文件")
    Write-Pass "只改大小写的重命名：先删旧名再写新名，工作区与记录都换成新文件名"

    # 模板自带与保留目录同名的目录（.cache/；bin/ 与 obj/ 被模板引擎默认排除，不会生成）照常镜像；模板删掉后工作区同样删除
    $ownedBin = "docs/.cache/note.md"
    New-Item -ItemType Directory -Path (Join-Path $templateCopy "docs/.cache") -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $templateCopy $ownedBin), "# note`n", $utf8)
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "模板自带 .cache 目录" -Contains @("写入 1 个文件")
    Assert-True (Test-Path -LiteralPath (Join-Path $project $ownedBin)) "模板里 docs/.cache/ 下的文件没有镜像进工作区"
    Remove-Item -LiteralPath (Join-Path $templateCopy "docs/.cache") -Recurse -Force
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "删除模板自带 .cache 目录" -Contains @("删除 1 个文件")
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $project "docs/.cache"))) "模板删掉的 docs/.cache/ 仍在工作区"
    Write-Pass "模板自带的 .cache/ 目录照常镜像与删除，保留目录只对工作区自己的产物生效"

    # 未同步的修改：改动与新增都要拦下；构建目录与项目 .gitignore 忽略的本地文件不算
    $modified = "backend/README.md"
    $modifiedPath = Join-Path $project $modified
    $synced = [IO.File]::ReadAllText($modifiedPath)
    [IO.File]::AppendAllText($modifiedPath, "`n工作区里的手工修改`n", $utf8)
    $added = "frontend/src/app/selftest-added.ts"
    [IO.File]::WriteAllText((Join-Path $project $added), "export const added = 1;`n", $utf8)
    $buildOutput = Join-Path $project "$unitTests/bin/selftest-keep.txt"
    [IO.File]::WriteAllText($buildOutput, "keep`n", $utf8)
    # 生成项目 .gitignore 忽略的本地文件：根 .gitignore 的 .env、backend/.gitignore 的 DataProtection-Keys/
    $localFiles = @(".env", "backend/src/$projectName.Api/DataProtection-Keys/key-selftest.xml")
    foreach ($relative in $localFiles) {
        $path = Join-Path $project $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, "local`n", $utf8)
    }
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "未同步的修改" -ExitCode 1 -Contains @("2 处未同步的修改", "已修改  $modified", "新增    $added", "-Discard") -Excludes @("selftest-keep.txt", ".env", "DataProtection-Keys", "镜像完成")
    Assert-True ([IO.File]::ReadAllText($modifiedPath).Contains("工作区里的手工修改")) "未同步的修改被覆盖了"
    $run = Invoke-Workspace @("-Sync", "-Discard") $feedB
    Assert-Run $run "-Discard" -Contains @("丢弃 2 处未同步的修改", "工作区就绪")
    Assert-True ([IO.File]::ReadAllText($modifiedPath) -ceq $synced) "-Discard 没有还原 $modified"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $project $added))) "-Discard 没有删除新增的 $added"
    Assert-True (Test-Path -LiteralPath $buildOutput) "构建目录里的文件被删了"
    foreach ($relative in $localFiles) { Assert-True (Test-Path -LiteralPath (Join-Path $project $relative)) "-Discard 删掉了被项目 .gitignore 忽略的 $relative" }
    $run = Invoke-Workspace @("-Sync") $feedB
    Assert-Run $run "只有被忽略的本地文件" -Contains @("写入 0 个文件，删除 0 个文件") -Excludes @("未同步的修改")
    Write-Pass "未同步的修改：-Sync 停止并列出改动与新增、不覆盖；-Discard 才覆盖；构建目录与 .gitignore 忽略的本地文件（.env、DataProtection-Keys/）不算修改也不删除"

    if ($Timing) {
        $null = Invoke-Workspace @("-Sync") $feedB
        $backendSource = Join-Path $templateCopy "backend/src/CompanyName.ProjectName.Domain/Users/Policies/PasswordPolicy.cs"
        $frontendSource = Join-Path $templateCopy "frontend/src/app/features/settings/setting-choices.ts"
        $spec = "src/app/features/settings/setting-choices.spec.ts"
        # 预热：编译输出、测试宿主与 lint 缓存
        Push-Location $project
        try {
            & dotnet test $unitTests --filter "FullyQualifiedName~UserManagementRulesTests" | Out-Null
            Push-Location frontend
            try { & npx ng test --watch=false --include $spec | Out-Null; & npm run lint | Out-Null } finally { Pop-Location }
        }
        finally { Pop-Location }
        $results = @{ backend = @(); frontend = @() }
        for ($round = 1; $round -le 5; $round++) {
            [IO.File]::AppendAllText($backendSource, "// timing $round`n", $utf8)
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $sync = Invoke-Workspace @("-Sync") $feedB
            Assert-Run $sync "计时同步（后端 $round）"
            Push-Location $project
            try { $test = (& dotnet test $unitTests --filter "FullyQualifiedName~UserManagementRulesTests" 2>&1 | Out-String); $testExit = $LASTEXITCODE } finally { Pop-Location }
            Assert-True ($testExit -eq 0) "计时后端测试失败：`n$test"
            $results.backend += $watch.Elapsed.TotalSeconds

            [IO.File]::AppendAllText($frontendSource, "// timing $round`n", $utf8)
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $sync = Invoke-Workspace @("-Sync") $feedB
            Assert-Run $sync "计时同步（前端 $round）"
            Push-Location (Join-Path $project "frontend")
            try {
                $test = (& npx ng test --watch=false --include $spec 2>&1 | Out-String); $testExit = $LASTEXITCODE
                $lint = (& npm run lint 2>&1 | Out-String); $lintExit = $LASTEXITCODE
            }
            finally { Pop-Location }
            Assert-True ($testExit -eq 0 -and $lintExit -eq 0) "计时前端检查失败：`n$test`n$lint"
            $results.frontend += $watch.Elapsed.TotalSeconds
        }
        foreach ($kind in @('backend', 'frontend')) {
            Write-Host ("计时 {0}：{1}；中位 {2:N1}s" -f $kind, (($results[$kind] | ForEach-Object { "{0:N1}s" -f $_ }) -join ' '), (Get-Median $results[$kind])) -ForegroundColor Cyan
        }
    }
}
catch {
    Write-Host "自检失败，现场保留在 $root" -ForegroundColor Red
    throw
}

Remove-Item -LiteralPath $root -Recurse -Force
Write-Host "模板开发工作区自检通过（$passed 项）。" -ForegroundColor Green
