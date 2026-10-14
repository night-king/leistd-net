#!/usr/bin/env pwsh

<#
.SYNOPSIS
    跑完本仓库的全部静态闸门，并汇总结果。

.DESCRIPTION
    **这里是闸门清单的唯一权威来源。** 新增闸门只需加进下面的 $gates，CI 与文档都不必再改——
    以前闸门散在三处（ci.yml 的独立步骤、test-template-matrix.ps1 内部、两份 development-guide
    各列一半），"本仓库有哪些检查、怎么一次跑完"没有任何一个地方能回答。

    只收静态闸门。需要构建产物或跑起来才能验的不在这里，各有自己的入口：
      - dotnet build / test                             框架源码
      - framework/build/pack-local-feed.ps1
        + framework/build/test-package-consumption.ps1  NuGet 隔离消费
      - scripts/test-template-matrix.ps1                场景生成 + 构建 + 前后端测试（-Tier pr 为 PR 档子集）
      - scripts/test-template-postgresql-e2e.ps1        真实 PostgreSQL 端到端
      - scripts/test-template-oidc-e2e.ps1              真实 OIDC 跨服务 HTTP 端到端

    模板源码的 symbols / using-guards / async-boundaries 由这里完整执行。
    独立矩阵入口默认也在生成前预检；同候选 CI 显式跳过重复扫描，必过汇总核对本作业成功。

.PARAMETER List
    只打印闸门清单，不执行。

.PARAMETER StopOnFirstFailure
    首个失败即停。默认跑完全部再汇总——一次看清所有问题，比修一个跑一轮快。
#>

param(
    [switch]$List,
    [switch]$StopOnFirstFailure
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Python 闸门的解释器：CI/Unix 常为 python3，Windows 通常只有 python（约定见 docs/framework/development-guide.md §9）。
# 需要 3.10+：EncodingWarning 与 -X warn_default_encoding 从 3.10 起才有，低版本会静默忽略这两个设置，
# 闸门看似在查默认编码，实际什么也没查。系统自带 3.9 的机器回落到带版本号的可执行名。
$pythonCmd = $null
$pythonSeen = @()
foreach ($candidate in @("python3", "python", "python3.14", "python3.13", "python3.12", "python3.11", "python3.10")) {
    $found = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { continue }
    $version = "$(& $found.Source -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $version) { continue }
    $pythonSeen += "$candidate=$version"
    if ([version]$version -ge [version]"3.10") { $pythonCmd = $found.Source; break }
}
if (-not $pythonCmd) {
    $seen = if ($pythonSeen) { "（已找到：$($pythonSeen -join '、')）" } else { "" }
    throw "需要 Python 3.10 或更高版本运行 Python 静态闸门$seen：EncodingWarning 检查依赖 3.10 引入的 -X warn_default_encoding。"
}

# 默认编码一律视为错误：Windows 的默认编码不是 UTF-8，读写不写 encoding 的代码在那里读错中文。
# 直接运行的 Python 闸门带参数；PowerShell 闸门内部再起的 Python 子进程（如 check-i18n-keys.ps1）
# 拿不到这些参数，靠同义的环境变量继承——"Python 编码警告设置生效"闸门证明两条路径都真的生效。
$pythonFlags = @("-X", "warn_default_encoding", "-W", "error::EncodingWarning")
# PYTHONIOENCODING：Windows 上管道输出默认按 cp1252 编码，✅ 与中文会让闸门在打印结果时崩溃
$pythonEncodingEnv = @{ PYTHONWARNDEFAULTENCODING = "1"; PYTHONWARNINGS = "error::EncodingWarning"; PYTHONIOENCODING = "utf-8" }

# 入口 ps1 的文件头（development-guide §9）：首行 shebang；有注释帮助块时与 shebang 之间空一行，
# 帮助块紧贴 shebang 时 Get-Help 认不出它。只被点源加载的库脚本不能直接执行，不要求 shebang。
$dotSourcedLibraries = @(
    'scripts/docker-image.ps1',
    'scripts/generated-project-checks.ps1',
    "scripts/quality-validation-plan.ps1",
    "scripts/template-matrix-scenarios.ps1",
    "scripts/test-template-oidc-browser.ps1",
    "scripts/test-template-oidc-multitenant.ps1"
)

function Get-EntryScriptHeaderProblem([string[]]$Lines) {
    if ($Lines.Count -eq 0 -or $Lines[0] -cne "#!/usr/bin/env pwsh") {
        return "首行必须是 #!/usr/bin/env pwsh"
    }
    if ($Lines.Count -gt 1 -and $Lines[1].TrimStart().StartsWith("<#")) {
        return "注释帮助块紧贴 shebang：中间空一行，否则 Get-Help 认不出帮助块"
    }
    return $null
}

function Test-EntryScriptHeaderRules {
    $cases = @(
        @{ Name = "shebang 后空一行再写帮助块";    Lines = @("#!/usr/bin/env pwsh", "", "<#", ".SYNOPSIS", "#>"); Expect = $null }
        @{ Name = "shebang 后直接 param";          Lines = @("#!/usr/bin/env pwsh", "param()");                   Expect = $null }
        @{ Name = "缺 shebang";                    Lines = @("<#", ".SYNOPSIS", "#>");                           Expect = "首行必须是" }
        @{ Name = "shebang 不在首行";              Lines = @("", "#!/usr/bin/env pwsh");                         Expect = "首行必须是" }
        @{ Name = "写死解释器路径";                Lines = @("#!/usr/local/bin/pwsh");                           Expect = "首行必须是" }
        @{ Name = "帮助块紧贴 shebang";            Lines = @("#!/usr/bin/env pwsh", "<#", ".SYNOPSIS", "#>");    Expect = "紧贴" }
    )
    $failures = @()
    foreach ($case in $cases) {
        $problem = Get-EntryScriptHeaderProblem $case.Lines
        $ok = if ($null -eq $case.Expect) { $null -eq $problem } else { $problem -and $problem.Contains($case.Expect) }
        if (-not $ok) { $failures += "$($case.Name)：期望 $(if ($case.Expect) { "命中「$($case.Expect)」" } else { '通过' })，实际 $(if ($problem) { $problem } else { '通过' })" }
    }
    $listed = @(git -C $repoRoot -c core.quotepath=off ls-files -- "*.ps1")
    foreach ($library in $dotSourcedLibraries) {
        if ($listed -notcontains $library) { $failures += "库脚本白名单里的 $library 已不存在：从白名单删掉" }
    }
    if ($failures) { $failures | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 入口脚本文件头规则自检通过（$($cases.Count) 例，库脚本白名单 $($dotSourcedLibraries.Count) 项均存在）。" -ForegroundColor Green
    return $true
}

function Test-EntryScriptHeaders {
    $listed = @(git -C $repoRoot -c core.quotepath=off ls-files --cached --others --exclude-standard -- "*.ps1")
    if ($LASTEXITCODE -ne 0 -or $listed.Count -eq 0) { Write-Host "❌ 列不出任何 ps1 文件，判据失效" -ForegroundColor Red; return $false }
    $problems = @()
    $checked = 0
    foreach ($relative in $listed) {
        if ($dotSourcedLibraries -contains $relative) { continue }
        $path = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $checked++
        $problem = Get-EntryScriptHeaderProblem @(Get-Content -LiteralPath $path -TotalCount 2 -Encoding UTF8)
        if ($problem) { $problems += "$relative：$problem" }
    }
    if ($problems) { $problems | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 入口脚本文件头检查通过（$checked 个入口脚本，$($dotSourcedLibraries.Count) 个点源库脚本不要求）。" -ForegroundColor Green
    return $true
}

# 证伪探针：一段不写 encoding 的文本 I/O 必须失败，写了 encoding 的同一段必须通过。
# 两条启动路径各验一次：runner 直接带参数启动；经 PowerShell 再起的子进程只靠继承的环境变量。
function Test-PythonEncodingWarnings {
    $implicit = "import io; io.TextIOWrapper(io.BytesIO())"
    $explicit = "import io; io.TextIOWrapper(io.BytesIO(), encoding='utf-8')"
    $failures = @()
    # 直接启动这一路先摘掉环境变量，证明起作用的是参数本身
    $savedEnv = @($env:PYTHONWARNDEFAULTENCODING, $env:PYTHONWARNINGS)
    $env:PYTHONWARNDEFAULTENCODING = $null
    $env:PYTHONWARNINGS = $null
    try {
        & $pythonCmd @pythonFlags -c $explicit 2>$null
        if ($LASTEXITCODE -ne 0) { $failures += "直接启动：显式 encoding 的对照组失败，探针本身有误" }
        & $pythonCmd @pythonFlags -c $implicit 2>$null
        if ($LASTEXITCODE -eq 0) { $failures += "直接启动：未写 encoding 的文本 I/O 没有失败，-X/-W 参数未生效" }
    }
    finally {
        $env:PYTHONWARNDEFAULTENCODING, $env:PYTHONWARNINGS = $savedEnv
    }
    $quotedPython = $pythonCmd.Replace("'", "''")
    pwsh -NoProfile -Command "& '$quotedPython' -c `"$explicit`"; exit `$LASTEXITCODE" 2>$null
    if ($LASTEXITCODE -ne 0) { $failures += "经 PowerShell 启动：显式 encoding 的对照组失败，探针本身有误" }
    pwsh -NoProfile -Command "& '$quotedPython' -c `"$implicit`"; exit `$LASTEXITCODE" 2>$null
    if ($LASTEXITCODE -eq 0) { $failures += "经 PowerShell 启动：子进程没有收到 EncodingWarning 设置，环境变量未继承" }
    if ($failures) { $failures | ForEach-Object { Write-Host "  ❌ $_" -ForegroundColor Red }; return $false }
    Write-Host "✅ 默认编码警告即错误：直接启动与经 PowerShell 启动的 Python 都已生效（$pythonCmd）。" -ForegroundColor Green
    return $true
}

# 闸门三种写法：Cmd = "pwsh" 跑脚本；Cmd = $pythonCmd 跑 Python 闸门（自动带 $pythonFlags）；
# Run = { ... } 是本文件内联的检查，返回 $true 表示通过。
$gates = @(
    # 先证明运行环境：编码警告设置没生效时，后面每道 Python 闸门的"通过"都少验了一项
    @{ Name = "Python 编码警告设置生效";   Run = { Test-PythonEncodingWarnings } }
    @{ Name = "入口脚本文件头规则自检";    Run = { Test-EntryScriptHeaderRules } }
    @{ Name = "镜像拉取重试自检";          Run = { . (Join-Path $repoRoot "scripts/docker-image.ps1"); Test-DockerPullRetry } }
    @{ Name = "入口脚本文件头";            Run = { Test-EntryScriptHeaders } }
    @{ Name = "组件文档与索引一致";        Cmd = "pwsh"; Args = @("framework/build/check-docs-sync.ps1") }
    @{ Name = "文档 API 自检与引用不漂移";       Cmd = "pwsh"; Args = @("framework/build/check-docs-api-drift.ps1") }
    @{ Name = "Skill 与文档引用";          Cmd = "pwsh"; Args = @("scripts/validate-skills.ps1") }
    # 自检先跑：退役符号规则本身失效时，紧随其后的那次"通过"没有意义
    @{ Name = "退役符号规则自检";          Cmd = "pwsh"; Args = @("scripts/check-retired-terms.ps1", "-SelfTest") }
    @{ Name = "无已删除符号/旧表述残留";   Cmd = "pwsh"; Args = @("scripts/check-retired-terms.ps1") }
    # 项目判据随模板分发（template/scripts/check-i18n.py），本仓判据在 check-i18n-repo.py，入口统一跑两段
    @{ Name = "i18n 规则自检";             Cmd = "pwsh"; Args = @("scripts/check-i18n-keys.ps1", "-SelfTest") }
    @{ Name = "i18n 词条键一致";           Cmd = "pwsh"; Args = @("scripts/check-i18n-keys.ps1") }
    # 这道闸门随模板分发（template/scripts/），本仓直接跑那一份：
    # 实现只有一处，生成项目拿到的与这里跑的是同一个判据，不会各自漂移。
    @{ Name = "动作码词条规则自检";        Cmd = $pythonCmd; Args = @("template/scripts/check-operation-action-i18n.py", "--self-test") }
    @{ Name = "动作码有句子模板";          Cmd = $pythonCmd; Args = @("template/scripts/check-operation-action-i18n.py") }
    @{ Name = "模板条件符号";              Cmd = "pwsh"; Args = @("scripts/check-template-symbols.ps1") }
    @{ Name = "条件块规则自检";            Cmd = $pythonCmd; Args = @("scripts/check-template-conditional-blocks.py", "--self-test") }
    @{ Name = "模板条件块结构";            Cmd = $pythonCmd; Args = @("scripts/check-template-conditional-blocks.py") }
    # PR 档只跑场景子集；子集能否代表全集，由逐行求值判定，不靠人记
    @{ Name = "场景覆盖规则自检";          Cmd = $pythonCmd; Args = @("scripts/check-template-scenario-coverage.py", "--self-test") }
    @{ Name = "PR 档场景覆盖全部条件行";   Cmd = $pythonCmd; Args = @("scripts/check-template-scenario-coverage.py") }
    @{ Name = "using 守卫规则自检";         Cmd = $pythonCmd; Args = @("scripts/check-using-guards.py", "--self-test") }
    @{ Name = "模板 using/import 守卫";    Cmd = $pythonCmd; Args = @("scripts/check-using-guards.py") }
    # 错误码闸门随模板分发且不随本地化裁剪；本仓直接跑模板里那一份（脚本根即 template/）
    @{ Name = "错误码规则自检";            Cmd = $pythonCmd; Args = @("template/scripts/check-error-codes.py", "--self-test") }
    @{ Name = "错误码形态、归属与引用";    Cmd = $pythonCmd; Args = @("template/scripts/check-error-codes.py") }
    # 生成产物上的部署资产核对随 CI 的全部形态生成作业执行；这里只验判据本身
    @{ Name = "部署资产判据自检";          Cmd = $pythonCmd; Args = @("scripts/test-template-generation.py", "--self-test") }
    # 后端路由、前端调用与 Mock 键三方求差；源码上按条件分支取并集
    @{ Name = "Mock 覆盖规则自检";         Cmd = $pythonCmd; Args = @("scripts/check-template-mock-coverage.py", "--self-test") }
    @{ Name = "前端端点都有 Mock";         Cmd = $pythonCmd; Args = @("scripts/check-template-mock-coverage.py") }
    @{ Name = "动态连接路径异步边界";      Cmd = $pythonCmd; Args = @("scripts/check-async-boundaries.py") }
    # 自检先跑：豁免机制本身失效时，紧随其后的那次"通过"没有意义
    @{ Name = "时间源规则自检";            Cmd = $pythonCmd; Args = @("scripts/check-clock-access.py", "--self-test") }
    @{ Name = "时间源可替换";              Cmd = $pythonCmd; Args = @("scripts/check-clock-access.py") }
    @{ Name = "DbContext 访问口径";        Cmd = $pythonCmd; Args = @("scripts/check-dbcontext-access.py") }
    @{ Name = "csproj 约定规则自检";       Cmd = $pythonCmd; Args = @("scripts/check-csproj-conventions.py", "--self-test") }
    @{ Name = "csproj 约定";               Cmd = $pythonCmd; Args = @("scripts/check-csproj-conventions.py") }
    # 日志调用点改回记原文不会让任何用例变红（调用点在要连 SMTP 的方法里），判据只能放在这里
    @{ Name = "联系方式日志规则自检";      Cmd = $pythonCmd; Args = @("scripts/check-contact-info-logging.py", "--self-test") }
    @{ Name = "联系方式不进日志";          Cmd = $pythonCmd; Args = @("scripts/check-contact-info-logging.py") }
    # 覆盖率报告发现不了"程序集从未被任何测试加载"——那种包根本不出现在报告里
    @{ Name = "测试布局规则自检";          Cmd = $pythonCmd; Args = @("scripts/check-test-layout.py", "--self-test") }
    @{ Name = "测试布局与家族对应";        Cmd = $pythonCmd; Args = @("scripts/check-test-layout.py") }
    # 测试报告、CI 日志与 IDE 测试树里的名字统一用英文；中文只在注释与测试数据里
    @{ Name = "测试名规则自检";            Cmd = $pythonCmd; Args = @("scripts/check-test-names.py", "--self-test") }
    @{ Name = "测试名用英文";              Cmd = $pythonCmd; Args = @("scripts/check-test-names.py") }
    # 随模板分发（template/scripts/）：生成项目的 verify 跑项目模式，本仓以 --repository 另扫框架测试
    @{ Name = "测试诊断规则自检";          Cmd = $pythonCmd; Args = @("template/scripts/check-test-diagnostics.py", "--self-test") }
    @{ Name = "测试诊断消息用英文";        Cmd = $pythonCmd; Args = @("template/scripts/check-test-diagnostics.py", "--repository") }
    @{ Name = "XML 注释形态规则自检";      Cmd = $pythonCmd; Args = @("scripts/check-doc-comment-shape.py", "--self-test") }
    @{ Name = "XML 注释形态";              Cmd = $pythonCmd; Args = @("scripts/check-doc-comment-shape.py") }
    @{ Name = "组件文档骨架规则自检";      Cmd = $pythonCmd; Args = @("scripts/check-docs-skeleton.py", "--self-test") }
    @{ Name = "组件文档骨架";              Cmd = $pythonCmd; Args = @("scripts/check-docs-skeleton.py") }
    # 编译在包消费作业里（需要本地包源）；这里先跑抽取规则自检与豁免标记校验，示例写错标记不必等打包
    @{ Name = "组件文档示例抽取";          Cmd = $pythonCmd; Args = @("scripts/extract-doc-snippets.py", "--output", ".tmp/check-all/doc-snippets") }
    # 矩阵在生成产物上做权威检查（含条件裁剪删掉被链接章节）；源码这道给模板文档改动即时反馈。
    # 源码含全部条件分支的标题（并集），条件标记不会造成误报，只可能漏掉裁剪类问题。
    @{ Name = "章节锚点规则自检";          Cmd = $pythonCmd; Args = @("scripts/check-markdown-anchors.py", "--self-test") }
    @{ Name = "模板文档章节锚点";          Cmd = $pythonCmd; Args = @("scripts/check-markdown-anchors.py", "template") }
    # 源码模式只查路径与链接；命令对照的是条件裁剪后的配置，由矩阵在生成产物上以 --root 执行
    @{ Name = "文档引用规则自检";          Cmd = $pythonCmd; Args = @("scripts/check-doc-references.py", "--self-test") }
    @{ Name = "文档引用存在性";            Cmd = $pythonCmd; Args = @("scripts/check-doc-references.py") }
)

if ($List) {
    Write-Host "本仓库静态闸门（$($gates.Count) 道）：" -ForegroundColor Cyan
    foreach ($g in $gates) {
        $command = if ($g.Run) { "（内联）" } elseif ($g.Cmd -eq $pythonCmd) { "$pythonCmd $($pythonFlags -join ' ')" } else { $g.Cmd }
        Write-Host ("  {0,-28} {1} {2}" -f $g.Name, $command, ($g.Args -join ' '))
    }
    exit 0
}

# 环境变量只在跑闸门期间生效，结束后恢复：以 & 在当前会话调用本脚本时不污染调用方
$savedPythonEnv = @{}
foreach ($name in $pythonEncodingEnv.Keys) {
    $savedPythonEnv[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $pythonEncodingEnv[$name])
}

$results = @()
try {
    foreach ($g in $gates) {
        Write-Host "▶ $($g.Name)" -ForegroundColor Cyan
        $started = Get-Date
        # 闸门自己的输出直通终端：失败时诊断信息就在眼前，不必再单独复跑一次
        if ($g.Run) {
            $ok = [bool](& $g.Run | Select-Object -Last 1)
        }
        else {
            $gateArgs = @($g.Args | ForEach-Object { if ($_ -like '-*') { $_ } else { Join-Path $repoRoot $_ } })
            if ($g.Cmd -eq $pythonCmd) { $gateArgs = @($pythonFlags) + $gateArgs }
            # 输出照常直通终端，同时留一份给 CI 注解
            & $g.Cmd @gateArgs 2>&1 | Tee-Object -Variable gateOutput | Out-Host
            $ok = $LASTEXITCODE -eq 0
        }
        $results += [PSCustomObject]@{
            Gate    = $g.Name
            Passed  = $ok
            Seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        }
        # GitHub Actions 的作业日志要登录才能看，注解公开可见：失败的闸门写成错误注解，带上输出末尾
        if (-not $ok -and $env:GITHUB_ACTIONS -eq 'true') {
            $tail = if (-not $g.Run -and $gateOutput) { @($gateOutput | ForEach-Object { "$_" } | Select-Object -Last 30) -join "`n" } else { "见作业日志" }
            $escaped = $tail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
            # 属性值另需转义 : 与 ,（GitHub 工作流命令的属性规则）
            $title = "check-all：$($g.Name)".Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A').Replace(':', '%3A').Replace(',', '%2C')
            Write-Host "::error title=$title::$escaped"
        }
        $gateOutput = $null
        if (-not $ok -and $StopOnFirstFailure) { break }
    }
}
finally {
    foreach ($name in $savedPythonEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedPythonEnv[$name]) }
}

Write-Host ""
Write-Host "═══ 汇总 ═══" -ForegroundColor Cyan
foreach ($r in $results) {
    $mark = if ($r.Passed) { "✅" } else { "❌" }
    $color = if ($r.Passed) { "Green" } else { "Red" }
    Write-Host ("  {0} {1,-28} {2,5}s" -f $mark, $r.Gate, $r.Seconds) -ForegroundColor $color
}

$failed = @($results | Where-Object { -not $_.Passed })
$skipped = $gates.Count - $results.Count
if ($skipped -gt 0) { Write-Host "  （因 -StopOnFirstFailure 未执行 $skipped 道）" -ForegroundColor Yellow }

if ($failed.Count -gt 0) {
    Write-Host ""
    throw "$($failed.Count)/$($gates.Count) 道闸门失败：$(($failed.Gate) -join '、')"
}

Write-Host ""
Write-Host "全部 $($gates.Count) 道静态闸门通过。" -ForegroundColor Green
