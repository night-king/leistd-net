# 模板前端 UI 改版（E 方案）实施计划

状态：已完成（PR #84、#86、#88）

范围：`template/` 的前端（着陆页、登录注册、平台后台、工作空间）、为仪表盘与用户批量操作新增的后端接口与 Mock，以及承载规则的生成项目规范。

方案来源：对比页「Leistd 界面改版对比」的 E 档（C 的版式 + 圆角尺寸统一 + 登录注册右栏独立 + 列表页保持原内容 + 后台顶栏不变）。本计划取代 `2026-10-03-template-ui-refresh.md`，该文件在本计划获批后删除；其中仍成立的条目（中性当前项、inset 的 peer 问题、Mock 设置面板名、减少动效清单、加载四态）已并入下文。

全部任务完成并验收后删除本计划。

## 评审依据

- 设计原则 `docs/architecture/design-principles.md` §5：终局不留中间态、优先官方机制、不过度设计、只修"默认就会出错"的问题。
- 模板开发规范 `docs/template/development-guide.md`；生成项目规范 `template/docs/standards/`：`frontend-ui.md` §2.8、`frontend-spartan.md`、`coding-frontend.md`、`api.md`、`testing.md`。
- Spartan 官方 Skill `template/.agents/skills/spartan/`：只用语义 token；组件外 class 只管布局；新颜色先定义变量再 `@theme inline`；不改 Brain。
- 操作记录长期约束 `docs/architecture/operation-records-principles.md`；组件文档 `framework/docs/components/operation-records.md`、`multi-tenancy.md`。

## 现状事实（决定方案边界）

| # | 事实 | 出处 | 影响 |
| --- | --- | --- | --- |
| F1 | 1.5.0 CLI 内置 `vega`；`migrate-helm-libraries` 删库重建，会覆盖本地改动 | `@spartan-ng/cli@1.5.0` 源码 | 切换分两类处理，见 T1 |
| F2 | 规范登记了 15 个已定制 helm（button、input、input-group、select、dialog、alert-dialog、sheet、popover、tooltip、combobox、navigation-menu、sidebar、dropdown-menu、badge、utils），**规定不得对它们执行 migrate** | `frontend-spartan.md` | 已定制组件按 vega 样式表手工合入 |
| F3 | 登录成功/失败已写入操作记录（`auth.login.succeeded` / `auth.login.failed`），表有 `(TenantId, CreationTime DESC)` 索引；读取接口只有分页与导出，没有聚合 | `OperationRecordActions.cs:115,123`、`OperationRecordConfiguration.cs:56-62` | 登录趋势可以做，需自写聚合查询 |
| F4 | 匿名登录失败会被节流折叠采样 | `operation-records.md` §19 | 「登录失败」只能标为近似值 |
| F5 | 操作记录受 `IncludeOperationRecords` 控制，登录动作另受 `LocalIdentity` 控制 | `template.json`、`OperationRecordActions.cs:112` | 趋势与失败数按条件裁剪 |
| F6 | 宿主上下文只看 `TenantId == null` 的行；租户可有独立数据库 | `multi-tenancy.md:110` | 统计按**当前作用域**，不做跨租户汇总 |
| F7 | `User` 有 `CreationTime`、`Lockout.{IsLocked,End}`、`LastLogin.Time`、`TwoFactor`、`EmailConfirmed`、`IsActive`，均为可查询列；**没有**密码更新时间 | `User.cs`、`IdentityEntityConfiguration.cs:26-28` | 新注册、锁定、未开两步验证可统计；去掉「密码 N 天前更新」 |
| F8 | 开放应用**没有**密钥到期概念 | `OpenApplicationOutputDto` | 去掉「密钥即将到期」 |
| F9 | 用户已有 enable / disable / unlock / roles 单条接口（前三者不受 `LocalIdentity` 限制），**没有**批量接口；`api.md` 没有批量约定 | `UserController.cs` | 新增批量接口与规范条目 |
| F10 | 没有请求量 / API 调用量指标 | — | 去掉「API 调用」KPI |
| F11 | 用户表基于 TanStack Table，无行选择；`hlm-checkbox` 已安装 | `user-table.ts` | 用 TanStack 行选择 |
| F12 | 官方 `chart` 依赖 `@tanstack/angular-charts@0.16`，Angular `>=21 <23` | CLI 1.5.0 | 引入新依赖，T5 前先核对许可与包体 |

## 已定决策

### D1 原型中按事实调整的内容

| 原型元素 | 调整 | 依据 |
| --- | --- | --- |
| KPI「API 调用」 | 改为「用户总数」 | F10 |
| KPI「活跃用户」 | 定义为所选区间内有登录成功记录的去重用户；无操作记录时退化为 `LastLogin.Time` 落在区间内的用户数，不显示同比 | F3、F5 |
| KPI「登录失败」 | 显示为近似值（提示说明节流采样） | F4 |
| 需要关注「密钥 7 天后到期」 | 删除 | F8 |
| 需要关注 | 改为：已锁定账户、未开启两步验证的管理员、邮箱未验证的用户、已停用租户（多租户形态） | F7 |
| 租户活跃度 | 删除，该位置放「快捷入口」 | F6：跨租户、跨库汇总超出范围 |
| 侧栏底部「已发送 1,284 条 · 3 个实例」 | 只保留实时连接状态（`IncludeRealTime`），不显示数字 | 没有数据来源 |
| 仪表盘「日期选择」「导出」 | 删除，只保留 7 天 / 30 天分段 | 没有导出对象；分段已覆盖区间 |
| 工作台「密码 N 天前更新」 | 删除；账户安全检查保留「邮箱已验证」「两步验证」两项 | F7 |
| 工作台「导出我的操作记录」快捷动作 | 改为「登录设备」 | 无用户侧导出接口 |

### D2 统计接口

- `GET /api/v1/platform/statistics?days=7|30`，一个接口返回仪表盘全部数据，不拆多次请求。
- 作用域：当前上下文（宿主看宿主、租户管理员看本租户），不禁用多租户过滤，不遍历租户库。
- 字段按条件裁剪：`loginTrend`、`activeUsers`、`failedLogins` 仅在 `IncludeOperationRecords && LocalIdentity` 时存在；`disabledTenants` 仅多租户；两步验证与邮箱相关字段仅 `LocalIdentity`。
- 同比：与上一个等长区间比较，前端只展示差值百分比；上一区间为 0 时不显示同比。
- 权限：新增 `App.Dashboard`（平台仪表盘可见即有）。现在仪表盘不需要权限，加了统计后，数字会暴露用户量，需要单独授权；没有权限的用户只看到问候语和快捷入口。
- 聚合在 Application 层的查询服务里用 EF LINQ 实现，不给 `Leistd.OperationRecords` 加聚合 API。原因：这是模板仪表盘的一次性需求，框架组件按 §6.5 不扩展。

### D3 用户批量操作

- 接口：`POST /api/v1/users/batch/enable`、`/disable`、`/unlock`、`/assign-roles`。请求体是 `ids`（上限 100）；`assign-roles` 另带 `roleIds`，只追加不覆盖。
- 逐条执行并逐条写操作记录（复用单条动作码）；响应返回每条的结果（成功 / 失败码）。部分失败不回滚已成功的条目。
- 权限与单条接口一致：`App.Users.Update`；分配角色用 `App.Users.ManageRoles`。不做批量删除。
- 不能对自己执行停用；超级管理员跳过并返回失败码。规则与单条接口相同，复用领域方法。
- 在 `api.md` 增加「批量操作」一节：路径形态、上限、逐条结果、不做批量删除。

### D4 其他沿用的决定

- 当前项统一中性（`sidebar-accent` 白底细边、`bg-muted`），删除 `!` 覆盖与手写 `bg-primary/10 text-primary`；规范 §2.8 同步改写。
- 圆角两档：控件、菜单项、图标块 `rounded-md`（8px）；卡片、inset 面板、对话框 `rounded-xl`（14px）。托盘卡内层按嵌套规则用 `rounded-lg`（10px = 14 − 4px 间距），不写任意值。徽章、头像保持官方的胶囊 / 圆形。
- 后台顶栏按钮保持现状（主题、通知、语言）；列表页内容保持现状，只有用户表加多选。
- 着陆页与登录注册保留 C 的视觉（光晕、渐变字仅限营销页），规范中把「不用装饰性渐变」限定为应用内页面。

## 任务（按实施顺序，每个阶段完成后先送审再提交）

### T0 准备

- 从最新 develop 拉新分支 `zengqinglei/template-ui-refresh-e`；删除旧计划 `2026-10-03-template-ui-refresh.md`。
- 先做旧计划里的 Mock 修复：`_mock/api/setting.ts` 按 `Accept-Language` 返回设置分组名（内容不变），避免后续截图混入译名错误。

### T1 Vega 与令牌

- **先核对登记表与源码一致**（已做，2026-10-12）：在临时目录用 CLI 1.5.0 重新生成纯净 Nova，两边统一 prettier 后对比，差异恰好落在登记的 14 个组件加 `utils`，没有未登记的改动。
- **全部基于官方重新生成，再按登记表重新改**，不做三方合并：
  1. `components.json` 的 `style` 改为 `vega`，对 37 个组件执行 `migrate-helm-libraries`（每个组件一个 `--libraries=<name>`）。`utils` 不在迁移范围，保留。
  2. 按登记表的六类改动逐项重做：触屏 44px（button、input、input-group、select）；减少动效（dialog、alert-dialog、sheet、popover、tooltip、select、combobox、navigation-menu 的动画加 `motion-safe:`，sheet、navigation-menu、sidebar 的过渡加 `motion-reduce:transition-none`）；暗色 destructive 淡底 alpha（button、badge）与 badge 的 `success` 变体；读屏文案令牌（dialog-content、sheet-content、sidebar-trigger）；页头为关闭按钮让位（dialog-header、sheet-header）；dropdown-menu 触发器的 CDK 定位修正。
  3. 按 Vega 的新几何复核数值：`sm` 按钮从 `h-7` 变为 `h-8` 后字号是否仍需改 `text-xs`；Vega 对话框关闭按钮移到 `top-4 end-4` 后，页头让位从 `pe-6` 调整为多少，以截图为准。
  4. 收尾时再生成一份纯净 Vega 做同样的对比，差异必须与更新后的登记表逐项一致。登记表补一列「Vega 下的取值」。
- 这一步单独提交，不夹带布局或颜色改动，便于评审和回退。
- `styles.css`：画布、面板、`--accent` 降彩度、`--sidebar-accent`、`--input` 与 `--border` 拆开，新增 `--chart-1..5` 与 `--info`（接入 `@theme inline`）；亮暗两套按对比度核算（文字 ≥ 4.5:1，控件边界 ≥ 3:1），核算表写进提交说明。
- 登录卡里手写的 `[--card-spacing:--spacing(6)]` 删除（Vega 已是 6）。
- 验收：`healthcheck` 通过；全部前端单测通过；五张列表、所有对话框、设置页在 1280 / 1600 截图对照，无溢出。

### T2 外壳

- 平台：`hlm-sidebar variant="inset"`；处理 `peer-data-[variant=inset]` 的兄弟选择器问题（把 `hlm-sidebar` 上移到布局，或在组件宿主转出 `peer` 与 `data-*`，取改动小的一种）。
- 侧栏顶部加搜索入口，点开 ⌘K 命令面板（已安装的 `command`）。命令项来自 `NavigationService` 按权限裁剪后的菜单；不建通用命令注册框架。
- 侧栏不显示分组计数，菜单项右侧留给将来的待办提醒。
- 实时连接状态卡：仅 `IncludeRealTime`，读取现有连接状态信号。
- 顶栏按钮不变。Logo 改为实心 `bg-primary`、`rounded-md`。
- 工作空间：顶栏内容 `max-w-7xl` 对齐正文；当前项中性；顶栏搜索框打开同一个命令面板。
- 验收：桌面展开 / 折叠、移动端抽屉、⌘K 键盘操作与焦点恢复、亮暗色、reduce motion。

### T3 着陆页与登录注册

- 着陆页：导航、首屏（公告徽章、展示标题、两个 lg 按钮）、产品截图（静态图，放 `public/`，亮暗两张）、能力网格、技术栈、深色收尾带、页脚。能力项按条件参数裁剪（多租户、通知、开放应用、多语言、外部登录）。
- `auth-shell` 改为五五分栏：右栏独立贴边，lg 以下隐藏；登录、注册共用。
- 登录：外部登录 GitHub | Google 并排在表单上方；租户行改为「租户：宿主 · 切换租户」，从域名解析时只读。
- 注册：密码强度条，规则读后端口令策略（已有设置），不另立规则。
- 新增词条（en / zh-CN），`IncludeLocalization` 关闭时英文直出。
- 验收：Identity、Standalone、Resource（只有登录入口）三种形态；ExternalLogin 开 / 关；375 / 768 / 1280。

### T4 后端：统计与批量接口

- `PlatformStatisticsAppService` + 控制器 + DTO + 权限 `App.Dashboard`（含多语言名称、种子授权给管理员角色）。
- 批量接口 4 个，逐条写操作记录。
- Client 项目（`IMyProjectClient`）同步新增方法与 DTO。
- 集成测试（真实 PostgreSQL）：
  - 统计：宿主与租户作用域隔离；7 / 30 天区间边界（按 UTC 日）；上一区间为 0 时不返回同比；`IncludeOperationRecords=false` 时字段缺失；无 `App.Dashboard` 返回 403。
  - 批量：权限、上限、部分失败、对自己停用被拒、超级管理员跳过、每条都有操作记录。
- 验收：`identity-all-features`、`identity-tenant-management-without-history`（无操作记录）、`resource-notifications` 生成后构建与测试通过。

### T5 平台仪表盘

- `ng g @spartan-ng/cli:ui chart`，登记依赖；迷你趋势与登录趋势都用它，不手写 SVG。
- 布局：问候页头（7 / 30 天分段）→ KPI 四格（托盘卡）→ 登录趋势 + 需要关注 → 最近操作 + 快捷入口。
- 托盘卡做成页面内的共享组件 `shared/components/stat-panel`（外框标题 + 内卡内容），不改 Card helm。
- 状态：首次加载骨架；失败可重试；无 `App.Dashboard` 只显示问候与快捷入口；无操作记录时隐藏趋势卡，「需要关注」占满宽度。
- 最近操作复用 `OperationRecordService`，没有权限不请求。
- 验收：有 / 无权限、有 / 无操作记录、宿主 / 租户、亮暗色。

### T6 工作台

- 问候 + 命令框（与 T2 共用命令面板）+ 常用动作按钮。
- 「继续工作」：静态示例卡，带「示例」徽章与给下游的替换说明，不读数据。
- 快捷入口六项按 `LocalIdentity`、`IncludeNotifications` 裁剪；账户安全检查两项；登录设备复用现有接口；最近通知复用 `NotificationService` 共享状态（与铃铛一致，加载失败可重试）。
- 验收：Identity、Resource 带通知、关闭通知三种形态，各区块无空壳。

### T7 用户多选与批量操作

- `user-table` 开启 TanStack 行选择，首列 `hlm-checkbox`（含全选当前页）。选择只在当前页内有效，翻页、筛选、排序时清空。
- 选中后在工具栏下方出现批量操作条：已选数量、启用、停用、解锁、分配角色、取消选择（Esc）。按钮按权限显示。
- 执行后 toast 汇总「成功 N 条，失败 M 条」，失败项可展开查看原因；完成后刷新当前页。
- 列、工具栏、分页保持现状。
- Mock：四个批量接口及 spec；`check-template-mock-coverage.py` 通过。
- 单测：选择清空时机、按权限显示、部分失败的提示。

### T8 规范

- `frontend-ui.md` §2.8：圆角改为 `rounded-xl` / `rounded-md` 两档加嵌套规则；当前项改为中性；页面外框与区块间距；装饰性渐变只限营销页；图表颜色用 `--chart-*`。
- `frontend-spartan.md`：记录 style 为 vega；登记新增或改动的 helm（chart 如有定制）。
- `api.md`：批量操作一节。
- `template/docs/README.md` 的按任务读取表同步。

### T9 验证

- 模板矩阵：`pwsh scripts/test-template-matrix.ps1 -Tier pr`（三个切片都涉及：all-features、notifications/localization、resource/standalone）。
- 另外生成 `identity-all-features`、`identity`、`resource-notifications`、`identity-tenant-management-without-history` 做浏览器验收（agent-browser，有头，Mock 与真实后端各一次）。
- 截图：375 / 768 / 1280 / 1600 × 亮暗 × 中英；与对比页的「现在」截图并排存证。
- 未执行的场景逐项列出。

## 已确认（2026-10-12）

1. **侧栏不显示计数。** 菜单项右侧的数字位置留给将来的待办提醒，本期不占用。
2. **新增 `App.Dashboard` 权限。** 现有管理员角色由种子补授权，升级说明写明。
3. **图表用 Spartan 官方 `chart`。** 核对结果：
   - 1.5.0 与当前最新 1.6.3 的 CLI 都提供 `chart`，peer 依赖都固定为 `@tanstack/angular-charts@0.16.0` 与 `@tanstack/charts@0.16.0`（MIT，2026-08-26 发布）。TanStack Charts 已发 1.x，但 Spartan 尚未跟进，按 CLI 给的精确版本安装，不自行升到 1.x。
   - helm 层只有三件：`hlmChart`（把 `--chart-1..5`、popover、border 映射到 TanStack 的 CSS 变量）、`HLM_CHART_THEME`（调色板读 `--chart-*`）、`hlm-chart-tooltip`。颜色全部来自语义 token，不写色值。
   - 官网 chart 页由脚本渲染、抓不到正文，以上取自 CLI 生成源码 `generators/ui/libs/chart`。迷你趋势（无坐标轴）能否只靠配置实现，在 T5 开头用一个原型验证；不能时迷你图改为卡片内的小号面积图，不手写 SVG。
4. **Spartan 版本留在 1.5.0。** 1.6.x 发布不足两周，且与风格切换叠在一起会混淆回归来源；改版完成后另行升级。
