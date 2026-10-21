#!/usr/bin/env pwsh
# 文档-源码 API 漂移校验
# 目的：抓住组件/ddd 文档正文里“臆造的 Leistd API”——文档写了反引号包裹的类型/成员名，
#       但该名字在框架源码里根本不存在（如曾出现的 tracing `IProxyGenerator`、event-bus `LogError`）。
# 策略（保守，宁可漏报不误报）：
#   - `Leistd.*` 完整限定名必须匹配真实包、命名空间、公共类型或其成员；
#   - 其余只校验 `反引号包裹` 且形如类型/接口名（PascalCase 或 I+大写开头）的标识符；
#   - 只在该标识符“看起来是 Leistd 自有 API”时校验：即它不在 .NET BCL / EF / 第三方 白名单里；
#   - 判定“存在”：该标识符作为一个词出现在 framework 任一 .cs 源码（排除 bin/obj）中；
#   - 不存在 → 记为疑似臆造，CI 失败。
# 闭包（docs/framework/development-guide.md §5.1）：组件文档代码块里的 Leistd 公共类型、Add*/Use*/Map* 入口
#   及其他扩展方法，至少有一个定义包属于本篇家族 csproj 的 ProjectReference 传递闭包；`## 注册` 段里调用兄弟组件
#   Add*/Use*/Map* 的宿主组合行整行豁免。代码块自己声明的类型、注释与字符串不参与判定。
# 用法：pwsh framework/build/check-docs-api-drift.ps1  （从仓库根运行）

$ErrorActionPreference = 'Stop'

$docGlobs = @(
    'framework/docs/components/*.md',
    'framework/docs/ddd-struct/*.md'
)

# 收集全部框架源码符号（排除 bin/obj）。用“定义处”正则提取 public 类型/成员名，作为真实 API 集合。
$srcFiles = Get-ChildItem -Recurse -File -Include *.cs framework/components, framework/ddd-struct |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$sourceTexts = [System.Collections.Generic.List[string]]::new()

# 完整限定名必须匹配真实包、命名空间或公共类型。短标识符存在并不能证明
# `Leistd.Core.Timing.IClock` 这类旧限定名仍然有效。
$packageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
Get-ChildItem -Recurse -File -Include *.csproj framework/components, framework/ddd-struct |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|tests)[\\/]' } |
    ForEach-Object { [void]$packageIds.Add($_.BaseName) }

# 包目录 → 包名、包 → 直接引用的 Leistd 包：闭包判定用。
$packageDirs = @{}
$packageRefs = @{}
Get-ChildItem -Recurse -File -Include *.csproj framework/components, framework/ddd-struct |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|tests)[\\/]' } |
    ForEach-Object {
        $packageDirs[$_.DirectoryName + [IO.Path]::DirectorySeparatorChar] = $_.BaseName
        $packageRefs[$_.BaseName] = @([regex]::Matches((Get-Content $_.FullName -Raw), '<ProjectReference\s+Include="([^"]+)"') |
            ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Groups[1].Value.Replace('\', '/')) })
    }
$typeOwners = @{}
$entryOwners = @{}
$extensionOwners = @{}

$namespaces = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$qualifiedTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$typeSources = @{}
$qualifiedTypeSources = @{}
foreach ($srcFile in $srcFiles) {
    $text = Get-Content $srcFile.FullName -Raw
    $sourceTexts.Add($text)
    $namespaceMatch = [regex]::Match($text, '(?m)^\s*namespace\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;')
    if (-not $namespaceMatch.Success) { continue }

    $namespace = $namespaceMatch.Groups[1].Value
    [void]$namespaces.Add($namespace)
    $typePattern = '(?m)^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly|ref)\s+)*(?:class|record(?:\s+(?:class|struct))?|struct|interface|enum)\s+([A-Za-z_]\w*)'
    $ownerDir = @($packageDirs.Keys | Where-Object { $srcFile.FullName.StartsWith($_, [StringComparison]::Ordinal) } |
        Sort-Object Length -Descending | Select-Object -First 1)
    $owner = if ($ownerDir) { $packageDirs[$ownerDir[0]] } else { $null }
    if ($owner) {
        # 扩展方法都按定义包计闭包；其中 Add*/Use*/Map* 是宿主组合行豁免所认的注册入口。
        $extensionPattern = 'public\s+static\s+[^=;{(]*?\b([A-Z]\w*)\s*(?:<[^>]*>)?\s*\(\s*this\b'
        foreach ($extensionMatch in [regex]::Matches($text, $extensionPattern)) {
            $name = $extensionMatch.Groups[1].Value
            $extensionOwners[$name] = @($extensionOwners[$name]) + $owner | Where-Object { $_ } | Sort-Object -Unique
            if ($name -cmatch '^(Add|Use|Map)') { $entryOwners[$name] = $extensionOwners[$name] }
        }
    }
    foreach ($typeMatch in [regex]::Matches($text, $typePattern)) {
        $typeName = $typeMatch.Groups[1].Value
        if ($owner) { $typeOwners[$typeName] = @($typeOwners[$typeName]) + $owner | Where-Object { $_ } | Sort-Object -Unique }
        $qualifiedType = "$namespace.$typeName"
        [void]$qualifiedTypes.Add($qualifiedType)
        $typeSources[$typeName] = @($typeSources[$typeName]) + $text
        $qualifiedTypeSources[$qualifiedType] = @($qualifiedTypeSources[$qualifiedType]) + $text
    }
}

$srcText = $sourceTexts -join "`n"

function Test-MemberInSources([string]$Member, [object[]]$Sources) {
    return [bool]($Sources | Where-Object { $_ -match "\b$([regex]::Escape($Member))\b" } | Select-Object -First 1)
}

function Test-MemberReference([string]$Reference) {
    $match = [regex]::Match($Reference, '^(?<type>[A-Z][A-Za-z0-9]*)(?:<[^>]+>)?\.(?<member>[A-Z][A-Za-z0-9]*)')
    if (-not $match.Success -or -not $typeSources.ContainsKey($match.Groups['type'].Value)) { return $true }
    return Test-MemberInSources $match.Groups['member'].Value $typeSources[$match.Groups['type'].Value]
}

function Test-QualifiedReference([string]$Reference) {
    if ($Reference -cnotmatch '^Leistd\.' -or $Reference -match '[*<>:]') { return $true }
    if ($packageIds.Contains($Reference) -or $namespaces.Contains($Reference) -or $qualifiedTypes.Contains($Reference)) {
        return $true
    }

    foreach ($type in $qualifiedTypes) {
        if (-not $Reference.StartsWith("$type.", [System.StringComparison]::Ordinal)) { continue }
        $member = $Reference.Substring($type.Length + 1).Split('.')[0]
        return Test-MemberInSources $member $qualifiedTypeSources[$type]
    }
    return $false
}

function Get-PackageClosure([string[]]$Packages) {
    $closure = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $pending = [System.Collections.Generic.Stack[string]]::new()
    foreach ($package in $Packages) { $pending.Push($package) }
    while ($pending.Count -gt 0) {
        $package = $pending.Pop()
        if (-not $closure.Add($package)) { continue }
        foreach ($reference in @($packageRefs[$package])) { if ($reference) { $pending.Push($reference) } }
    }
    return , $closure
}

function Get-FamilyClosure([string]$Family) {
    $projects = @(Get-ChildItem -File -Path "framework/components/$Family/*/Leistd.*.csproj" -ErrorAction SilentlyContinue | ForEach-Object BaseName)
    return , (Get-PackageClosure $projects)
}

# 返回违规列表（"行号: 标识符 → 定义包"）。正文与自检共用同一实现。
function Test-DocClosure([string]$Text, [System.Collections.Generic.HashSet[string]]$Closure) {
    $violations = @()
    $lines = $Text -split "`r?`n"
    $blocks = @()
    $section = $null
    $current = $null
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($null -ne $current) {
            if ($line.Trim().StartsWith('```')) { $blocks += $current; $current = $null }
            else { $current.Lines += [PSCustomObject]@{ Number = $index + 1; Text = $line } }
            continue
        }
        if ($line -match '^\s*```\s*(csharp|cs|c#)\s*$') { $current = [PSCustomObject]@{ Section = $section; Lines = @() }; continue }
        if ($line -match '^\s*```') { $current = [PSCustomObject]@{ Section = '__other'; Lines = @() }; continue }
        if ($line -match '^##\s+(.+?)\s*$') { $section = $Matches[1] }
    }
    $blocks = @($blocks | Where-Object Section -ne '__other')
    # 去掉注释与字符串，只看代码里的标识符。
    $code = { param($t) [regex]::Replace(($t -replace '//.*$', ''), '\$?@?"(?:\\.|[^"\\])*"', '""') }
    $declared = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($block in $blocks) {
        foreach ($entry in $block.Lines) {
            foreach ($m in [regex]::Matches((& $code $entry.Text), '\b(?:class|record|struct|interface|enum)\s+([A-Za-z_]\w*)')) { [void]$declared.Add($m.Groups[1].Value) }
        }
    }
    foreach ($block in $blocks) {
        foreach ($entry in $block.Lines) {
            $text = & $code $entry.Text
            if ($text -match '^\s*using\s') { continue }
            $tokens = @([regex]::Matches($text, '\b[A-Z]\w*\b') | ForEach-Object Value | Sort-Object -Unique)
            # 宿主组合行：`## 注册` 段里调用兄弟组件的 Add*/Use*/Map*，整行豁免（§5.1）。
            $callsSibling = $block.Section -eq '注册' -and @($tokens | Where-Object {
                $entryOwners.ContainsKey($_) -and -not @($entryOwners[$_] | Where-Object { $Closure.Contains($_) }).Count
            }).Count -gt 0
            if ($callsSibling) { continue }
            foreach ($token in $tokens) {
                if ($declared.Contains($token)) { continue }
                $owners = if ($typeOwners.ContainsKey($token)) { $typeOwners[$token] } elseif ($extensionOwners.ContainsKey($token)) { $extensionOwners[$token] } else { $null }
                if (-not $owners) { continue }
                if (@($owners | Where-Object { $Closure.Contains($_) }).Count -eq 0) {
                    $violations += "{0}: ``{1}`` → {2}" -f $entry.Number, $token, ($owners -join '/')
                }
            }
        }
    }
    return $violations
}

# 闭包规则自检：用本次索引里真实的兄弟入口与类型构造正反例。
$selfFamily = Get-FamilyClosure 'email'
foreach ($name in 'AddRealTimeSignalR', 'RealTimeHub', 'IEmailSender', 'EnableCreationAuditing') {
    if (-not ($extensionOwners.ContainsKey($name) -or $typeOwners.ContainsKey($name))) { throw "文档闭包规则自检缺少索引项: $name" }
}
if (@($typeOwners['RealTimeHub'] + $entryOwners['AddRealTimeSignalR'] + $extensionOwners['EnableCreationAuditing'] | Where-Object { $selfFamily.Contains($_) }).Count) {
    throw '文档闭包规则自检前提失效：realtime 已进入 email 家族闭包，请换一个兄弟组件。'
}
$fence = '```'
$closureCases = @(
    @{ Name = '注册段调用兄弟入口豁免'; Expected = 0; Text = "## 注册`n$fence" + "csharp`nbuilder.Services.AddRealTimeSignalR();`n$fence" },
    @{ Name = '使用段调用兄弟入口'; Expected = 1; Text = "## 使用`n$fence" + "csharp`nbuilder.Services.AddRealTimeSignalR();`n$fence" },
    @{ Name = '注册段调用兄弟的非注册扩展'; Expected = 1; Text = "## 注册`n$fence" + "csharp`nChangeTracker.EnableCreationAuditing(serviceProvider);`n$fence" },
    @{ Name = '注册段使用兄弟类型'; Expected = 1; Text = "## 注册`n$fence" + "csharp`nbuilder.Services.AddSingleton<RealTimeHub>();`n$fence" },
    @{ Name = '本家族类型'; Expected = 0; Text = "## 使用`n$fence" + "csharp`npublic class X(IEmailSender sender);`n$fence" },
    @{ Name = '注释与字符串不计'; Expected = 0; Text = "## 使用`n$fence" + "csharp`nvar name = `"RealTimeHub`"; // RealTimeHub`n$fence" },
    @{ Name = '代码块自声明类型'; Expected = 0; Text = "## 使用`n$fence" + "csharp`npublic class RealTimeHub;`nvar hub = new RealTimeHub();`n$fence" },
    @{ Name = '非 C# 代码块不计'; Expected = 0; Text = "## 使用`n$fence" + "bash`nAddRealTimeSignalR RealTimeHub`n$fence" }
)
$failedClosure = @($closureCases | Where-Object { @(Test-DocClosure $_.Text $selfFamily).Count -ne $_.Expected } | ForEach-Object Name)
if ($failedClosure.Count -gt 0) { throw "文档闭包规则自检失败: $($failedClosure -join ', ')" }
Write-Host "✅ 文档闭包规则自检通过（$($closureCases.Count) 例）。" -ForegroundColor Green

# 自检与正文扫描使用同一份本次源码索引；规则失效时不继续给出正文通过。
$cases = @(
    @{ Reference = 'Leistd.Core'; Expected = $true },
    @{ Reference = 'Leistd.Timing'; Expected = $true },
    @{ Reference = 'Leistd.Timing.IClock'; Expected = $true },
    @{ Reference = 'Leistd.Timing.IClock.Now'; Expected = $true },
    @{ Reference = 'Leistd.Core.Timing.IClock'; Expected = $false },
    @{ Reference = 'Leistd.Response.Wrappers.Result.Details'; Expected = $false }
)
$failed = @($cases | Where-Object { (Test-QualifiedReference $_.Reference) -ne $_.Expected })
$memberCases = @(
    @{ Reference = 'IClock.Now'; Expected = $true },
    @{ Reference = 'Result.Details'; Expected = $false }
)
$failedMembers = @($memberCases | Where-Object { (Test-MemberReference $_.Reference) -ne $_.Expected })
if ($failed.Count -gt 0 -or $failedMembers.Count -gt 0) {
    throw "文档 API 规则自检失败: $(@($failed.Reference) + @($failedMembers.Reference) -join ', ')"
}
Write-Host "✅ 文档 API 规则自检通过（$($cases.Count + $memberCases.Count) 例）。" -ForegroundColor Green

# 已知非 Leistd 自有的标识符白名单（.NET BCL / EF Core / ASP.NET / 第三方）。
# 这些即使文档用反引号包裹也不校验——它们本就不该在框架源码里定义。
$allow = @(
    # BCL / 常用
    'DateTime','TimeSpan','Guid','Task','ValueTask','CancellationToken','IDisposable','IAsyncDisposable','FakeTimeProvider',
    'TimeZoneInfo','TryFindSystemTimeZoneById','HasIanaId',   # 时区值域判定：设置组件文档要求宿主只收 IANA 标识
    'IEnumerable','IReadOnlyList','IReadOnlyCollection','IList','List','IDictionary','Dictionary','ConcurrentDictionary',
    'IQueryable','Func','Action','Nullable','Exception','ArgumentNullException','InvalidOperationException',
    'OperationCanceledException','TimeoutException','HttpRequestException','ValidationException','String','Boolean','Int32',
    'Assembly','Type','ClaimsPrincipal','ClaimTypes','IServiceProvider','IServiceCollection','IServiceScopeFactory',
    'AddValidation',   # ASP.NET Core Minimal API 内置校验：分页契约的文档要求宿主启用它
    'JsonHttpResult',   # ASP.NET Core 的结果类型：响应包装文档要说明它不被改写
    'LoggingOptions',   # Microsoft.Extensions.Http.Diagnostics：服务客户端文档给出官方正文日志的入口
    # EF Core
    'DbContext','DbContextOptions','DbContextOptionsBuilder','DbSet','ModelBuilder','EntityEntry','ChangeTracker',
    'EntityState','SaveChangesInterceptor','IEntityTypeConfiguration','IServiceProviderFactory',
    'UseHiLo','UseSequence',   # EF Core 的客户端主键预分配：整形键也能在 Add 时就有值，unit-of-work 文档据此给取舍
    # ASP.NET
    'UseForwardedHeaders','ForwardedHeadersOptions','KnownProxies','KnownIPNetworks',
    'OptionsValidationException','IValidateOptions',
    'IExceptionHandler','AddExceptionHandler','AuthorizationPolicyBuilder','DefaultAuthorizationPolicyProvider','ProblemDetails',
    'IExceptionHandlerFeature',   # 操作记录文档要说明它保留了端点与路由值、却保留不了租户作用域
    'AuthenticateAsync','IClaimsTransformation','PolicyEvaluator',
    'ValidationProblemDetails','IProxyGenerator',   # IProxyGenerator=Castle 类型：文档不该引它，但它不是 Leistd 自有，靠源码存在性判定
    # 第三方基类（Castle / Mapster / AutoMapper）
    'AsyncInterceptorBase','IMapperConfigurationExpression','TypeAdapterConfig','Profile','IInterceptor',
    'AddProfile','CreateMap',
    # Polly（service-client 文档说明宿主弹性管道的超时异常如何被归类）
    'TimeoutRejectedException',
    # Refit（service-client 文档引用的第三方类型/特性；AttachmentName 为已废弃特性，文档明确禁用）
    'StreamPart','ByteArrayPart','FileInfoPart','ApiResponse','ApiException','RefitSettings',
    'BodySerializationMethod','AttachmentName',
    # BCL / LINQ / MSBuild 概念（散文中合法出现的非 Leistd 词）
    'GetHashCode','FormatException','SingleOrDefault','ProjectReference','EmbeddedResource','Debug',
    # 本地化：BCL/ASP.NET 本地化抽象（Leistd 在其上实现，非自有类型）
    'IStringLocalizer','IStringLocalizerFactory','ResourceManagerStringLocalizerFactory','LocalizedString','CultureInfo','RequestLocalizationOptions',
    # ASP.NET MVC 结果类型（response 文档在对比自动包装边界时会提到）
    'ContentResult','FileResult','StatusCodeResult','ObjectResult',
    # Redis / StackExchange 原语（lock 文档提到底层命令）
    'KeyDelete',
    # ASP.NET Core SignalR 连接选项（notifications 文档说明"令牌过期不会自动断连"时引用）
    'CloseOnAuthenticationExpiration',
    # Microsoft.IdentityModel 令牌校验（multi-tenancy 文档说明子域名部署的 issuer 校验链时引用）
    'TokenValidationParameters','ValidIssuers','IssuerValidator',
    # 文档中的“一族方法”通配/截断写法与状态描述词（非具体 API 名）
    'SetXxx','GetGrantedPermissionsFor','PublishTo','PermissionAttribute','Completed'
)

$fail = @()
$checked = 0

foreach ($glob in $docGlobs) {
    foreach ($doc in Get-ChildItem $glob -File) {
        $text = Get-Content $doc.FullName -Raw
        # 先剥离 ``` 围栏代码块——代码块里的反引号/示例代码不按“文档在描述 API”对待，
        # 且混入会导致行内反引号配对错位（三反引号与行内反引号无法区分）。只在散文上校验行内 `API`。
        $prose = [regex]::Replace($text, '(?s)```.*?```', '')
        # 提取散文里所有 `反引号包裹` 的片段
        $ticks = [regex]::Matches($prose, '`([^`]+)`')
        foreach ($m in $ticks) {
            $inner = $m.Groups[1].Value.Trim()
            if ($inner -cmatch '^Leistd\.' -and -not (Test-QualifiedReference $inner)) {
                $checked++
                $fail += "{0}: 完整限定名 `{1}` 不是现有包、命名空间或公共类型" -f $doc.Name, $inner
                continue
            }
            if (-not (Test-MemberReference $inner)) {
                $checked++
                $fail += "{0}: 成员引用 `{1}` 不属于该公共类型" -f $doc.Name, $inner
                continue
            }
            # 只校验“整体是一个 API 引用”的反引号：类型名 / 类型.成员 / 方法(...) / 泛型<...>。
            # 排除叙述性短语与示例值：含空格、箭头(→/->)、引号、逗号、等号、方括号(特性/索引)的一律跳过——
            # 这些是流程叙述(`Begin → SaveChanges`)或字符串值(`Orders.Read`、`[Authorize(...)]`)，非 API 引用。
            if ($inner -match '[\s→,"''\[\]=]' -or $inner -match '->') { continue }
            # 成员访问 `A.B` 但无方法调用括号 → 可能是示例值(如权限名 `Orders.Read`)而非 API：
            # 这类只有当根类型确实存在于源码时才有校验意义，否则跳过（避免把示例字符串当臆造 API）。
            $isDottedValue = ($inner -match '\.') -and ($inner -notmatch '\(')
            # 从片段里取根标识符：PascalCase 类型名或 I+大写 接口名
            $idMatch = [regex]::Match($inner, '^(I?[A-Z][A-Za-z0-9]+)')
            if (-not $idMatch.Success) { continue }
            $id = $idMatch.Groups[1].Value
            # 跳过：太短、纯大写缩写(HTTP/DTO/API/DI/AOP/EF)、白名单
            if ($id.Length -lt 4) { continue }
            if ($id -cmatch '^[A-Z0-9]+$') { continue }
            if ($allow -contains $id) { continue }
            # 只校验“像 Leistd 自有 API”的：命中源码=真实；未命中=疑似臆造
            $checked++
            # 单词边界匹配，避免子串误判
            $existsInSrc = $srcText -match "\b$([regex]::Escape($id))\b"
            if (-not $existsInSrc) {
                # 点号值形式(如 `Orders.Read`)且根类型不在源码 → 判为示例值，跳过不报；
                # 裸标识符或方法调用形式不存在 → 判为臆造 API，报错。
                if ($isDottedValue) { continue }
                $fail += "{0}: 反引号 API `{1}` 在框架源码中不存在（疑似臆造或已改名）" -f $doc.Name, $id
            }
        }
    }
}

# 组件示例只用本组件闭包里的类型（§5.1）。ddd-struct 文档就是分层示范，不在此列。
foreach ($doc in Get-ChildItem 'framework/docs/components/*.md' -File | Where-Object Name -ne 'README.md') {
    $closureSet = Get-FamilyClosure $doc.BaseName
    if ($closureSet.Count -eq 0) { continue }
    foreach ($violation in Test-DocClosure (Get-Content $doc.FullName -Raw) $closureSet) {
        $fail += "{0}:{1}（不在本家族 csproj 的传递闭包内）" -f $doc.Name, $violation
    }
}

$fail = $fail | Sort-Object -Unique

if ($fail.Count -gt 0) {
    Write-Host "❌ 文档-源码 API 漂移校验失败（共 $($fail.Count) 项，已校验 $checked 个标识符）：" -ForegroundColor Red
    $fail | ForEach-Object { Write-Host "  - $_" }
    Write-Host ""
    Write-Host "处理：确认该 API 是否真实存在于源码；若为第三方/BCL 类型，加入脚本 `$allow 白名单；若文档写错，修正文档。" -ForegroundColor Yellow
    exit 1
}
Write-Host "✅ 文档-源码 API 漂移校验通过（已校验 $checked 个 Leistd 自有标识符，均在源码中存在）。" -ForegroundColor Green
