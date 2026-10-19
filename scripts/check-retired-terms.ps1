#!/usr/bin/env pwsh

<#
.SYNOPSIS
    已废弃符号与表述扫描闸门。

.DESCRIPTION
    功能权限已改为纯加法模型（行的存在即授予，没有"拒绝"这一态），随之删除了一批公开 API。
    文档与注释不会因为类型删除而编译失败，因此漂移只能靠扫描发现——此前就出现过
    随包文档仍在教 `PermissionGrantEffect` 的情况。

    **一条规则值得留在这里，前提是那个错误会复发。**只守着"已经过去且不会回来的事"的规则，
    修完就该删——否则闸门表会一路堆积，每条都有维护成本与误报面，最终没人再看它。
    判据示例：AI 按训练记忆写已删除的 API（会复发，留）；某次移植遗留的跨语言对照
    （一次性，清理完即删除该规则，2026-09-03 已按此删掉 java 规则）。

    三类规则，都刻意收窄，宁可漏也不要吵：
      1. 已删除的符号 —— 名字本身就是证据；
      2. 外部参考框架的痕迹 —— 交付面里不应出现"照 ABP 怎么做"这类指引；
      3. 断言旧模型的**短语** —— 不用裸词。"三态""显式拒绝"在别处是合法的：
         主题切换是亮/暗/跟随系统三态，资源实例授权保留了自己的 ResourceGrantEffect，
         而"没有显式拒绝"这类否定句正是新模型的正确表述。裸词黑名单会把它们一并报掉，
         一个吵闹的闸门很快就会被关掉。

    退出码非 0 表示存在残留，供 CI 阻断。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,

    # 只跑规则自测再退出。规则是正则且刻意收窄过，写错一个边界就会静默不再命中，
    # 从那以后闸门永远是绿的——所以规则本身也需要被测。
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.Generic.List[string]

# 变更日志与历史设计记录必须能提到被删掉的东西，否则就没法记录"删了什么"。
$historicalPaths = @(
    "docs/framework/versioning.md",
    # 按需提供的升级指南允许对照旧名与当前名称：框架版本的，以及已发布模板版本（template-v*）的升级说明。
    # 模板的变更说明本身是 template-v tag 的 Release 正文，不在仓库里；豁免只到这两个升级说明目录，
    # template/ 载荷与 docs/template/ 的其他文档照常扫描。
    "docs/framework/upgrades/",
    "docs/template/upgrades/",
    "docs/plans/",
    "docs/assessments/"
)

# 依据当前契约登记已删除的名称，避免源码和分发文档继续引用。
#
# 用正则而不是子串：好几个被删掉的名字是现存名字的前缀或后缀，直接按子串拦会误伤。
#   \bPermissionGrant\b   —— PermissionGrantSet/Store/Record/Manager 都还活着，必须靠单词边界排除；
#   AddPermission          —— IPermissionGroupDefinition.AddPermission 是现行 API，只能拦带接收者的字面量；
#   PermissionName         —— PermissionGrantRecord.PermissionName 是现行属性，同上；
#   ForUser / ForRole      —— 裸词误伤面太大，只拦 PermissionGrantRecord 上的那两个工厂。
$retiredSymbols = @(
    "\bPermissionGrantEffect\b",
    "\bPermissionGrant\b",
    "\bIsGrantedToUserAsync\b",
    "\bIsGrantedToRoleAsync\b",
    "\bIsGrantedToAnyRoleAsync\b",
    "\bIsGrantedToUserOrRolesAsync\b",
    "\bGetGrantedPermissionsFor\w*\b",
    "\bGrantToUserAsync\b",
    "\bGrantToRoleAsync\b",
    "\bRevokeFromUserAsync\b",
    "\bRevokeFromRoleAsync\b",
    "\bGetEffectiveEffects\b",
    "IPermissionDefinitionContext\.AddPermission",
    "PermissionRequirement\.PermissionName",
    "PermissionGrantRecord\.ForUser",
    "PermissionGrantRecord\.ForRole",
    # 业务异常的 Message 必须安全可展示，技术异常始终回落通用 500；
    # 不再保留抛出点或宿主可见性开关，避免同一错误随配置泄露内部消息。
    "\bAsUserFacing\b",
    "\bIsUserFacingMessage\b",
    "\bFallbackToExceptionMessage\b",
    "\bBusinessMessageExposure\b",
    "\.MessageExposure\b"
)

# 外部参考框架的痕迹。设计时参考过别的框架是正常的，但**交付面里不该留下指向它的引用**：
#
#   - framework/docs/ 与 template/ 是对外分发内容，出现"参考 ABP 的做法"会把使用者引向
#     一份我们并不遵循、也不会同步的外部契约；
#   - docs/ 里的稳定规范同理，读者无法判断哪部分是我们的决定、哪部分是照抄。
#
# 历史 assessment 与 plan 里记录"当时参考了什么"是证据，因此走 $historicalPaths 豁免。
#
# 用单词边界而不是子串：abp 是常见的 base64/哈希片段（package-lock 已排除，但 .md 里的
# 校验和、示例令牌同样会命中），裸子串会把它们一并报掉。
#
# 类型名那条必须显式关掉大小写不敏感（`(?-i:...)`）：PowerShell 的 `-match` 默认忽略大小写，
# 于是 `[A-Z]` 连小写也匹配，`abpx` 这种普通词会被当成 `AbpXxx` 类型名报出来。
# 自测里就有这个样例，别把它删掉。
$foreignFrameworkSymbols = @(
    "\bABP\b",
    "(?-i:\bAbp[A-Z]\w*)",
    "\bVolo\.",
    "abp\.io"
)

# 路线图与升级动作短语。§4.1 把"曾经是什么样、为什么改、升级动作"归给
# docs/framework/versioning.md（仓库内、不分发），把"将来会怎样"排除在随包内容之外：
# 消费者装上某个版本时只关心它现在是什么。
#
# **只作用于分发面**——framework 的源码与随包文档、template 载荷。docs/ 是仓库内部规划场所，
# 谈将来正是它的职责，不受本规则约束。
# 分发面：随 NuGet 包或 dotnet new 出去的内容。docs/ 是仓库内部规划与规范场所，
# 既要能谈将来，也要能引用"对应 Java XXX"这类反例来说明规则本身，因此不在作用域内。
$distributionScopes = @("framework/components", "framework/ddd-struct", "framework/docs", "template/")
$roadmapPhrases = @(
    "后续引入",
    "届时",
    "升级到本版本",
    "未来版本",
    "将来支持"
)

# 待办标记。交付的代码不留"以后再改"：工具（迁移 schematic、代码生成器）留下的待办与自己写的
# 待办一样，要在当期按终局做法改掉——留下来就会被遗忘，也会被读者当成当前仍未完成的约定。
# 作用于 framework/ 与 template/（含测试）；docs/ 里的计划与评估本来就在记录未完成的事。
# 豁免只给第三方生成、按上游原样维护的代码：libs/ui 是 spartan 生成的组件库。
# 只认注释里的大写标记（`//`、`/*`、块注释续行 `*`、`#`、`<!--`、`@*`），以及 Markdown 行首的标记：
# 待办是写给维护者的话，只会出现在注释与说明文字里。字符串与数据里的 'TODO'（任务状态之类）
# 是正常的值，不拦；普通单词（todos、TodoList）靠单词边界排除。
# 注释起始符与标记之间不允许出现引号；URL 里的 `://` 不算注释起始。
$todoScopes = @("framework/", "template/")
$todoExemptPaths = @("template/frontend/libs/ui/")
$todoMarker = '(?-i:(?:(?<!:)//|/\*|<!--|@\*|^\s*\*|^\s*#|^\s*(?:[-*]\s+)?(?=(?:TODO|FIXME|HACK)\b))[^''"`]*?\b(?<marker>TODO|FIXME|HACK)\b)'

# 维护事实不进模板。template/ 是生成项目的载荷，生成项目里没有本仓库的 workflow、仓库根目录与仓库名；
# 注释写"由 .github/workflows/release.yml 替换""仓库根 VERSION"，读者在生成项目里找不到对应物。
# .template.config/ 是模板引擎的配置，不进生成项目，不受约束。
# `leistd-net-framework` 是随框架包分发的 Skill 名，生成项目里确实存在，属于例外（负向断言排除）。
# 生成项目自己的 CI 薄壳 `.github/workflows/ci.yml`（Ci=github）也确实存在：目录本身与 ci.yml 不拦，点名其他 workflow 文件照拦。
$maintenanceFactScope = "template/"
$maintenanceFactExemptPaths = @("template/.template.config/")
$maintenanceFactPatterns = @(
    "\.github/workflows/(?!ci\.yml(?![\w.-]))[\w.-]",
    "仓库根",
    "(?<![\w-])leistd-net(?![\w-])"
)

# 修复轮次不进源码与测试。"N9 的回归守卫""上一轮""（P4）"只对当时的评审有意义，
# 读者既查不到编号指的是什么，也无从判断它是否仍然成立——注释写行为与理由，不写来历。
# 编号区分大小写：小写的 p1)、n2) 是普通参数名。
$roundRecordScopes = @("framework/", "template/backend/")
# "上一轮"本身是普通词（"上一轮抓取""上一轮失败锁定"），只拦它指评审或修复的用法。
$roundRecordPatterns = @(
    "(?-i:\b[NP]\d{1,2}\s*(的|）|\)))",
    "上一轮(评审|审查|审核|修复|修改|改动)",
    "本轮修"
)

# 短语 → 豁免路径片段。资源实例授权那一层保留了 ResourceGrantEffect，
# "显式拒绝优先"在那里是当前正确的描述，不是漂移。
$retiredPhrases = @{
    "三态组合"     = @()
    "三态权限"     = @()
    "无法表达三态" = @()
    "含显式拒绝"   = @()
    "扣除显式拒绝" = @()
    "显式拒绝优先" = @("authorization-resource", "Authorization.Resource", "authorization-data-scope", "Authorization.DataScope")
}

function Test-Historical([string]$RelativePath) {
    # 只按前缀匹配：用 Contains 会把恰好含有相同片段的其他目录一并豁免。
    $normalized = $RelativePath.Replace('\', '/')
    foreach ($fragment in $historicalPaths) {
        if ($normalized -eq $fragment -or $normalized.StartsWith($fragment)) {
            return $true
        }
    }

    return $false
}

if ($SelfTest) {
    # 每条规则一个"应命中"与一个"不应命中"的样例。不应命中的那些全部取自现存 API，
    # 它们正是把子串换成正则的原因。
    $cases = @(
        @{ Rule = "symbol"; Text = "new PermissionGrant(name, effect)";               ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "var set = new PermissionGrantSet(...);";          ShouldMatch = $false }
        @{ Rule = "symbol"; Text = "PermissionGrantRecord.PermissionName";            ShouldMatch = $false }
        @{ Rule = "symbol"; Text = "PermissionRequirement.PermissionName";            ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "usersGroup.AddPermission(name, displayName)";     ShouldMatch = $false }
        @{ Rule = "symbol"; Text = "IPermissionDefinitionContext.AddPermission";      ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "PermissionGrantRecord.ForUser(...)";              ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "record.ForUserDisplay";                           ShouldMatch = $false }
        @{ Rule = "symbol"; Text = "grants.GetEffectiveEffects(definitions)";         ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "grants.GetGrantedNames()";                        ShouldMatch = $false }
        @{ Rule = "symbol"; Text = "PermissionGrantEffect.Granted";                   ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "ResourceGrantEffect.Granted";                     ShouldMatch = $false }
        @{ Rule = "symbol"; Text = ".AsUserFacing()";                                ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "FallbackToExceptionMessage = true";              ShouldMatch = $true }
        @{ Rule = "symbol"; Text = "options.MessageExposure = All";                  ShouldMatch = $true }
        @{ Rule = "phrase"; Text = "任一来源三态组合后取并集";                        ShouldMatch = $true }
        @{ Rule = "phrase"; Text = "亮/暗/跟随系统三态";                              ShouldMatch = $false }
        @{ Rule = "foreign"; Text = "参考 ABP 的多租户实现";                          ShouldMatch = $true }
        @{ Rule = "foreign"; Text = "using Volo.Abp.MultiTenancy;";                   ShouldMatch = $true }
        @{ Rule = "foreign"; Text = "详见 https://abp.io/docs";                       ShouldMatch = $true }
        @{ Rule = "foreign"; Text = "AbpTenantResolver 的形态";                       ShouldMatch = $true }
        @{ Rule = "foreign"; Text = "sha512-4fjYABPvFnQ7iyaBPKKS9";                   ShouldMatch = $false }
        @{ Rule = "foreign"; Text = "labpartner 与 abpx 都是普通单词";                 ShouldMatch = $false }
        @{ Rule = "roadmap"; Text = "以及后续引入分布式权限缓存时的失效信号";                ShouldMatch = $true }
        @{ Rule = "roadmap"; Text = "需把存储改成 1:N，届时第 3、4 级按名字查";              ShouldMatch = $true }
        @{ Rule = "roadmap"; Text = "升级到本版本需要一次 EF 迁移";                        ShouldMatch = $true }
        @{ Rule = "roadmap"; Text = "当前每租户一条配置，业务 DbContext 解析到 Default";     ShouldMatch = $false }
        @{ Rule = "todo"; Text = "// TODO: vitest-migration: Please migrate manually.";           ShouldMatch = $true }
        @{ Rule = "todo"; Text = "/// FIXME 以后再处理";                                         ShouldMatch = $true }
        @{ Rule = "todo"; Text = "// HACK: 临时绕过";                                           ShouldMatch = $true }
        @{ Rule = "todo"; Text = "const todos = listTodoItems();";                              ShouldMatch = $false }
        @{ Rule = "todo"; Text = "export class TodoList {}";                                    ShouldMatch = $false }
        @{ Rule = "todo"; Text = "/* TODO: 拆分 */";                                           ShouldMatch = $true; Marker = "TODO" }
        @{ Rule = "todo"; Text = " * FIXME 边界未处理";                                          ShouldMatch = $true; Marker = "FIXME" }
        @{ Rule = "todo"; Text = "# TODO 换成正式镜像";                                          ShouldMatch = $true }
        @{ Rule = "todo"; Text = "<!-- TODO: 补截图 -->";                                        ShouldMatch = $true }
        @{ Rule = "todo"; Text = "@* HACK 临时样式 *@";                                          ShouldMatch = $true }
        @{ Rule = "todo"; Text = "TODO: 补充部署步骤";                                           ShouldMatch = $true }
        @{ Rule = "todo"; Text = "- TODO 补充部署步骤";                                          ShouldMatch = $true }
        @{ Rule = "todo"; Text = "expect(task.status).toBe('TODO');";                          ShouldMatch = $false }
        @{ Rule = "todo"; Text = '{ "status": "TODO" }';                                        ShouldMatch = $false }
        @{ Rule = "todo"; Text = "const next = Status.TODO;";                                   ShouldMatch = $false }
        @{ Rule = "todo"; Text = 'var url = "http://host/TODO";';                              ShouldMatch = $false }
        @{ Rule = "maintenance"; Text = "由 .github/workflows/release.yml 替换版本号";               ShouldMatch = $true }
        @{ Rule = "maintenance"; Text = "由 .github/workflows/ci.yml.bak 替换";                      ShouldMatch = $true }
        @{ Rule = "maintenance"; Text = "附带的 ``.github/workflows/ci.yml`` 调用 verify";          ShouldMatch = $false }
        @{ Rule = "maintenance"; Text = "|-- .github/workflows/       # CI 薄壳";                  ShouldMatch = $false }
        @{ Rule = "maintenance"; Text = "版本号来自仓库根 VERSION";                                 ShouldMatch = $true }
        @{ Rule = "maintenance"; Text = "源码位于 leistd-net 仓库";                                  ShouldMatch = $true }
        @{ Rule = "maintenance"; Text = "使用 leistd-net-framework Skill 定位包内文档";              ShouldMatch = $false }
        @{ Rule = "maintenance"; Text = "部署后按镜像 revision 标签确认版本";                         ShouldMatch = $false }
        @{ Rule = "round"; Text = "// N9 的回归守卫";                                               ShouldMatch = $true }
        @{ Rule = "round"; Text = "// 补上（P4）发现的缺口";                                         ShouldMatch = $true }
        @{ Rule = "round"; Text = "// 修复 P12) 之后";                                               ShouldMatch = $true }
        @{ Rule = "round"; Text = "// 上一轮评审遗漏的分支";                                         ShouldMatch = $true }
        @{ Rule = "round"; Text = "// 晚于上一轮抓取完成才取得工厂所有权";                           ShouldMatch = $false }
        @{ Rule = "round"; Text = "// 本轮修正了排序";                                               ShouldMatch = $true }
        @{ Rule = "round"; Text = "Assert.Equal(expected, Compute(p1));";                           ShouldMatch = $false }
        @{ Rule = "round"; Text = "var n2 = Next(n1);";                                             ShouldMatch = $false }
        @{ Rule = "round"; Text = "// HTTP2) 与 P4-27 这类带连字符的编号不是轮次短语";              ShouldMatch = $false }
        # 豁免路径：只有升级说明目录；模板载荷与模板维护文档照常扫描
        @{ Rule = "historical"; Text = "docs/template/upgrades/0.2.0.md";                           ShouldMatch = $true }
        @{ Rule = "historical"; Text = "docs/framework/upgrades/0.13.0.md";                         ShouldMatch = $true }
        @{ Rule = "historical"; Text = "docs/template/quality-assurance.md";                        ShouldMatch = $false }
        @{ Rule = "historical"; Text = "template/docs/upgrades/0.2.0.md";                           ShouldMatch = $false }
        @{ Rule = "historical"; Text = "docs/template/upgrades-notes.md";                           ShouldMatch = $false }
    )

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($case in $cases) {
        $matched = $false

        if ($case.Rule -eq "symbol") {
            foreach ($symbol in $retiredSymbols) {
                if ($case.Text -match $symbol) { $matched = $true; break }
            }
        }
        elseif ($case.Rule -eq "foreign") {
            foreach ($symbol in $foreignFrameworkSymbols) {
                if ($case.Text -match $symbol) { $matched = $true; break }
            }
        }
        elseif ($case.Rule -eq "todo") {
            $matched = $case.Text -match $todoMarker
            # 诊断信息要能说出命中的是哪个标记
            if ($matched -and $case.Marker -and $Matches['marker'] -ne $case.Marker) {
                $failures.Add("[todo] ""$($case.Text)"" 命中的标记是 '$($Matches['marker'])'，应为 '$($case.Marker)'")
            }
        }
        elseif ($case.Rule -eq "maintenance") {
            foreach ($pattern in $maintenanceFactPatterns) {
                if ($case.Text -match $pattern) { $matched = $true; break }
            }
        }
        elseif ($case.Rule -eq "round") {
            foreach ($pattern in $roundRecordPatterns) {
                if ($case.Text -match $pattern) { $matched = $true; break }
            }
        }
        elseif ($case.Rule -eq "historical") {
            $matched = Test-Historical $case.Text
        }
        elseif ($case.Rule -eq "roadmap") {
            foreach ($phrase in $roadmapPhrases) {
                if ($case.Text.Contains($phrase)) { $matched = $true; break }
            }
        }
        else {
            foreach ($phrase in $retiredPhrases.Keys) {
                if ($case.Text.Contains($phrase)) { $matched = $true; break }
            }
        }

        if ($matched -ne $case.ShouldMatch) {
            $expectation = if ($case.ShouldMatch) { "应命中但没命中" } else { "不应命中却命中了" }
            $failures.Add("[$($case.Rule)] `"$($case.Text)`" $expectation")
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host "规则自测失败：" -ForegroundColor Red
        foreach ($failure in $failures) { Write-Host "  $failure" -ForegroundColor Red }
        exit 1
    }

    Write-Host "✅ 规则自测通过（$($cases.Count) 个样例）。" -ForegroundColor Green
    exit 0
}

# 清单取 Git 已跟踪与未跟踪但未忽略的文件：新建未提交的文件照样被扫，node_modules 等忽略目录不必先遍历再过滤，
# .agents 这类隐藏目录也不会漏掉。也扫 .html 与 .json：页面文案和 i18n 词条同样会讲解旧模型。
$listed = @(git -C $RepoRoot -c core.quotepath=off ls-files --cached --others --exclude-standard -- framework template docs)
if ($LASTEXITCODE -ne 0) { throw "git ls-files 失败，无法确定扫描范围。" }
# 既有规则只扫源码、文档与词条；维护事实与修复轮次另扫构建文件（props、csproj），
# 维护事实再加部署与脚本文件（yml、ps1、py、Dockerfile 等）——这两类表述最常出现在那里的注释里。
$sourceExtensions = '\.(cs|ts|md|html|json)$'
$buildExtensions = '\.(props|targets|csproj)$'
$files = $listed | Where-Object {
    ($_ -match $sourceExtensions -or $_ -match $buildExtensions -or
     $_ -match '\.(mjs|ya?ml|ps1|py|sh|example)$' -or $_ -match '(^|/)Dockerfile$') -and
    $_ -notmatch '(^|/)(bin|obj|node_modules|dist|\.angular)/' -and
    $_ -notlike '*package-lock.json' -and
    (Test-Path -LiteralPath (Join-Path $RepoRoot $_) -PathType Leaf)
}

$scanned = 0
foreach ($relative in $files) {
    if (Test-Historical $relative) { continue }

    $scanned++
    $isSource = $relative -match $sourceExtensions
    $isBuild = $relative -match $buildExtensions
    # 强制成数组：单行文件下 Get-Content 返回字符串，按下标取到的是字符而不是整行。
    $lines = @(Get-Content -LiteralPath (Join-Path $RepoRoot $relative) -Encoding UTF8)

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]

        foreach ($symbol in $(if ($isSource) { $retiredSymbols } else { @() })) {
            if ($line -match $symbol) {
                $problems.Add("$relative`:$($index + 1) 引用了已删除的符号 '$symbol'")
            }
        }

        foreach ($symbol in $(if ($isSource -or $isBuild) { $foreignFrameworkSymbols } else { @() })) {
            if ($line -match $symbol) {
                $problems.Add("$relative`:$($index + 1) 引用了外部参考框架 '$symbol'")
            }
        }

        $normalizedPath = $relative.Replace('\', '/')
        $inDistribution = $false
        foreach ($scope in $distributionScopes) {
            if ($normalizedPath.StartsWith($scope)) { $inDistribution = $true; break }
        }

        if ($inDistribution -and $isSource) {
            foreach ($phrase in $roadmapPhrases) {
                if ($line.Contains($phrase)) {
                    $problems.Add("$relative`:$($index + 1) 分发面出现路线图/升级动作表述 '$phrase'；归 docs/framework/versioning.md")
                }
            }
        }

        $inTodoScope = $false
        foreach ($scope in $todoScopes) {
            if ($normalizedPath.StartsWith($scope)) { $inTodoScope = $true; break }
        }
        foreach ($exemptPath in $todoExemptPaths) {
            if ($normalizedPath.StartsWith($exemptPath)) { $inTodoScope = $false; break }
        }
        if ($normalizedPath.StartsWith($maintenanceFactScope) -and
            -not ($maintenanceFactExemptPaths | Where-Object { $normalizedPath.StartsWith($_) })) {
            foreach ($pattern in $maintenanceFactPatterns) {
                if ($line -match $pattern) {
                    $problems.Add("$relative`:$($index + 1) 模板载荷写了本仓库的维护事实 '$($Matches[0])'；生成项目里没有它，改写为生成项目视角")
                }
            }
        }

        if (($isSource -or $isBuild) -and ($roundRecordScopes | Where-Object { $normalizedPath.StartsWith($_) })) {
            foreach ($pattern in $roundRecordPatterns) {
                if ($line -match $pattern) {
                    $problems.Add("$relative`:$($index + 1) 记录了修复轮次 '$($Matches[0])'；注释写行为与理由，不写来历")
                }
            }
        }

        if ($isSource -and $inTodoScope -and $line -match $todoMarker) {
            $problems.Add("$relative`:$($index + 1) 留下了待办标记 '$($Matches['marker'])'；在当期按终局做法完成，不留待办")
        }

        foreach ($phrase in $(if ($isSource) { $retiredPhrases.Keys } else { @() })) {
            if (-not $line.Contains($phrase)) { continue }

            $exempt = $false
            foreach ($fragment in $retiredPhrases[$phrase]) {
                if ($relative.Replace('\', '/').Contains($fragment)) { $exempt = $true; break }
            }

            if (-not $exempt) {
                $problems.Add("$relative`:$($index + 1) 使用了旧模型的表述 '$phrase'")
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "发现已废弃的符号或表述：" -ForegroundColor Red
    foreach ($problem in $problems) {
        Write-Host "  $problem" -ForegroundColor Red
    }
    exit 1
}

Write-Host "✅ 废弃符号/外部框架痕迹扫描通过（已扫描 $scanned 个文件）。" -ForegroundColor Green
