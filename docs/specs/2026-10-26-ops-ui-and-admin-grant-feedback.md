# 运维页前端组件与 Admin 权限补齐（下游反馈）

状态：进行中 · 风险档：高（框架公共 API、权限语义变更、跨框架与模板） · 关联：待补

## 1. 需求

### 问题与背景

下游三仓在运维页改造中提交了两份反馈，评审结论已写成回执：

- `framework-feedback-ops-ui-20261026.md`，回执为 `framework-receipt-ops-ui-20261026.md`；
- `framework-feedback-host-admin-20261026.md`，回执为 `framework-receipt-host-admin-20261026.md`。

本规格只覆盖经用户授权实施的条目（2026-10-26）。其余条目的不采纳理由见回执：ops-ui 第 2、6 条，host-admin 第 3 条。

### 条目与决定

| 编号 | 问题 | 决定 | 落位 |
| --- | --- | --- | --- |
| UI-1 | `faceted-filter` 搜索框写死 `h-8!`，盖掉了粗指针下的 44px；选项行、清除按钮没有 `data-slot`，`styles.css` 补不到。长标签、长选项会把工具条撑出横向溢出 | 粗指针三处补足；宿主、按钮可收缩，标签与徽章截断；面板选项改为折行；窄屏只显示计数徽章，按断点自动切换，不加 `compact` 输入 | 模板前端 |
| UI-3 | `frontend-lists.md` 没写展开详情的数据从哪来 | 补规则：详情取自行数据，只有一对多、要分页的子数据才按需请求，并丢弃迟到的响应 | 模板规范 |
| UI-4 | 值带图标按钮的详情行，标签与值错位 | 补规范：网格 `items-baseline`、值在前、按钮 `shrink-0`、空值显示占位文字 | 模板规范 |
| UI-5 | 带 `overflow-hidden` 的元素直接放进页面滚动列会被压扁 | 页面骨架的滚动列加 `*:shrink-0`，规范写明 | 模板前端、规范 |
| HA-1 | Admin 只在首次播种时拿到全部权限，之后新增的权限要手工授予；租户 Admin 不是超级管理员，每个租户都得自己补 | **推翻**「首次播种后不自动补回」的现行约定（用户 2026-10-26 确认）：框架提供补缺，模板 DbMigrator 每次施加迁移后给每个 Admin 补齐。Admin 只增不减，要细分权限用自定义角色 | 框架 Authorization、模板 DbMigrator 与文档 |
| HA-2 | 授权文档要求改名一律「删旧、重新授予」，语义不变的拆分也因此丢授予 | 按语义是否变化分两种处理，给出平移示例迁移 | 框架随包文档 |

### 验收标准

- **HA-1 框架**：
  - Given 一个已有部分授予的角色，When 调 `GetMissingAsync`，Then 返回该侧可用、尚未授予的权限，不写入。
  - When 调 `TopUpAsync`，Then 只补这些权限，已有授予（含已失效的旧名）不动；没有缺口时零写入、版本不变；重复调用幂等。
  - 覆盖测试：`PermissionManagementTests`。
- **HA-1 模板**：
  - Given 一个已完成初始化的系统（宿主 Admin、共享库租户 Admin、独立库租户 Admin），新增一条宿主侧、一条租户侧权限定义。When 运行 `DbMigrator --apply`，Then 宿主 Admin 补到宿主侧那条，各租户 Admin 补到租户侧那条，版本各 +1。
  - When 再运行一次，Then 零写入。
  - When 不带 `--apply` 预演，Then 输出各 Admin 将补入的条数，不写入。
  - Given 有租户库迁移失败，Then 不执行补齐，作业以失败结束。
  - 覆盖测试：集成测试 `AdminGrantTopUpTests`，真实 PostgreSQL。
- **HA-1 恢复命令**：`--restore-tenant-admin` 与 `--grant-admin --restore` 改用框架的补缺入口，既有集成测试不变。
- **HA-2**：文档示例在真实 PostgreSQL 上执行两次，第二次无变化；受影响主体版本 +1；旧名授予删除。人工验收，结果记入 §4。
- **UI-1**：spec 用 `expectTouchTarget` 固定三处点按目标；伪语言长标签下按钮不超出容器；窄屏只显示计数，宽屏显示徽章。
- **UI-5**：spec 把带 `overflow-hidden` 的元素直接放进滚动列，展开内容后断言它的高度等于内容高度。
- **UI-1、UI-5 的界面**：先出对比页，用户确认后实施（人工验收）。

### 方案

- **HA-1 框架**：`IPermissionGrantSeeder` 新增 `GetMissingAsync` 与 `TopUpAsync`。补缺逐项调用 `IPermissionGrantManager.GrantAsync`：点操作带版本快照重试，保留已失效的旧名授予，不需要额外的锁。`ReplaceGrantsAsync` 会校验整个集合，已失效的旧名会让它拒绝写入，所以不用它。
- **HA-1 模板**：新增 `AdminGrantTopUpRunner`，在迁移报告成功后执行（预演时只在全部目标都已是最新时计算）。
  - 按物理业务库（默认库与各独立库）读出未删除的 Admin 角色及其租户 Id。
  - 逐个切到对应租户，在工作单元里经补缺入口写入；写入路径与运行时一致，按租户路由落库。
  - 迁移作业的服务组合加上授权存储与权限定义。
- **UI 各项**：落点见上表。

## 3. 计划

| 任务 | 产物 | 验证 |
| --- | --- | --- |
| HA-1 框架 | 接口、实现、XML 注释、`authorization.md`、单测 | 授权组件测试；打包到 `.tmp/local-feed` |
| HA-2 | `authorization.md`「删除或改名一个权限定义」改写 | 生成项目里在真实 PostgreSQL 上执行示例 |
| HA-1 模板 | Runner、DI、Program、两个恢复命令改用补缺入口、注释与 `backend/README.md`、集成测试 | 本地包生成 `identity-all-features` 与 `resource`；跑相关集成测试；其余场景由 PR CI 矩阵承担 |
| UI-1、UI-5 | 对比页 → 组件、页面骨架、spec | 前端单测、lint，四档宽度复核 |
| UI-3、UI-4 | `frontend-lists.md` | 文档引用检查 |

分两个 PR：PR 1 为 HA-1、HA-2（框架 + 模板后端）；PR 2 为 UI 各项（模板前端与规范）。PR 1 改了框架，合入 develop 后会发框架 beta；发布前先向用户确认。

### 不做的事

- 迁移补齐不写操作记录，只记日志。迁移作业每次都会执行补齐，留操作记录会产生大量无意义记录。
- 不加管理作用域锁。授权版本已经是并发令牌，点操作冲突时会重试。
