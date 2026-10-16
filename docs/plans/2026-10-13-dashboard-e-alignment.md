# 仪表盘对齐 E 方案实施计划

范围：模板平台仪表盘（`template/` 前端、统计接口、Mock）按对比页「Leistd 界面改版对比」E 档补齐细节；统计接口改为"日历区间 + 生效时区"；框架操作记录组件的按日计数改为按调用方给定的 UTC 桶边界计数。全部任务完成并验收后删除本计划，有效结论上收到稳定文档。

方案来源：E 档原型源码（artifact `ED6nTqzZGdZ9zmf95roa9P`，本地副本 `.tmp/e-compare-artifact.html`）的 `dashC()`、`areaChart()`、`spark()`、`OPS`、`AD_NOTES.E` 与 `.mk.e` 样式。`docs/assessments/2026-10-12-ui-review` 的"全面优化"静态图不是 E；上一轮按它把失败线改成浅蓝、曲线改平滑，属于参照错误，由 T0 撤回。

修订记录：第一版经 Codex 会话审查提出 7 项重要问题与 4 项建议，第二版逐条吸收（【I1】…【S4】）；复审提出 1 项重要与 3 项建议，第三版吸收（【N1】…【N4】）。

## 评审依据

- 设计原则 `docs/architecture/design-principles.md` §5：终局不留中间态、优先官方机制、不过度设计。
- 框架：`docs/framework/development-guide.md`、`docs/framework/versioning.md`、`framework/docs/components/core.md`（UTC 持久化与传递；不能用宿主 `TimeZoneInfo.Local` 代替业务时区）、`framework/docs/components/operation-records.md`。
- 模板：`docs/template/development-guide.md`；`template/docs/standards/` 的 `api.md`、`coding-backend.md`、`coding-frontend.md` §9、`frontend-ui.md`、`frontend-spartan.md`、`testing.md`。
- Spartan 官方 Skill：只用语义令牌；能用样式表解决的不改 helm，改 helm 的登记在 `frontend-spartan.md`。
- 评审取舍：终局但不过度设计；不过度考虑安全；权衡复杂度；开发者视角；组件通用性。

## 已定决策（用户确认）

1. 区间：今天 / 7 天 / 30 天分段 + 自定义区间（官方 `HlmDateRangePicker`），不做导出；自定义最长 366 个包含日，超过 31 天默认按周。
2. 趋势图按 E：成功 `--chart-1`、失败 `--chart-3` 琥珀、直线、成功渐变面积、2px 圆角方块图例。
3. 时区：存储与比较一律 UTC；统计请求传日历日期 + 前端实际生效的展示时区，后端一处换算、用同一时区生成桶；响应时刻按 UTC 传输并回显时区。

## 现状事实

| # | 事实 | 出处 | 影响 |
| --- | --- | --- | --- |
| F1 | 时间列均为 `timestamp with time zone`，写入经 `IClock.Now`（UTC） | 迁移快照、`coding-backend.md` | 存储侧不改 |
| F2 | 前端展示时区：`Display.TimeZone` 能被当前 `Intl` 解析则用它，否则回落浏览器时区（`app-date-pipe.ts:75–80`）；操作记录页把选中日期换算成 UTC 起止再传 | `coding-frontend.md` §9、`zoned-time.ts`、`operation-records.ts:437` | 统计请求的时区须复用同一回落【I5】 |
| F3 | `UserTimeZoneProvider` 空值回落 `TimeZoneInfo.Local`，违背 `core.md`；业务代码目前只用其 `IsValidId` | `UserTimeZoneProvider.cs:21`、`SettingDefinitionProvider.cs:58–66` | 回落改 UTC；统计不调用 `GetAsync`【3.3】 |
| F4 | 统计接口 `?days=7|30`，UTC 日分组；`totalUsers.value` 是此刻存量、`previous` 是本期起点存量；无操作记录形态的活跃用户只判 `LastLogin.Time >= start` | `PlatformStatisticsAppService.cs:59–60,107–118` | 口径按新契约重定义【I3】【I4】 |
| F5 | `CountByDayAsync`、`CountDistinctTargetsAsync`、`OperationRecordCountFilter` 只在本分支（`09a74648`）；`OperationRecordFilter` 已发布，但用户指示按终局改名 | `git branch -r --contains`、全仓检索 | 直接替换，不留兼容层 |
| F6 | 框架操作记录 EF 测试用 SQLite，项目明确不用 InMemory 验证翻译 | `Leistd.OperationRecords.Tests.csproj:5–8`、`OperationRecordCountTests.cs` | T1 用 PostgreSQL + SQLite 验证【I7】 |
| F7 | 匿名请求中非自证动作的操作人为空（如匿名登录失败）；机器主体有 `client:<id>`；`outcome` 只有 `succeeded`/`failed`；前端 `actorOf()` 按 `actorIsTarget` 回落并展示模拟登录人 | `OperationRecorder.cs:111–122`、`operation-record-table.ts:168–174` | 空操作人显示"匿名"，结果两态【I6】 |
| F8 | `hlm-badge` 有登记的 `success` 变体，无 `warning` | `frontend-spartan.md` | 新增 `warning` 变体并登记 |
| F9 | Spartan 提供 `HlmDateRangePicker`；锁定的 `@tanstack/charts@0.16.0` 支持 `guides: false`、`margin: 0` 画无轴迷你图，网格只有 boolean、无虚线配置 | Spartan 文档、随包类型 | 迷你图用官方配置；网格保持实线【S3】 |
| F10 | 统计控制器是 `[ApiController]` + `[FromQuery]` DTO，不是 Minimal API | `PlatformStatisticsController.cs:17–19` | 必填用 MVC 机制【S1】 |
| F11 | 工作区有上一轮未提交改动：深色品牌面板、首页同心细环、产品截图、图表蓝色深浅与平滑曲线（含 `d3-shape`） | `git status` | T0 拆分 |

## 接口契约

`GET /api/v1/platform/statistics?startDate=2026-10-07&endDate=2026-10-13&timeZone=Asia/Shanghai`

### 请求校验【I2】【S1】【S4】

- 三个参数必填：`DateOnly?` + `[Required]`（MVC 模型绑定），缺失、空值、非法日期由现有 ProblemDetails 字段错误返回。
- 时区按 `IUserTimeZoneProvider.IsValidId` 同一判定；非法返回字段 400，不静默替换。
- 跨字段规则作为请求结构校验，走同一字段错误通道：`startDate ≤ endDate`；`endDate ≤ 该时区的今天`；包含日数 `N = endDate.DayNumber − startDate.DayNumber + 1 ≤ 366`；日期前移与次日运算不得溢出。`api.md` §2.5 补"区间合法性属于请求结构校验"的澄清，不把业务规则整体改成字段错误。
- 一个真实 HTTP 测试固定 400 的响应形状；Mock 按同一形状返回，不加顶层业务 code。

### 时间换算【I1】【I2】【3.2】

- 后端只读取一次 `IClock.Now`。
- **本地日起点**：该日期在时区内的第一个时刻；本地 00:00 不存在时取跳时后的第一个时刻，歧义时取较早的那个。
- **本期实际区间**：`[startDate 起点, min(endDate 次日起点, now))` 的 UTC 半开区间。
- **上期**：按日历日前移 N 日比较相同的本地进度，不减 `N × 24h`。完整历史区间：`[startDate−N 起点, endDate−N 次日起点)`；含今天的区间：上期终点为 `now` 的本地日期时间前移 N 日后换算的时刻（不存在则取跳时后第一个时刻，歧义取较早）。
- **粒度**：单日（`N = 1`）按小时，其余按日；按周由前端聚合可加序列。
- **桶边界**：日桶边界为各本地日起点；小时桶从本地日起点起按 UTC 每小时步进，末桶截到当日终点。因此纽约回拨日有 25 个桶、两个本地 01 点分别成桶，春令日 23 个，Lord Howe 半小时夏令时日末桶为半小时。
- **只返回与本期实际区间相交的桶**：当前末桶为部分桶，不为未来小时补 0。
- **零长度区间**【N2】：在今天零点请求时本期 `start == end`，应用层直接短路：事件类总数为 0、`buckets` 与各 `series` 为空，不调用按桶计数；存量按截止时刻照常计算。上期为零长度时同样处理。框架的边界校验不放宽。

### 响应【I3】【I4】【3.1】【4】

- `range`：`startDate`、`endDate`、`timeZone`、`granularity`（`hour`/`day`）、`start`、`end`、`previousStart`、`previousEnd`（后四项为 UTC 时刻）。
- `buckets`：各桶起点的 UTC 时刻，严格升序且唯一；最后一桶的终点为 `range.end`；所有 `series` 与之等长。
- KPI 均为 `{ value, previous?, series? }`：

| 指标 | value | previous | series | 可按周相加 |
| --- | --- | --- | --- | --- |
| `totalUsers` | 本期实际终点前的存量（创建早于终点且未在终点前删除） | 上期终点前的存量 | 各桶终点前的存量 | 否 |
| `newUsers` | 本期内创建数（之后删除的照计） | 上期内创建数 | 各桶创建数 | 是 |
| `activeUsers`（有操作记录） | 本期内登录成功的去重用户数 | 上期同口径 | 各桶内去重数 | 否 |
| `activeUsers`（无操作记录） | 最近一次登录落在本期内的现存用户数 | 不给 | 不给 | — |
| `failedLogins` | 本期登录失败次数 | 上期次数 | 各桶次数 | 是 |

- `loginTrend`：`succeeded[]`、`failed[]`（与 `buckets` 等长）、`succeededTotal`、`previousSucceededTotal`；仍受 `LocalIdentity && IncludeOperationRecords` 裁剪。
- 不可获取的指标按现有条件裁剪缺省，不用 0 冒充；上期为 0 时沿用"无可比数据"规则。
- `attention` 不变。

## 任务分解

### T0 拆分在途改动并撤回参照错误

- 撤回：`--chart-2/3` 恢复改动前的模板值（亮 `0.6 0.11 195` / `0.66 0.15 70`，暗 `0.72 0.11 195` / `0.8 0.14 75`）。原型 `.mk` 亮色 `--chart-3` 为 `0.76 0.14 75`，但模板值是按卡片上 ≥ 3:1 核算过的，保留核算结果、色相差 5° 不改【S3】。同时撤回：注释、`d3Curve` 与平滑曲线、`d3-shape` 依赖（两份 `package.json` 与锁文件），图例恢复 `bg-chart-3`，`frontend-ui.md` 图表规则去掉深浅三档的表述。渐变面积保留。
- 保留并单独提交：深色品牌面板、首页同心细环与对应规范；产品截图等 T5 定稿后重拍。
- 验证：受影响形态（全功能、无本地化）的前端构建、lint、单测；`check-all.ps1`。

### T1 先行验证：按桶计数的实现（达标即继续，不达标停下回报）【I7】【3.4】

- 首选 A：由调用方给出有序 UTC 桶边界，数据库用一条语句按边界计数（CASE 桶下标 + GroupBy，复用 `CountedRecordsAsync` 的主表 + 归档投影与筛选），支持按目标去重（排除 `NoTargetId`）。
- 验证：真实 PostgreSQL 与现有 SQLite 测试路径；检查生成的 SQL 为单条语句、无逐桶查询、不拉明细；366 桶（367 个边界）、10 万条记录的耗时；去重的翻译【N3】。
- B（Npgsql 的 `TimeZoneInfo.ConvertTimeBySystemTimeZoneId` 翻译为 `AT TIME ZONE`）只在 PostgreSQL 上作性能对照，不作为通用存储的默认路径。
- 判据：A 在两种数据库上翻译成功、结果正确、366 桶（367 个边界）耗时可接受（同量级于现有分页查询）；结论与 SQL 摘要写进 T2 的提交说明。

### T2 框架：操作记录按 UTC 桶边界计数（用户指示：按终局重命名，允许破坏性变更）

- 读取契约只保留两个方法：`GetPagedListAsync(OperationRecordCriteria criteria, PageRequest page, ct)` 与 `CountByBucketsAsync(scope, edges, actions?, distinctTargets?, ct)`。删除 `CountByDayAsync`、`CountDistinctTargetsAsync`、`OperationRecordCountFilter`；区间总去重数用两个边界 + `distinctTargets` 取得。
- `OperationRecordFilter` 改名 `OperationRecordCriteria`，单独成文件放 `Models/`（与 `OperationRecordVisibilityScope`、`OperationRecordInfo` 同处），不再用 `Filter` 后缀，避免与 `IDataFilter` 等过滤语义混淆。
- 桶号按二分嵌套条件（366 桶最深 8 层），边界经实例字段参数化；主表与归档表一条语句合并分组。
- T1 结论（10 万条、40% 在归档表）：PostgreSQL 366 日桶计数 45 ms、去重 79 ms（候选 B 对照 39 ms）；SQLite 238 / 383 ms；24 小时桶均 < 3 ms；结果与内存重算一致，每次调用一条语句。
- 同步替换 EF 存储、查询用例、`TestDoubles` 与测试；`operation-records.md` API 表；按 `versioning.md` 写破坏性提交并加脚注。
- 交付：本地 feed 打包、隔离缓存、核对包内 DLL 哈希与 XML 文档，并经 NuGet 由生成项目实际消费验证。

### T3 模板后端：统计接口重做

- 输入 DTO 按"请求校验"一节实现；删除 `Days`。
- 区间与边界在一处计算（新建私有计算类型，命名按后端规范核对），用户存量与创建数、操作记录计数共用同一组边界；用户表的按桶计数与框架采用同一实现思路（T1 结论）。
- 指标口径按响应表实现；无操作记录形态的 `LastLogin` 改为 `[start, end)`，DTO 注释写明它只是"最近一次登录"的近似，不代表历史活跃人数。
- `UserTimeZoneProvider` 空值与无效值回落改为 UTC：接口注释、`TryResolve` 初值、`SettingDefinitionProvider` 注释、现有缺值/坏值/Windows ID 测试同步更新。统计接口不调用 `GetAsync`。
- `Client` DTO、控制器、`IPlatformStatisticsAppService` 同步。
- 测试（真实 PostgreSQL）：今天按小时（含当前部分桶、零点请求、整点请求）、7 天、30 天、跨月自定义、366/367 天、未来结束日 400、缺参与坏日期 400、纽约回拨日 25 桶与春令日 23 桶、Kolkata 半小时偏移、Lord Howe 半小时夏令时、上期截止落在不存在或歧义时刻、边界上的事件、历史区间之后的注册与删除不影响期末存量、注册后删除仍计新注册、区间内登录后又在区间外登录的退化口径、跨租户隔离。

### T4 Mock

- `_mock/api/platform-statistics.ts` 按同一契约实现，边界规则与后端一致。用一组共享的期望边界用例（纽约回拨与春令、Kolkata、Lord Howe）同时校验 Mock 单测与后端单测的期望值，不以 `Intl` 两次迭代的结果自证。
- 400 的形状与后端 HTTP 测试固定的一致。

### T5 前端：仪表盘按 E 补齐

- **生效时区**：抽出单一解析入口（设置值能被当前 `Intl.DateTimeFormat` 使用则用它，否则用浏览器实际时区），`appDate` 与统计请求共用；日期上限与"今天"预设也用它。补"设置值非空但无效"的测试【I5】。
- **区间控件**：`toggle-group`（今天 / 7 天 / 30 天）+ `HlmDateRangePicker`（显示当前区间；选自定义时分段取消选中）。区间写入 URL（`startDate`、`endDate`）；URL 非法、缺一端、超跨度，或日期选择器被清空（`dateChange` 发出 `null`）时，统一回到 7 天，控件、URL、请求与标注同步【N4】。日期按本地年月日直接序列化，不用 `toISOString()`。请求身份包含日期与生效时区，`switchMap` 取消旧请求；刷新失败保留旧数据并保持其对应的区间标注【4】。
- **页头副标题**：区间说明 · 成功登录次数 · 需要处理的项数。
- **KPI 托盘卡**：外框标题右侧 ⓘ 是可聚焦按钮（有可访问名），`HlmTooltip` 写清口径（存量为期末快照、活跃用户定义与无操作记录时的近似、登录失败为近似值）。内卡为数值 + 迷你趋势（TanStack Chart `guides: false`、`margin: 0`，88×34；登录失败用 `--chart-3`，其余 `--chart-1`；没有 `series` 时不画）+ 官方胶囊徽章（升降箭头，按指标方向着色）+ 对比说明（事件类"较前 N 天同一进度"，"今天"为"较昨天同一时段"；存量类"较上期末"）。
- **图表导入的条件**：迷你图在所有形态都会用到（总用户、新注册），图表模块不再整体锁在 `LocalIdentity && IncludeOperationRecords` 内；登录趋势仍按原条件。按对应形态构建验证【4】。
- **登录趋势**：外框标题 + 方块图例；内卡头部为成功登录总数 + "次成功登录 · 区间说明" + 环比徽章 + 按日/按周分段（区间 ≥ 14 天才显示，超过 31 天默认按周；"今天"按小时，不显示分段）。直线，成功渐变面积，失败琥珀线，网格保持实线（0.16 没有虚线配置，规范注明差异）。纵轴 `Intl.NumberFormat` 紧凑记数；横轴按桶的真实 UTC 起点与响应时区本地化：小时 `HH:mm`（保留实际分钟，Lord Howe 切换后为 `02:30` 等），重复的本地时刻附 UTC 偏移；日 `M/d`；周 `M/d–M/d`【N1】。提示框共用十字准线，标题为该桶真实起止（由相邻桶起点或 `range.end` 得出，跨夏令时与末尾部分桶如实标注），两行成功、失败带色块。按周只累加登录次数序列，按响应时区的日历日归周，周起始日取界面语言的 `Intl.Locale#getWeekInfo`（不可用时取周一），首尾不完整周按实际日期标注；无本地化形态用 `displayLocale()` 的现有回落。图表保留读屏摘要。
- **需要关注**：计数用新增的 `warning` 徽章（大于 0 时）。图标块改为描边并按严重程度着色：锁定账户 `destructive`，未开两步验证的管理员与邮箱未验证 `warning`，已停用租户中性。动作文案按项区分（锁定账户"处理"，其余"查看"），仍按目标页权限显示。
- **最近操作**（按 E 的行表）【I6】：头像（`hlm-avatar` 首字回退，底色由操作人标识哈希取 `--chart-*` 淡底）；操作人复用现有 `actorOf()` 回落，空主体显示"匿名"，机器主体按其标识显示，模拟登录保留真实操作人提示；动作句；目标"类型 · 名称"（`targetName` 优先）；结果徽章两态（成功 / 失败），不新增第三态；相对时间。窄屏折叠目标与结果列，行可打开详情。
- **相对时间**【S2】：按生效时区判定。今天：1 分钟内（含时钟轻微超前的未来时刻）显示"刚刚"，否则"N 分钟前 / N 小时前"；昨天显示"昨天 HH:mm"；更早用 `appDate` 的 `monthDayTime`。完整时间放在 `<time datetime>` 上，键盘可达的提示中显示。复用一个分钟级时钟信号，随页面销毁清理；测试冻结当前时刻，覆盖午夜与夏令时日。
- `hlm-badge` 新增 `warning` 变体，登记到 `frontend-spartan.md`，同步 `scripts/check-spartan-customizations.mjs`。不重新生成或覆盖已有定制组件。
- 单测：区间与 URL 还原、清空选择器回到 7 天、生效时区回落、按周聚合（周起始日、跨年、首尾部分周、语言切换）、重复小时标注、Lord Howe 两个切换日的 `:30` 标签与时段、提示框内容、相对时间分档、徽章与图标严重程度、无 `series` 时不画迷你图、匿名与模拟登录的操作人展示、权限裁剪不回退。

### T6 文档与规范

- `api.md`：按日历区间统计的接口传"日期 + 生效时区"，时刻仍为 UTC 并回显时区；§2.5 澄清区间合法性属于请求结构校验。
- `coding-frontend.md` §9：生效时区的单一入口；统计类请求与坐标轴、提示框使用响应回显的时区。
- `frontend-ui.md`：图表规则恢复为分类色并补 E 的画法（直线、渐变面积、方块图例、提示框内容、网格实线差异）；KPI 托盘卡的说明图标与迷你趋势。
- 重拍着陆页产品截图（英文、1920×1200、`cwebp -q 80`）。

### T7 验证与交付

- 框架：操作记录与多租户测试全集。
- 模板后端（真实 PostgreSQL）：`identity-all-features`（全功能）、`standalone-capabilities-02`（无多租户、无操作记录）、`standalone-capabilities-06`（多租户、无操作记录）、`identity`（默认）、`resource` 的单元与集成测试。
- 前端：全功能、无本地化、最小（同 `standalone-capabilities-02` 参数）、resource 四形态的构建、lint、Prettier、单测；`check-all.ps1`；全场景矩阵交给 CI。T0 与 T7 各按真实变更执行，未改动的检查不重复跑。
- 有头浏览器端到端（真实后端）：今天 / 7 / 30 / 自定义区间切换与 URL 还原；`Display.TimeZone` 设为 `Asia/Kolkata`、`America/New_York`、留空、非空但无效四种情况下，坐标轴、提示框、分桶与操作记录页时间一致；迷你趋势、提示框、需要关注、最近操作亮暗两主题截图核对。
- 完成后送当前工作区的 Codex 会话做代码审查，通过后提交（不带身份署名）并推送 PR #86；合并等用户指示。

## 不做

- 导出（无独立导出对象，操作记录页已有导出）。
- 租户活跃度（跨租户、跨库汇总超出范围）。
- "高风险"状态与"已拒绝"第三态（没有对应数据，不虚构）。
- 为虚线网格升级图表依赖或手写 SVG。

## 风险

- T1 的 A 在 SQLite 或 PostgreSQL 上翻译失败或 366 桶耗时不可接受：停下回报，再在"限定提供程序的 B"与"框架内保留替换点"之间决定。
- `HlmDateRangePicker` 与已定制的 popover、button 组合：如需改 helm，先登记再改。
