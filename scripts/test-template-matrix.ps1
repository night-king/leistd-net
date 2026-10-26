#!/usr/bin/env pwsh
param(
    # 不给就跑全量：全量清单是 $AllScenarios（见下），不写死在这里，
    # 否则「加了场景定义却忘了加进清单」会让新场景静默不跑——下面有断言兜住
    [string[]]$Scenarios = @(),
    # 档位：full 为全集，core 为 PR 代表场景（归属见 template-matrix-scenarios.ps1）。不能与人工场景选择混用；
    # 都不给时等同 full。
    [ValidateSet("core", "full")]
    [string]$Tier,
    # 人工入口使用逻辑组；传入计划时必须使用该计划的实际执行组ID。
    [string]$Slice,
    # 独立预期计划：绑定本候选/档位、选定场景与阶段；默认入口仍完整执行。
    [string]$ValidationPlanPath,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("chromiumHeadless", "chromium")]
    [string]$FrontendBrowser = "chromiumHeadless",
    [switch]$SkipPack,
    [string]$LocalFeedPath,
    [switch]$SkipFrontend,
    [switch]$SkipRuntime,
    # 仅 CI 的同候选 check-all + 必过汇总接替源码预检；独立入口默认仍执行。
    [switch]$SkipSourcePreflight,
    # 只为选中的场景构建并运行容器入口；Dockerfile/Compose 变化时使用，不随每个普通代码修改运行。
    [string[]]$ContainerSmokeScenarios = @(),
    # 在登记的容器场景（若在本片）上执行容器检查；CI 用它，不在 workflow 重抄场景名。
    [switch]$ContainerSmoke,
    # 只生成并执行形态断言与文档检查（文件集合、入口指针、条件裁剪、文档引用与章节锚点）；
    # 不打包、不 restore/构建/测试、不跑前端与运行时，也不产出回执。用于改模板文档或 Skill 时的快速验证。
    [switch]$GenerateOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "generated-project-checks.ps1")
. (Join-Path $PSScriptRoot "docker-image.ps1")
. (Join-Path $PSScriptRoot "test-count-summary.ps1")
. (Join-Path $repoRoot "framework/build/candidate-feed.ps1")
$tempRoot = Join-Path $repoRoot ".tmp"
$tempPrefix = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

# 每次运行独立的工作根：generated/hive/feed 都放在唯一 run 目录下，使上一轮残留的被锁目录
# （MSBuild 复用节点仍持有 *.Tasks.dll 句柄等）永不阻断本轮，也让多个 AI/终端可并行执行——
# 每个 run 自包含，互不写对方目录。用 PID + 高精度时间戳组合成 run id（脚本运行时确定，天然唯一）。
$runId = "{0}-{1}" -f $PID, (Get-Date -Format "yyyyMMddHHmmssfff")
$runRoot = Join-Path $tempRoot (Join-Path "runs" $runId)

# 禁用 MSBuild 节点复用：worker 节点默认 /nodeReuse:true，进程退出后仍常驻并持有生成目录/包缓存的文件句柄，
# 导致后续清理失败。MSBUILDDISABLENODEREUSE=1 才是关闭 node reuse 的正确开关（DOTNET_CLI_USE_MSBUILD_SERVER
# 关的是另一个「MSBuild Server」特性，不影响 node reuse）。
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# globalPackagesFolder 必须也按 run 隔离：共享解包目录并定点清理会在并发 build 期间抽走 DLL，
# restore 后的候选包来源断言也只对本 run 解出的包有意义。每个 run 独立解包，NuGet HTTP 缓存仍可复用下载内容。
$packagesRoot = Join-Path $runRoot "nuget-cache"
# 本地 Leistd 包源位置随模式而定：
#  - 正常模式（本轮 pack）：per-run 独立目录，两个并行 run 各 pack 各的，杜绝共享目录重置竞争（发现#1）。
#  - -SkipPack：消费预先 pack 到共享 .tmp/local-feed 的包（CI 先 `pack-local-feed.ps1` 再 -SkipPack），
#    只读复用、并发安全。
#  两种模式都读包源里的 candidate.json：生成项目经外层 Directory.Build.rsp 消费其中的候选版本。
$sharedFeedRoot = if ($LocalFeedPath) { [IO.Path]::GetFullPath($LocalFeedPath, $repoRoot) } else { Join-Path $tempRoot "local-feed" }
if ($LocalFeedPath -and -not $SkipPack) { throw "-LocalFeedPath requires -SkipPack." }
if ($LocalFeedPath) {
    if (-not $sharedFeedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "-LocalFeedPath must remain inside .tmp: $sharedFeedRoot"
    }
}
$feedRoot = if ($SkipPack) { $sharedFeedRoot } else { Join-Path $runRoot "local-feed" }
$generatedRoot = Join-Path $runRoot "generated-template"
$hiveRoot = Join-Path $runRoot "template-hive"
$nugetConfigPath = Join-Path $runRoot "template-matrix.NuGet.Config"
$lockFile = Join-Path $runRoot ".run.lock"            # 活动锁：清理旧 run 时据此/据修改时间判活，绝不删正在运行的 run
$staleRunAgeHours = 2                                  # 超过此时长且非本 run 的目录才视为陈旧、可清理
$templateRoot = Join-Path $repoRoot "template"
$nugetOrg = "https://api.nuget.org/v3/index.json"

function Assert-TempPath([string]$Path) {
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside .tmp: $resolvedPath"
    }
}

function Reset-Directory([string]$Path) {
    Assert-TempPath $Path
    if (Test-Path -LiteralPath $Path) {
        # Windows 下 node/dotnet 残留句柄常导致 "目录不是空的"/访问被拒——对 IO/权限异常带退避重试，
        # 避免在打包/构建/测试前就阻断整个矩阵；最终仍失败时输出残留路径便于排查。
        $maxAttempts = 5
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            try {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
                break
            }
            catch [System.IO.IOException], [System.UnauthorizedAccessException] {
                if ($attempt -eq $maxAttempts) {
                    Write-Warning "无法清理目录（$maxAttempts 次重试后仍失败）: $Path"
                    throw
                }
                Start-Sleep -Milliseconds (200 * $attempt)
            }
        }
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Invoke-SourcePreflight([switch]$Skip) {
    if ($Skip) {
        if ($env:GITHUB_ACTIONS -cne 'true') { throw '-SkipSourcePreflight 仅用于具有同候选静态作业与必过汇总的 GitHub CI。' }
        # 只核对实际总入口清单，成功责任仍由 CI 汇总对 docs-sync 的结果检查承担。
        $listing = @(& pwsh -NoProfile -File (Join-Path $repoRoot 'scripts/check-all.ps1') -List)
        if ($LASTEXITCODE -ne 0) { throw '无法读取源码预检接替入口清单' }
        foreach ($required in @('check-template-symbols.ps1', 'check-using-guards.py', 'check-async-boundaries.py')) {
            if (-not ($listing -cmatch ('\s' + [regex]::Escape("scripts/$required") + '$'))) {
                throw "check-all 缺少源码预检接替入口：$required"
            }
        }
        Write-Host '源码预检由同候选 CI 静态作业承担，质量汇总必须核对其成功。'
        return
    }

    # 模板引擎不能可靠诊断悬空符号、指令字面形式与恒真嵌套，先检查源码再准备生成。
    Invoke-External 'pwsh' @('-File', (Join-Path $repoRoot 'scripts/check-template-symbols.ps1'))
    $pythonCmd = Get-Python3Command '运行 Python 静态闸门'

    # using/import 守卫在全部符号取值上求值，严于登记的生成场景。
    Invoke-External $pythonCmd @((Join-Path $repoRoot 'scripts/check-using-guards.py'))
    # 动态连接解析路径不能包含 sync-over-async。
    Invoke-External $pythonCmd @((Join-Path $repoRoot 'scripts/check-async-boundaries.py'))
}

# 与 Invoke-External 同型，但显式关闭子进程的 stdin。
# 用于那些会无条件发问、又没有非交互开关的 CLI：拿到 EOF 即取默认值，
# 从而让脚本在交互终端下也保持无人值守。
function Invoke-ExternalWithClosedInput([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    Write-Host "> $Command $($Arguments -join ' ')  (stdin closed)" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $null | & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

# 与 Invoke-External 同形，但把标准输出交回调用方（用于要读取命令结果的自检）。
function Invoke-ExternalCapture([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $output = & $Command @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ("Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')`n" +
                ($output -join "`n"))
        }
        return ($output | ForEach-Object { [string]$_ })
    }
    finally {
        Pop-Location
    }
}

# 与 Invoke-External 同形，输出照常逐行显示，同时从 Vitest 汇总行读出通过的前端单测用例数，
# 传 -ExpectE2E 时另从 Playwright 汇总行读出最终通过的端到端用例数（verify 的 frontend-e2e 步骤，含重试后通过的 flaky）。
# 判据见 test-count-summary.ps1 的 Get-BrowserTestCounts：退出码非零先失败；回执记录这两个数，汇总要求对应阶段为 pass 时大于 0，
# 一次没有执行任何用例的运行也会退出 0。浏览器由调用方保证是 Chromium（逐阶段传 --browsers，
# verify 路径核对 angular.json）；非交互输出里 Vitest 不逐文件打印浏览器实例名，不能从输出判定浏览器。
function Invoke-ExternalCountingBrowserTests([string]$Command, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot, [switch]$ExpectE2E) {
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        $lines = [Collections.Generic.List[string]]::new()
        & $Command @Arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            $lines.Add($line)
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
        $counts = Get-BrowserTestCounts $lines.ToArray() $LASTEXITCODE $ExpectE2E.IsPresent
        # 重试后才通过的端到端用例不阻断，但在日志与 GitHub 运行摘要里标出，报告与 trace 在本片的 e2e-report 产物里
        if ($counts.Flaky -gt 0) {
            $message = "$($counts.Flaky) E2E test(s) passed only after retries (flaky); see the Playwright report."
            Write-Host "⚠️ $message" -ForegroundColor Yellow
            if ($env:GITHUB_ACTIONS) { Write-Host "::warning title=Flaky E2E::$message" }
        }
        return $counts
    }
    finally {
        Pop-Location
    }
}

# verify 的前端单测不传 --browsers，浏览器来自生成项目 angular.json 的 test 目标：必须只含 Chromium 实例
function Assert-ChromiumTestBrowsers([string]$FrontendRoot) {
    $angular = Get-Content -LiteralPath (Join-Path $FrontendRoot 'angular.json') -Raw | ConvertFrom-Json -AsHashtable
    $browsers = @($angular.projects.Values | ForEach-Object { $_.architect.test.options.browsers })
    if ($browsers.Count -eq 0 -or @($browsers | Where-Object { $_ -cnotin @('chromiumHeadless', 'chromium') }).Count -gt 0) {
        throw "Frontend unit tests must run in Chromium; angular.json test browsers: $($browsers -join ', ')."
    }
}

# 随模板分发的 i18n 闸门与本地化同进退（template.json 的 !IncludeLocalization 排除项），
# 并且必须在生成产物上跑通：它按项目相对路径定位，模板源码上通过不代表生成后通过。
function Assert-I18nGate([string]$ProjectRoot) {
    $gate = Join-Path $ProjectRoot "scripts/check-i18n.py"
    $hasLocalization = @(Get-ChildItem -Path (Join-Path $ProjectRoot "backend/src/*.Api/Resources") -Directory -ErrorAction SilentlyContinue).Count -gt 0
    if ((Test-Path -LiteralPath $gate) -ne $hasLocalization) {
        throw "scripts/check-i18n.py must ship exactly when localization is enabled (localization: $hasLocalization)"
    }
    if ($hasLocalization) {
        Invoke-External (Get-Python3Command '运行生成项目的 i18n 闸门') @($gate) $ProjectRoot
    }
}

# 错误码闸门不随任何参数裁剪，每个生成项目都必须带上并在产物上跑通：
# 关闭本地化或邮件时，被裁掉的抛出处会让对应映射或常量变成死码，只有生成产物上看得见。
function Assert-ErrorCodeGate([string]$ProjectRoot) {
    $gate = Join-Path $ProjectRoot "scripts/check-error-codes.py"
    if (-not (Test-Path -LiteralPath $gate)) {
        throw "scripts/check-error-codes.py must ship with every generated project"
    }
    Invoke-External (Get-Python3Command '运行生成项目的错误码闸门') @($gate) $ProjectRoot
}

# 测试诊断闸门随每个生成项目下发；以 -I 隔离模式在产物上运行（不加入脚本目录与环境里的 import 路径），
# 证明它只靠标准库、不依赖本仓库其他脚本。verify 之外的逐阶段路径也由这里执行同一判据。
function Assert-TestDiagnosticsGate([string]$ProjectRoot) {
    $gate = Join-Path $ProjectRoot "scripts/check-test-diagnostics.py"
    if (-not (Test-Path -LiteralPath $gate)) {
        throw "scripts/check-test-diagnostics.py must ship with every generated project"
    }
    Invoke-External (Get-Python3Command '运行生成项目的测试诊断闸门') @('-I', $gate) $ProjectRoot
}

function Assert-GeneratedProject([string]$ProjectRoot) {
    Assert-GeneratedDocumentation $ProjectRoot
    $textFiles = Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|node_modules|\.git)[\\/]' -and
        ($_.Extension -in @(".cs", ".csproj", ".json", ".md", ".ts", ".html", ".scss", ".yml", ".yaml", ".props", ".targets", ".sln") -or
         $_.Name -in @("Dockerfile", ".gitignore", ".editorconfig"))
    }
    Assert-GeneratedText $textFiles
    $applicationProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "backend/src") -Filter "*.Application.csproj" -Recurse | Select-Object -First 1
    $apiProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "backend/src") -Filter "*.Api.csproj" -Recurse | Select-Object -First 1
    if ((Get-Content -LiteralPath $applicationProject.FullName -Raw).Contains("Infrastructure.csproj")) {
        throw "Application must not reference Infrastructure: $($applicationProject.FullName)"
    }
    if (-not (Get-Content -LiteralPath $apiProject.FullName -Raw).Contains("Infrastructure.csproj")) {
        throw "Api composition root must reference Infrastructure: $($apiProject.FullName)"
    }

    $programText = Get-Content -LiteralPath (Join-Path $apiProject.DirectoryName "Program.cs") -Raw
    if ($programText.Contains("MigrateAsync(") -or $programText.Contains("EnsureCreatedAsync(")) {
        throw "Generated API must not mutate database schema during startup."
    }

    Assert-I18nGate $ProjectRoot
    Assert-ErrorCodeGate $ProjectRoot
    Assert-TestDiagnosticsGate $ProjectRoot
}

# 本地化产物与生成源码一一对应，递归核对 scope 文件都已由 postbuild 展平。

function Assert-OptimizedTranslations([string]$FrontendRoot) {
    $source = Join-Path $FrontendRoot "public/i18n"
    if (-not (Test-Path $source)) { return }
    $package = Get-Content -LiteralPath (Join-Path $FrontendRoot "package.json") -Raw | ConvertFrom-Json
    $output = Join-Path $FrontendRoot "dist/$($package.name)/browser/i18n"
    $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.json')
    $outputFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.json')
    if ($sourceFiles.Count -ne $outputFiles.Count) { throw "Translation file count changed during production build." }
    function Get-TranslationKeys($Node, [string]$Prefix = '') {
        foreach ($property in $Node.PSObject.Properties) {
            $key = if ($Prefix) { "$Prefix.$($property.Name)" } else { $property.Name }
            if ($property.Value -is [pscustomobject]) { Get-TranslationKeys $property.Value $key }
            else { $key }
        }
    }
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $built = Get-Content -LiteralPath (Join-Path $output $relative) -Raw | ConvertFrom-Json
        if (@($built.PSObject.Properties | Where-Object { $_.Value -isnot [string] }).Count -gt 0) {
            throw "Translation file was not flattened: $relative"
        }
        $original = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $expected = @(Get-TranslationKeys $original | Sort-Object)
        $actual = @($built.PSObject.Properties.Name | Sort-Object)
        if (Compare-Object $expected $actual -CaseSensitive) { throw "Translation keys changed during optimization: $relative" }
    }
    Write-Host "Validated $($sourceFiles.Count) flattened global/scope translation files."
}

# 磁盘上的每个 spec 文件都必须被测试发现。
#
# 这一条防的是"恒绿"：`_mock/**/*.spec.ts` 从加进模板那天起就没被执行过
# （builder 的 findTests 以 sourceRoot 为 glob 工作目录，`_mock/**` 被解析成不存在的 `src/_mock/**`），
# 而测试照样全绿——比一条失败的用例更坏，因为它让人以为那一层有覆盖。Karma 时代同样如此，
# 不是 Vitest 迁移引入的。
#
# 用 builder 自己的 --list-tests：判据与真实运行共用同一套发现逻辑，不另写一份 glob 去猜。
# 判据是**路径集合**而不是文件数：数量相等而集合不同是可能的（同时改名与挪目录）。
# 只报缺失项——报告里出现磁盘上没有的文件属于工具问题，不是模板要守的约定。
function Assert-EveryFrontendSpecDiscovered([string]$FrontendRoot) {
    $onDisk = @(
        Get-ChildItem -LiteralPath $FrontendRoot -Recurse -File -Filter "*.spec.ts" |
            Where-Object { $_.FullName -notmatch "[\\/]node_modules[\\/]" } |
            ForEach-Object { [IO.Path]::GetRelativePath($FrontendRoot, $_.FullName).Replace('\', '/') }
    )
    if ($onDisk.Count -eq 0) {
        throw "No *.spec.ts found under $FrontendRoot; the self-check would pass vacuously."
    }

    $listed = Invoke-ExternalCapture "npm" @("test", "--", "--list-tests") $FrontendRoot
    $discovered = @(
        $listed | ForEach-Object { $_.Trim() } | Where-Object { $_ -like "*.spec.ts" } |
            ForEach-Object {
                $path = $_
                if ([IO.Path]::IsPathRooted($path)) {
                    [IO.Path]::GetRelativePath($FrontendRoot, $path).Replace('\', '/')
                } else {
                    $path.Replace('\', '/')
                }
            }
    )

    $discoveredSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$discovered, [StringComparer]::Ordinal)
    $missing = @($onDisk | Where-Object { -not $discoveredSet.Contains($_) })
    if ($missing.Count -gt 0) {
        throw ("These spec files exist on disk but are not discovered by the test builder. The `include` " +
            "patterns in angular.json resolve relative to sourceRoot, so paths outside it need `../`:`n  " +
            ($missing -join "`n  "))
    }

    Write-Host ("  每个 spec 都会被发现（{0} 个）。" -f $onDisk.Count)
}


# 生成项目的完整回归入口 scripts/verify.ps1：默认模式的步骤清单须与矩阵按产物判定的阶段一致——
# 随包闸门按脚本是否随包、测试项目按矩阵的发现范围（backend 下全部 *Tests.csproj，单元测试在前）、前端按目录是否存在。
# -Fast 须恰好是默认模式去掉单元测试以外的测试项目、前端安装、单测、构建与端到端后的子序列。
# 前端生产依赖审计不在 verify 里（生成项目的 CI 在锁文件变化时执行），由矩阵按实际锁文件各审一次。
function Assert-VerifySteps([string]$ProjectRoot) {
    $listing = @(Invoke-ExternalCapture 'pwsh' @('-NoProfile', '-File', (Join-Path $ProjectRoot 'scripts/verify.ps1'), '-List') $ProjectRoot)
    $rows = @($listing | Where-Object { $_.Trim() } | ForEach-Object { , ($_ -split "`t") })
    $unknownModes = @($rows | Where-Object { $_[0] -cnotin 'full', 'fast' } | ForEach-Object { $_[0] })
    if ($unknownModes) { throw "scripts/verify.ps1 -List has rows outside the full/fast modes: $($unknownModes -join ', ')" }
    $actual = @($rows | Where-Object { $_[0] -ceq 'full' } | ForEach-Object { $_[1] })
    $actualFast = @($rows | Where-Object { $_[0] -ceq 'fast' } | ForEach-Object { $_[1] })
    $expected = [Collections.Generic.List[string]]::new()
    $expected.Add('check-error-codes')
    foreach ($gate in @('check-i18n', 'check-operation-action-i18n')) {
        if (Test-Path -LiteralPath (Join-Path $ProjectRoot "scripts/$gate.py")) { $expected.Add($gate) }
    }
    $expected.Add('check-test-diagnostics')
    $expected.Add('backend-restore')
    $expected.Add('backend-build')
    $expected.Add('backend-format')
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'backend') -Filter '*Tests.csproj' -Recurse |
        Sort-Object @{ Expression = { $_.BaseName -notlike '*.UnitTests' } }, BaseName |
        ForEach-Object { $expected.Add("backend-test:$($_.BaseName)") }
    if (Test-Path -LiteralPath (Join-Path $ProjectRoot 'frontend')) {
        foreach ($stage in @('frontend-install', 'frontend-lint', 'frontend-test', 'frontend-build')) { $expected.Add($stage) }
    }
    # 浏览器端到端只随本地身份形态（有登录界面与可预置的账号）下发；纯 API 与 Resource 前端都没有
    $hasE2e = Test-Path -LiteralPath (Join-Path $ProjectRoot 'frontend/playwright.config.ts')
    $localIdentity = @(Get-ChildItem -Path (Join-Path $ProjectRoot 'backend/src/*.Api/Controllers/AuthController.cs') -ErrorAction SilentlyContinue).Count -gt 0
    if ($hasE2e -ne $localIdentity) {
        throw "frontend/playwright.config.ts must ship exactly with local identity (local identity: $localIdentity)."
    }
    if ($hasE2e) { $expected.Add('frontend-e2e') }
    if (($actual -join '|') -cne ($expected -join '|')) {
        throw "scripts/verify.ps1 steps differ from the matrix stages.`n  verify: $($actual -join ', ')`n  matrix: $($expected -join ', ')"
    }
    $fullOnly = @('frontend-install', 'frontend-test', 'frontend-build', 'frontend-e2e')
    $expectedFast = @($expected | Where-Object { $_ -notin $fullOnly -and ($_ -notlike 'backend-test:*' -or $_ -like 'backend-test:*.UnitTests') })
    if (($actualFast -join '|') -cne ($expectedFast -join '|')) {
        throw "scripts/verify.ps1 -Fast steps differ from the expected subset.`n  verify: $($actualFast -join ', ')`n  expected: $($expectedFast -join ', ')"
    }
}

function Assert-ScenarioShape([string]$ProjectRoot, [string]$ProjectName, [hashtable]$Definition) {
    foreach ($relativePath in @($CommonScenarioShape.Present) + @($Definition.Present)) {
        $expandedPath = $relativePath.Replace('{name}', $ProjectName)
        if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot $expandedPath))) {
            throw "Scenario '$($Definition.Name)' is missing expected path: $expandedPath"
        }
    }
    foreach ($relativePath in $Definition.Absent) {
        $expandedPath = $relativePath.Replace('{name}', $ProjectName)
        if (Test-Path -LiteralPath (Join-Path $ProjectRoot $expandedPath)) {
            throw "Scenario '$($Definition.Name)' contains path that should be removed: $expandedPath"
        }
    }

    $readme = Get-Content -LiteralPath (Join-Path $ProjectRoot "README.md") -Raw -Encoding UTF8
    foreach ($expectedText in $Definition.ReadmeContains) {
        if (-not $readme.Contains($expectedText)) {
            throw "Scenario '$($Definition.Name)' README is missing expected text: $expectedText"
        }
    }
    foreach ($unexpectedText in $Definition.ReadmeExcludes) {
        if ($readme.Contains($unexpectedText)) {
            throw "Scenario '$($Definition.Name)' README contains disabled capability: $unexpectedText"
        }
    }

    # RequiredTokens：指定文件必须含指定片段。用于断言"剪裁后留下的是对的那一份"，
    # 与 ForbiddenTokens（不该留的没留下）互补——只查缺失会漏掉"两份都在"的情形
    foreach ($requiredTokens in @($CommonScenarioShape.RequiredTokens, $Definition.RequiredTokens)) {
        if (-not $requiredTokens) { continue }
        foreach ($entry in $requiredTokens.GetEnumerator()) {
            $relativePath = $entry.Key.Replace("{name}", $ProjectName)
            $filePath = Join-Path $ProjectRoot $relativePath
            if (-not (Test-Path -LiteralPath $filePath)) {
                throw "Scenario '$($Definition.Name)' is missing required file: $relativePath"
            }

            $content = Get-Content -LiteralPath $filePath -Raw -Encoding UTF8
            foreach ($token in @($entry.Value | ForEach-Object { $_.Replace("{name}", $ProjectName) })) {
                if (-not ($content -and $content.Contains($token))) {
                    throw "Scenario '$($Definition.Name)' file $relativePath is missing required token '$token'"
                }
            }
        }
    }

    if ($Definition.ForbiddenTokens) {
        $sourceRoots = @("backend/src", "frontend/src", "frontend/_mock") |
            ForEach-Object { Join-Path $ProjectRoot $_ } |
            Where-Object { Test-Path -LiteralPath $_ }

        $sourceFiles = Get-ChildItem -LiteralPath $sourceRoots -Recurse -File -Include *.cs, *.ts -ErrorAction SilentlyContinue
        foreach ($file in $sourceFiles) {
            $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
            foreach ($token in $Definition.ForbiddenTokens) {
                if ($content -and $content.Contains($token)) {
                    $relative = $file.FullName.Substring($ProjectRoot.Length).TrimStart('/', '\')
                    throw "Scenario '$($Definition.Name)' leaks trimmed contract token '$token' in $relative"
                }
            }
        }
    }

    if ($Definition.Frontend) {
        $mockInterceptorPath = Join-Path $ProjectRoot "frontend/_mock/core/interceptor.ts"
        $mockInterceptor = Get-Content -LiteralPath $mockInterceptorPath -Raw -Encoding UTF8
        foreach ($marker in @("MOCK_ROUTE_NOT_FOUND", "Mock Route Not Found", "startsWith('/api/')")) {
            if (-not $mockInterceptor.Contains($marker)) {
                throw "Scenario '$($Definition.Name)' Mock interceptor is missing fail-fast marker: $marker"
            }
        }
    }

    $notificationServicePath = Join-Path $ProjectRoot "frontend/src/app/core/services/notification-service.ts"
    if (Test-Path -LiteralPath $notificationServicePath) {
        $notificationService = Get-Content -LiteralPath $notificationServicePath -Raw -Encoding UTF8
        foreach ($marker in @("await this.signalR.connect()")) {
            if (-not $notificationService.Contains($marker)) {
                throw "Scenario '$($Definition.Name)' notification service is missing Mock isolation marker: $marker"
            }
        }
    }
    # Mock 建连隔离集中在共享连接服务，通知和业务实时均受同一守卫保护。
    $signalRPath = Join-Path $ProjectRoot 'frontend/src/app/core/services/signalr-service.ts'
    if (Test-Path -LiteralPath $signalRPath) {
        $signalR = Get-Content -LiteralPath $signalRPath -Raw -Encoding UTF8
        foreach ($marker in @('inject(MOCKED_URL)', 'this.isMockedUrl(SignalRService.hubPath)')) {
            if (-not $signalR.Contains($marker)) { throw "Scenario '$($Definition.Name)' shared connection lacks Mock isolation: $marker" }
        }
    }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

# 运行时冒烟走克隆后的真实路径：迁移入口建库，再启动 API。本次运行共用一个 PostgreSQL 容器、每个场景一个库；
# 容器按 run id 命名，结束时只删本次创建的这一个。口令随机生成，只在本机回环地址上监听。
$runtimeDatabaseContainer = "leistd-matrix-db-$runId"
$runtimeDatabaseImage = "postgres:18-alpine"
$runtimeDatabasePassword = [Guid]::NewGuid().ToString("N")
$runtimeDatabasePort = $null
$runtimeDatabaseStarted = $false

function Start-RuntimeDatabase {
    Initialize-DockerImage $runtimeDatabaseImage
    Write-Host "> docker run $runtimeDatabaseImage as $runtimeDatabaseContainer" -ForegroundColor DarkGray
    & docker run -d --name $runtimeDatabaseContainer -e "POSTGRES_PASSWORD=$runtimeDatabasePassword" -p "127.0.0.1::5432" `
        $runtimeDatabaseImage -c fsync=off -c synchronous_commit=off | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to start the runtime smoke database container." }
    # 容器已存在就必须由收尾删除，即使下面取端口或等待就绪失败
    $script:runtimeDatabaseStarted = $true
    $script:runtimeDatabasePort = [int](((& docker port $runtimeDatabaseContainer 5432) | Select-Object -First 1) -split ':')[-1]

    # 镜像初始化时先起一个临时实例再重启：pg_isready 会在中途短暂成功，紧接着连接被切断。
    # 以"就绪日志出现第二次"为准（Testcontainers 的同一判据）
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while (@((& docker logs $runtimeDatabaseContainer 2>&1) -match 'database system is ready to accept connections').Count -lt 2) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "The runtime smoke database did not become ready within 90 seconds." }
        Start-Sleep -Milliseconds 500
    }
}

function Stop-RuntimeDatabase {
    if ($script:runtimeDatabaseStarted) {
        & docker rm -fv $runtimeDatabaseContainer 2>$null | Out-Null
        $global:LASTEXITCODE = 0
    }
}

function New-RuntimeDatabase([string]$Name) {
    if (-not $script:runtimeDatabaseStarted) { Start-RuntimeDatabase }
    & docker exec $runtimeDatabaseContainer psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$Name`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to create runtime smoke database $Name." }
    return "Host=127.0.0.1;Port=$($script:runtimeDatabasePort);Database=$Name;Username=postgres;Password=$runtimeDatabasePassword"
}

function Invoke-PureApiContainerSmoke([string]$Scenario, [string]$ApiImage, [string]$MigratorImage) {
    $databaseName = 'container_' + ($Scenario -replace '[^a-z0-9]', '_')
    [void](New-RuntimeDatabase $databaseName)
    $network = "leistd-container-$runId"
    $apiContainer = "leistd-api-$runId"
    $port = Get-FreeTcpPort
    $connection = "Host=$runtimeDatabaseContainer;Port=5432;Database=$databaseName;Username=postgres;Password=$runtimeDatabasePassword"
    try {
        Invoke-External 'docker' @('network', 'create', $network)
        Invoke-External 'docker' @('network', 'connect', $network, $runtimeDatabaseContainer)
        Invoke-External 'docker' @('run', '--rm', '--network', $network,
            '-e', "ConnectionStrings__MigrationTarget=$connection", $MigratorImage, '--apply')
        Invoke-External 'docker' @('run', '--rm', '--entrypoint', '/bin/sh', $ApiImage, '-c',
            'test ! -d /app/wwwroot && ! command -v node')
        $apiArguments = @('run', '-d', '--name', $apiContainer, '--network', $network,
            '-p', "127.0.0.1:${port}:8080", '-e', 'ASPNETCORE_ENVIRONMENT=Development',
            '-e', "ConnectionStrings__Default=$connection", '-e', 'ConnectionStrings__Redis=',
            '-e', 'Authentication__Issuer=https://identity.matrix.test/')
        if ($scenarioMap[$Scenario].Arguments -contains 'Resource') {
            # Resource 回源 Identity 的机器身份与代表本人写偏好的交换目标在启动期校验；冒烟只探存活，不换令牌
            $apiArguments += @('-e', 'Leistd__ServiceAuth__ClientId=matrix-resource-worker',
                '-e', 'Leistd__ServiceAuth__ClientSecret=matrix-resource-worker-secret',
                '-e', 'Leistd__ServiceClients__Identity__TokenExchange__Audience=matrix-identity-api',
                '-e', 'Leistd__ServiceClients__Identity__TokenExchange__Scope=matrix-identity-api')
        }
        Invoke-External 'docker' ($apiArguments + @($ApiImage))
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
        $healthy = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $response = Invoke-WebRequest "http://127.0.0.1:$port/api/health/live" -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $healthy = $true; break }
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $healthy) { throw 'Pure API container did not start successfully.' }
        $root = Invoke-WebRequest "http://127.0.0.1:$port/" -SkipHttpErrorCheck
        if ($root.StatusCode -ne 404) { throw 'Pure API container unexpectedly serves a frontend.' }
        Write-Host 'Pure API container: real migration, API startup, liveness and absent SPA passed.'
    }
    catch {
        & docker logs $apiContainer 2>&1 | Out-Host
        throw
    }
    finally {
        & docker rm -fv $apiContainer 2>$null | Out-Null
        & docker network disconnect $network $runtimeDatabaseContainer 2>$null | Out-Null
        & docker network rm $network 2>$null | Out-Null
        $global:LASTEXITCODE = 0
    }
}

function Invoke-WithEnvironment([hashtable]$Variables, [scriptblock]$Action) {
    $previous = @{}
    foreach ($key in $Variables.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, [string]$Variables[$key])
    }
    try { & $Action }
    finally {
        foreach ($key in $Variables.Keys) {
            # 原本不存在的变量必须删除而不是置空：PowerShell 把 $null 传给 string 形参时会变成 ""，
            # 残留的空 DOTNET_ENVIRONMENT 会让随后启动的 API 不再是开发环境
            if ($null -eq $previous[$key]) {
                [Environment]::SetEnvironmentVariable($key, [NullString]::Value)
            }
            else {
                [Environment]::SetEnvironmentVariable($key, [string]$previous[$key])
            }
        }
    }
}

function Invoke-RuntimeSmoke([string]$ProjectRoot, [string]$Configuration, [string]$Scenario) {
    $apiProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "backend/src") -Filter "*.Api.csproj" -Recurse | Select-Object -First 1
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($apiProject.Name)
    $apiAssembly = Join-Path $apiProject.DirectoryName "bin/$Configuration/net10.0/$assemblyName.dll"
    if (-not (Test-Path -LiteralPath $apiAssembly)) {
        throw "Built API assembly was not found: $apiAssembly"
    }

    # 先按发布流程用本场景自己的迁移入口建库：API 启动时只校验、不施加迁移
    $connectionString = New-RuntimeDatabase ("smoke_" + ($Scenario -replace '[^a-z0-9]', '_'))
    $migratorProject = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "backend/src") -Filter "*.DbMigrator.csproj" -Recurse | Select-Object -First 1
    $migratorAssembly = Join-Path $migratorProject.DirectoryName "bin/$Configuration/net10.0/$([IO.Path]::GetFileNameWithoutExtension($migratorProject.Name)).dll"
    $keysPath = Join-Path $runRoot "smoke-keys/$Scenario"
    New-Item -ItemType Directory -Path $keysPath -Force | Out-Null
    $migratorEnvironment = @{
        DOTNET_ENVIRONMENT = 'Production'
        DataProtection__KeysPath = $keysPath
    }
    if ($scenarioMap[$Scenario].Arguments -contains 'Resource') {
        # Resource 的迁移作业默认向 Identity 枚举独立库租户；冒烟没有 Identity，按单一目标迁移（与首次建独立库同一入口）。
        # 远端存储的地址在组合期就校验，给一个不可达的地址
        $migratorEnvironment['ConnectionStrings__MigrationTarget'] = $connectionString
        $migratorEnvironment['Leistd__ServiceClients__Identity__BaseAddress'] = 'https://identity.matrix.test/'
    }
    else {
        # 持有控制库的形态用默认连接，连同控制面 schema 一起迁移
        $migratorEnvironment['ConnectionStrings__Default'] = $connectionString
    }
    Invoke-WithEnvironment $migratorEnvironment {
        Invoke-External "dotnet" @($migratorAssembly, "--apply")
    }

    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet -ErrorAction Stop).Source
    $startInfo.WorkingDirectory = $apiProject.DirectoryName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($apiAssembly)
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $startInfo.Environment['ASPNETCORE_URLS'] = $baseUrl
    $startInfo.Environment['ConnectionStrings__Default'] = $connectionString
    $startInfo.Environment['DataProtection__KeysPath'] = $keysPath
    $startInfo.Environment['OAuth__DisableHttpsRequirement'] = 'true'
    # 不注入管理员口令：新库首次启动要建管理员，口令取自生成项目的 appsettings.Development.json，
    # 这正是克隆后首次运行的路径
    $startInfo.Environment['Leistd__Security__VerificationCodes__Key'] = 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8='
    # Resource 形态的签发方在基线配置里刻意留空、组合期必填；其余形态不读这一项。
    # 冒烟只探存活，不回源，给一个不可达的地址即可
    $startInfo.Environment['Authentication__Issuer'] = 'https://identity.matrix.test/'
    $startInfo.Environment['Authentication__ClientId'] = 'matrix-resource'
    $startInfo.Environment['Authentication__ClientSecret'] = 'matrix-resource-secret'
    $startInfo.Environment['Leistd__ServiceClients__Identity__BaseAddress'] = 'https://identity.matrix.test/'
    # Resource 的开发配置声明了回源 Identity 的机器身份，启动校验要求凭据齐全；冒烟只探存活，不换令牌
    $startInfo.Environment['Leistd__ServiceAuth__ClientId'] = 'matrix-resource-worker'
    $startInfo.Environment['Leistd__ServiceAuth__ClientSecret'] = 'matrix-resource-worker-secret'
    # 代表本人写账户偏好的交换目标同样在启动期校验；冒烟不写偏好，给出 Identity API 的标识即可
    $startInfo.Environment['Leistd__ServiceClients__Identity__TokenExchange__Audience'] = 'matrix-identity-api'
    $startInfo.Environment['Leistd__ServiceClients__Identity__TokenExchange__Scope'] = 'matrix-identity-api'

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $failure = $null
    $stdout = ""
    $stderr = ""
    try {
        $started = $process.Start()
        if (-not $started) {
            throw "Failed to start generated API."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        # 健康探测窗口。生成项目单独冷启动通常 ~10s 即 200；但矩阵在一次 pack+generate+restore+build 突发之后
        # 立即启动首个场景，CPU/磁盘/句柄仍处饱和，冷启动可显著变慢——旧的 60s 会偶发假超时（非启动故障，
        # 单独启动即健康）。放宽到 150s 覆盖满载冷启动尾延迟；可用 $env:MATRIX_HEALTH_TIMEOUT_SEC 覆盖。
        $healthTimeoutSec = 150
        if ($env:MATRIX_HEALTH_TIMEOUT_SEC) { $healthTimeoutSec = [int]$env:MATRIX_HEALTH_TIMEOUT_SEC }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($healthTimeoutSec)
        $healthy = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                $failure = "Generated API exited before becoming healthy with code $($process.ExitCode)."
                break
            }
            try {
                $response = Invoke-WebRequest -Uri "$baseUrl/api/health/live" -TimeoutSec 2 -ErrorAction Stop
                if ($response.StatusCode -eq 200) {
                    $healthy = $true
                    break
                }
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $healthy -and -not $failure) {
            $failure = "Generated API did not become healthy within $healthTimeoutSec seconds."
        }
    }
    catch {
        $failure = $_.Exception.Message
    }
    finally {
        if ($started) {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
            $process.WaitForExit()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
        }
        $process.Dispose()
    }

    if ($failure) {
        $combinedLog = ($stdout + "`n" + $stderr).Trim()
        if ($combinedLog.Length -gt 4000) {
            $combinedLog = $combinedLog.Substring($combinedLog.Length - 4000)
        }
        throw "$failure`nGenerated API log:`n$combinedLog"
    }
}

. (Join-Path $PSScriptRoot "template-matrix-scenarios.ps1")
. (Join-Path $PSScriptRoot "quality-validation-plan.ps1")
. (Join-Path $PSScriptRoot ".." "template" "scripts" "template-marker.ps1")
$validationMode = 'full'
$validationPlan = $null
if ($ValidationPlanPath) {
    if (-not $Tier -or $Scenarios.Count -gt 0 -or $SkipFrontend -or $SkipRuntime -or $ContainerSmokeScenarios.Count -gt 0) {
        throw '-ValidationPlanPath requires -Tier and may not combine with manual skips/scenarios.'
    }
    $validationPlan = Read-QualityValidationPlan $ValidationPlanPath $Tier
    if (-not $validationPlan.Jobs.'template-slices') { throw 'Internal-document plans must not run dynamic scenarios.' }
    $validationMode = $validationPlan.Mode
    if ($ContainerSmoke -and -not $validationPlan.ContainerSmoke) { throw 'Manual container scope differs from the plan.' }
    $ContainerSmoke = $validationPlan.ContainerSmoke
}

if ($GenerateOnly -and ($ValidationPlanPath -or $ContainerSmoke -or $ContainerSmokeScenarios.Count -gt 0 -or $SkipPack -or $LocalFeedPath)) {
    # 这些开关都作用于打包、构建或容器阶段；与只生成同用时没有可执行的含义，直接拒绝而不是静默忽略。
    throw '-GenerateOnly cannot be combined with -ValidationPlanPath, -ContainerSmoke, -ContainerSmokeScenarios, -SkipPack or -LocalFeedPath.'
}
if ($Slice -and -not $Tier) { throw "-Slice requires -Tier." }
if ($ContainerSmoke -and -not $Tier) { throw "-ContainerSmoke requires -Tier." }
if ($Tier) {
    if ($Scenarios.Count -gt 0) { throw "-Tier and -Scenarios cannot be combined." }
    if ($validationPlan) {
        if ($Slice -and $Slice -cnotin @($validationPlan.Slices.key)) { throw "Unknown planned execution group '$Slice'." }
        $Scenarios = if ($Slice) { @(($validationPlan.Slices | Where-Object { $_.key -ceq $Slice }).Scenarios) } else {
            @($validationPlan.Slices | ForEach-Object { $_.Scenarios })
        }
    } else {
        if ($Slice -and -not $MatrixSlices[$Tier].Contains($Slice)) {
            throw "Unknown $Tier-tier slice '$Slice'. Valid slices: $($MatrixSlices[$Tier].Keys -join ', ')"
        }
        $Scenarios = Get-TierScenarios $Tier $Slice
    }
    if ($ContainerSmoke) {
        $ContainerSmokeScenarios += @($ContainerScenarios | Where-Object { $_ -in $Scenarios })
    }
}

if ($Scenarios.Count -eq 0) {
    if ($validationPlan) { throw 'No selected scenarios in this planned slice.' }
    $Scenarios = $AllScenarios
}

foreach ($scenario in $Scenarios) {
    if (-not $scenarioMap.Contains($scenario)) {
        throw "Unknown scenario '$scenario'. Valid scenarios: $($AllScenarios -join ', ')"
    }
}
foreach ($scenario in $ContainerSmokeScenarios) {
    if ($scenario -notin $Scenarios) {
        throw "Container smoke scenario '$scenario' must also be selected with -Scenarios."
    }
    if ($validationMode -cne 'full') { throw 'Container validation requires full stages.' }
}

# 独立入口先预检，避免错误源码仍先 pack；CI 只核对接替清单，不重复扫描。
Invoke-SourcePreflight -Skip:$SkipSourcePreflight

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
[IO.File]::WriteAllText($lockFile, ("pid={0} started={1}" -f $PID, (Get-Date -Format "o")), [Text.UTF8Encoding]::new($false))

# 清理陈旧 run 目录：仅删「非本 run」且「超过 $staleRunAgeHours 未活动」的目录，绝不删正在运行的 run——
# 支持多 AI/终端并行执行。活动判据：目录里 .run.lock（无则回退目录本身）的最后写入时间。被锁清不掉也无妨（尽力而为）。
$oldRunsRoot = Join-Path $tempRoot "runs"
$staleBefore = (Get-Date).AddHours(-$staleRunAgeHours)
if (Test-Path -LiteralPath $oldRunsRoot) {
    Get-ChildItem -LiteralPath $oldRunsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $runRoot } |
        Where-Object {
            $lock = Join-Path $_.FullName ".run.lock"
            # TOCTOU 安全：Test-Path 与读取之间锁可能被并行 run 删除，Get-Item 加 SilentlyContinue 返回 $null 后回退目录时间，
            # 避免在 $ErrorActionPreference=Stop 下因「文件已消失」中止整轮清理（进而中止整轮门禁）。
            $lockItem = if (Test-Path -LiteralPath $lock) { Get-Item -LiteralPath $lock -ErrorAction SilentlyContinue } else { $null }
            $activityTime = if ($lockItem) { $lockItem.LastWriteTime } else { $_.LastWriteTime }
            $activityTime -lt $staleBefore
        } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
New-Item -ItemType Directory -Path $hiveRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
$env:NUGET_PACKAGES = $packagesRoot

$escapedFeedRoot = [Security.SecurityElement]::Escape($feedRoot)
$escapedPackagesRoot = [Security.SecurityElement]::Escape($packagesRoot)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$escapedPackagesRoot" />
  </config>
  <packageSources>
    <clear />
    <add key="local-feed" value="$escapedFeedRoot" />
    <add key="nuget.org" value="$nugetOrg" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-feed"><package pattern="Leistd.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
[IO.File]::WriteAllText($nugetConfigPath, $nugetConfig, [Text.UTF8Encoding]::new($false))
# 生成项目自己的 restore（scripts/verify.ps1）不带 --configfile，按目录层级取到这份同内容配置
[IO.File]::WriteAllText((Join-Path $generatedRoot "NuGet.Config"), $nugetConfig, [Text.UTF8Encoding]::new($false))

if ($GenerateOnly) {
    # 只生成：模板从源码目录安装，不消费 Leistd 包，无需本地包源
}
elseif (-not $SkipPack) {
    Invoke-External "pwsh" @("-NoProfile", "-File", (Join-Path $repoRoot "framework/build/pack-local-feed.ps1"), "-Configuration", $Configuration, "-OutputPath", $feedRoot)
}
elseif (-not (Test-Path -LiteralPath $feedRoot)) {
    throw "-SkipPack requires an existing feed at '$feedRoot'（先 `pwsh framework/build/pack-local-feed.ps1`，或省略 -SkipPack 以重新 pack）."
}
$candidateFeed = $null
if (-not $GenerateOnly) {
    $candidateFeed = Read-CandidateManifest $feedRoot
    # 生成项目（含其 verify.ps1 起的 restore/build/test 子进程）都在 generatedRoot 之下
    Set-CandidateVersionOverride $generatedRoot $candidateFeed.CandidateVersion
    Write-Host "候选 Framework 包：$($candidateFeed.CandidateVersion)（SDK $($candidateFeed.Sdk)）"
}

Invoke-External "dotnet" @("new", "--debug:custom-hive", $hiveRoot, "install", $templateRoot, "--force")
# 来源标记记录生成时的 SDK：dotnet new 与这里同在仓库根执行，按同一 global.json 选 SDK
Push-Location $repoRoot
try { $generationSdk = (& dotnet --version).Trim() } finally { Pop-Location }

$results = [System.Collections.Generic.List[object]]::new()
try {
    foreach ($scenario in $Scenarios) {
        # 心跳：刷新锁文件时间戳，防止长时间运行的 run 被并行进程按 stale 目录清理。
        [IO.File]::SetLastWriteTimeUtc($lockFile, [DateTime]::UtcNow)
        $definition = $scenarioMap[$scenario]
        $definition["Name"] = $scenario
        $projectName = Get-ScenarioProjectName $scenario
        $projectRoot = Join-Path $generatedRoot $scenario
        $newArguments = @("new", "--debug:custom-hive", $hiveRoot, "fullstack-app", "-n", $projectName, "-o", $projectRoot, "--force") + $definition.Arguments
        Invoke-External "dotnet" $newArguments
        Assert-GeneratedProject $projectRoot
        Assert-ScenarioShape $projectRoot $projectName $definition
        # 从模板源码目录安装：标记记录占位的包版本与源提交
        Assert-ScenarioMarker $projectRoot $scenario $projectName '0.0.0-source' ('0' * 40) $generationSdk
        Assert-VerifySteps $projectRoot
        if ($GenerateOnly) {
            $results.Add([PSCustomObject]@{
                Scenario = $scenario
                Generate = 'pass'
                Output = [IO.Path]::GetRelativePath($repoRoot, $projectRoot)
            })
            continue
        }

        $backendValidated = $false
        $runtimeValidated = $false
        $frontendValidated = $false
        $lintValidated = $false
        $testValidated = $false
        $browserTests = 0
        $e2eTests = 0
        # 登记了 Verify 的场景在完整阶段由生成项目自己的 verify.ps1 完成构建与测试，矩阵只补运行时冒烟、
        # 前端 spec 发现范围与产物断言。verify 固定 Release、无头浏览器且前后端一起跑，人工改了这些开关时回到逐阶段执行。
        # 包源与版本：本 run 的 NuGet.Config 与 Directory.Build.rsp 放在生成目录上层，verify 的 dotnet 子进程按目录层级
        # 取到它们（候选包源与候选版本）；verify 结束后同样断言解包目录里的 Leistd 包来源。
        $useVerify = $definition.Verify -and $validationMode -ceq 'full' -and -not $SkipFrontend -and
            $Configuration -ceq 'Release' -and $FrontendBrowser -ceq 'chromiumHeadless'
        if ($definition.Verify -and -not $useVerify) {
            Write-Warning "Scenario '$scenario' runs matrix stages instead of scripts/verify.ps1 (mode, configuration, browser or frontend skip differs)."
        }
        if ($useVerify) {
            $env:HUSKY = "0"
            # 有前端时 verify 内含一次前端单测（浏览器取 angular.json），从同一份输出读出用例数
            if ($definition.Frontend) {
                Assert-ChromiumTestBrowsers (Join-Path $projectRoot "frontend")
                $counts = Invoke-ExternalCountingBrowserTests 'pwsh' @('-NoProfile', '-File', (Join-Path $projectRoot 'scripts/verify.ps1')) $projectRoot `
                    -ExpectE2E:(Test-ScenarioE2E $scenario)
                $browserTests = $counts.Browser
                $e2eTests = $counts.E2E
            }
            else {
                Invoke-External 'pwsh' @('-NoProfile', '-File', (Join-Path $projectRoot 'scripts/verify.ps1')) $projectRoot
            }
            Assert-CandidatePackages $packagesRoot $candidateFeed
            $backendValidated = $true
            if (-not $SkipRuntime) {
                Invoke-RuntimeSmoke $projectRoot $Configuration $scenario
                $runtimeValidated = $true
            }
            if ($definition.Frontend) {
                $frontendRoot = Join-Path $projectRoot "frontend"
                Invoke-External "npx" @("ng", "g", "@spartan-ng/cli:info", "--json") $frontendRoot
                Invoke-ExternalWithClosedInput "npx" @("ng", "g", "@spartan-ng/cli:healthcheck") $frontendRoot
                Assert-OptimizedTranslations $frontendRoot
                Assert-EveryFrontendSpecDiscovered $frontendRoot
                $lintValidated = $true
                $frontendValidated = $true
                $testValidated = $true
            }
        }
        if (-not $useVerify -and $validationMode -cne 'frontend') {
            $solution = Get-ChildItem -LiteralPath (Join-Path $projectRoot "backend") -Filter "*.sln" | Select-Object -First 1
            # --force 重建 project.assets.json；globalPackagesFolder 是本 run 私有目录，不会命中其他 run 的同版本 Leistd 内容。
            Invoke-External "dotnet" @("restore", $solution.FullName, "--configfile", $nugetConfigPath, "--force")
            Assert-CandidatePackages $packagesRoot $candidateFeed
            # 警告即错误：backend/.editorconfig 的风格规则以 warning 交付，生成项目的构建线是 0 警告
            Invoke-External "dotnet" @("build", $solution.FullName, "-c", $Configuration, "--no-restore", "-p:TreatWarningsAsErrors=true")

            if (-not $SkipRuntime) {
                Invoke-RuntimeSmoke $projectRoot $Configuration $scenario
                $runtimeValidated = $true
            }

            $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "backend") -Filter "*Tests.csproj" -Recurse)
            if ($testProjects.Count -eq 0) { throw 'No backend test projects found; refusing empty validation.' }
            foreach ($testProject in $testProjects) {
                Invoke-External "dotnet" @("test", $testProject.FullName, "-c", $Configuration, "--no-build")
            }
            $backendValidated = $true
        }

        if (-not $useVerify -and -not $SkipFrontend -and $definition.Frontend -and $validationMode -cne 'backend') {
            $frontendRoot = Join-Path $projectRoot "frontend"
            $env:HUSKY = "0"
            Invoke-External "npm" @("ci", "--no-audit") $frontendRoot
            Invoke-External "npx" @("ng", "g", "@spartan-ng/cli:info", "--json") $frontendRoot
            # healthcheck 末尾会无条件询问"是否升级依赖"，默认 N。
            # 该 CLI 没有能覆盖这个提示的非交互开关——实测 --interactive=false、--defaults
            # 与 CI=true 三者都不生效（提示由它自带的提示库发出，不走 Angular schematic 提示）。
            # 因此显式把 stdin 关掉：拿到 EOF 就取默认值 N，有无 TTY 行为一致，
            # 不依赖"调用方恰好重定向了 stdin"
            Invoke-ExternalWithClosedInput "npx" @("ng", "g", "@spartan-ng/cli:healthcheck") $frontendRoot
            if ($definition.Lint) {
                Invoke-External "npm" @("run", "lint") $frontendRoot
                $lintValidated = $true
            }
            Invoke-External "npm" @("run", "build") $frontendRoot
            Assert-OptimizedTranslations $frontendRoot
            $frontendValidated = $true

            # 前端单测（单次）：CI 默认 chromiumHeadless，人工验收可传 -FrontendBrowser chromium 观看有头浏览器。
            # 每个场景都含一条不受本地化裁剪的基础 smoke spec；本地化场景另含语言切换「先加载再激活」与首帧词条的回归测试。
            # 先核对"磁盘上的每个 spec 都会被发现"，再真的跑。放在前面是因为它更快（--list-tests 不构建也不执行），
            # 而且这条不通过时后面那轮绿灯是假的。
            Assert-EveryFrontendSpecDiscovered $frontendRoot
            $browserTests = (Invoke-ExternalCountingBrowserTests "npm" @("test", "--", "--watch=false", "--browsers=$FrontendBrowser") $frontendRoot).Browser
            $testValidated = $true
        }

        $containerValidated = $false
        if ($scenario -in $ContainerSmokeScenarios) {
            # 候选 Framework 包可能尚未发布。只在本轮生成目录注入本地包和 NuGet.Config，
            # 让镜像验证消费的仍是同一候选源码；交付模板不包含这个临时包源。
            $containerFeed = Join-Path $projectRoot "backend/.local-feed"
            New-Item -ItemType Directory -Path $containerFeed -Force | Out-Null
            Copy-Item -Path (Join-Path $feedRoot "*.nupkg") -Destination $containerFeed
            $containerNuGetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="/src/backend/.local-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="Leistd.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
'@
            [IO.File]::WriteAllText((Join-Path $projectRoot "backend/NuGet.Config"), $containerNuGetConfig)
            # 构建上下文只拷入 backend/，外层的版本覆盖进不了镜像，同样放在 backend/ 下；
            # Dockerfile 的 dotnet publish 走隐式 restore，需要 -ImplicitRestore 让 restore 阶段也用候选版本
            Set-CandidateVersionOverride (Join-Path $projectRoot "backend") $candidateFeed.CandidateVersion -ImplicitRestore
            $apiImage = "leistd-template-smoke-api:$runId"
            $migratorImage = "leistd-template-smoke-migrator:$runId"
            try {
                Invoke-External "docker" @("build", "--target", "api", "-t", $apiImage, $projectRoot)
                Invoke-External "docker" @("build", "--target", "migrator", "-t", $migratorImage, $projectRoot)
                Invoke-External "docker" @("run", "--rm", "--entrypoint", "dotnet", $apiImage, "--info")
                Invoke-External "docker" @("run", "--rm", "--entrypoint", "dotnet", $migratorImage, "--info")
                if (-not $definition.Frontend) {
                    Invoke-PureApiContainerSmoke $scenario $apiImage $migratorImage
                }
                $containerValidated = $true
            }
            finally {
                & docker image rm $apiImage $migratorImage 2>$null | Out-Null
                $global:LASTEXITCODE = 0
            }
        }

        $results.Add([PSCustomObject]@{
            Scenario = $scenario
            Backend = if ($backendValidated) { 'pass' } else { 'not-applicable' }
            Runtime = if ($runtimeValidated) { 'pass' } elseif ($validationMode -ceq 'frontend') { 'not-applicable' } else { 'skipped' }
            Lint = if ($lintValidated) { 'pass' } elseif ($validationMode -ceq 'backend' -or -not $definition.Frontend) { 'not-applicable' } else { 'skipped' }
            Frontend = if ($frontendValidated) { 'pass' } elseif ($validationMode -ceq 'backend' -or -not $definition.Frontend) { 'not-applicable' } else { 'skipped' }
            Test = if ($testValidated) { 'pass' } elseif ($validationMode -ceq 'backend' -or -not $definition.Frontend) { 'not-applicable' } else { 'skipped' }
            BrowserTests = $browserTests
            E2ETests = $e2eTests
            Container = if ($containerValidated) { "pass" } else { "skipped" }
            Verify = if ($useVerify) { 'pass' } else { 'not-run' }
            Output = [IO.Path]::GetRelativePath($repoRoot, $projectRoot)
        })
    }
}
finally {
    Stop-RuntimeDatabase
}

$results | Format-Table -AutoSize
if ($GenerateOnly) {
    # 只生成模式不产出回执：回执代表完整阶段通过，汇总作业不能把形态检查当成构建与测试证据
    Write-Host "Template generation and document checks passed for $($results.Count) scenario(s); pack, build, test and runtime were not run." -ForegroundColor Green
    exit 0
}
Write-Host "Template matrix passed for $($results.Count) scenario(s)." -ForegroundColor Green

# 只在所有阶段成功后产出证明；汇总作业核对场景全集和阶段，缺片不得假绿。
$receiptSlices = if ($validationPlan -and -not $Slice) { @($validationPlan.Slices.key) } elseif ($Tier -and -not $Slice) {
    @($MatrixSlices[$Tier].Keys)
} else { @($Slice) }
foreach ($receiptSlice in $receiptSlices) {
    $plannedMembers = if ($validationPlan) { @(($validationPlan.Slices | Where-Object { $_.key -ceq $receiptSlice }).Scenarios) } else { @() }
    $sliceResults = @($results | Where-Object {
        if ($validationPlan) { $_.Scenario -cin $plannedMembers } else {
            -not $Tier -or -not $receiptSlice -or $scenarioMap[$_.Scenario].Slices[$Tier] -ceq $receiptSlice
        }
    })
    if ($sliceResults.Count -eq 0) { continue }
    $resultFile = Join-Path $runRoot "matrix-$(if ($receiptSlice) { $receiptSlice } else { 'local' }).json"
    [PSCustomObject]@{ Version = 3; Tier = $Tier; Slice = $receiptSlice; CandidateSha = (git rev-parse HEAD); CandidateVersion = $candidateFeed.CandidateVersion; Mode = $validationMode; Results = $sliceResults } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultFile -Encoding utf8
    Write-Host "Receipt: $resultFile"
}
$resultsPath = if ($Tier -and -not $Slice) { $runRoot } else { $resultFile }
if ($env:GITHUB_OUTPUT) { "results_path=$resultsPath" | Out-File $env:GITHUB_OUTPUT -Append }
