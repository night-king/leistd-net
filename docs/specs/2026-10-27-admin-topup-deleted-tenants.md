# Admin 权限补齐跳过已删除租户（下游反馈）

状态：已完成 · 风险档：中（模板 DbMigrator 发布阻断缺陷、部署身份 scope 变更） · 关联：[#193](https://github.com/zengqinglei/leistd-net/pull/193)

## 1. 需求

### 问题与背景

下游反馈 `framework-feedback-topup-deleted-tenants-20261026.md`（abroker-crm 协调会话）：下游吸收模板 `AdminGrantTopUpRunner` 后，Identity 的 `DbMigrator` 预演退出 1。原因是 45 个已软删除的租户在业务库里还残留着 Admin 角色行，`AdminTenantsAsync` 把它们列为候选；随后 `currentTenant.Change` 按正式路由进入这些租户时，路由抛出 `TenantNotFoundException`。每个这样的租户都记为失败，结果阻断发布。只要环境里删除过租户，就会触发。

核对模板时还发现第二个问题（反馈未提及）：资源服务的 compose 只给迁移身份 `tenant-migration.read`。补齐要按正式路由进入租户，路由回源 `/runtime` 要求 `tenant-routing.read`，所以资源服务的补齐对每个租户都会失败。现有 E2E 只跑单库模式（`MigrationTarget`），不会执行补齐，因此没有暴露。

### ABP 对照（2026-10-27 核对 abpframework/abp dev 分支源码）

| 环节 | ABP | 本模板 |
| --- | --- | --- |
| 遍历哪些租户 | 应用模板 `MyProjectNameDbMigrationService` 用 `_tenantRepository.GetListAsync(includeDetails: true)`，软删除过滤器排除已删除租户，停用状态不过滤 | 资源服务无法列出全部租户：迁移端点只列有独立库的租户，这是有意设计。所以从 Admin 角色行出发，再按连接登记判定租户是否存在 |
| 进入租户 | `_currentTenant.Change(tenant.Id)` 后执行种子数据，连接解析与运行时相同 | 同样按正式路由进入，和 API 写授予走同一条路径 |
| 补 Admin 权限 | `PermissionDataSeedContributor` 在租户上下文里，把本侧权限授予 `admin` 角色 | `IPermissionGrantSeeder.TopUpAsync` |
| 找不到租户 | `MultiTenantConnectionStringResolver` 退回宿主默认连接 | 直接拒绝。这是正确的，不改 |

### 决定（用户 2026-10-27 确认按此执行）

- 补齐对象是存在且未删除的租户，与 ABP 一致：已删除租户计入跳过，不算失败；停用租户照常补齐。
- 判定放在模板 DbMigrator，复用现有的 `ITenantConnectionConfigurationStore.FindAsync`，返回 `null` 即视为租户不存在或已删除。框架不加新能力，也不放进 `IPermissionGrantSeeder`：遍历租户是迁移作业的职责，种子组件只管一个租户内的事。
- 报告保留跳过计数，并列出被跳过的租户标识。
- 资源服务迁移身份按方案 A，在 `tenant-migration.read` 之外再授予 `tenant-routing.read`。不采用方案 B（用迁移连接串直接写库），因为它绕开了正式路由。

### 验收标准

- Given 一个已软删除、业务库仍有 Admin 角色行的租户，以及一个停用租户，When Identity 的 `DbMigrator` 预演和施加，Then 两次都退出 0，输出 `Skipped 1 deleted or missing tenant(s): <id>`，停用租户被补齐。由 `AdminGrantTopUpTests.Deleted_tenants_are_skipped_and_inactive_tenants_are_still_topped_up` 覆盖；撤掉修复后该测试失败。
- Given 资源服务的迁移身份只持有 compose 里的两个 scope，When 在共享库与独立库租户、加一个已删除租户上运行全目标模式 `--apply`，Then 退出 0，无 `FAILED`，已删除租户计入跳过，重跑不再补授。由 OIDC E2E 的 S12 覆盖。

## 2. 方案

- `template/backend/src/CompanyName.ProjectName.DbMigrator/Runners/AdminGrantTopUpRunner.cs`：多租户形态下，进入租户前在宿主作用域、独立工作单元里调用 `FindAsync(tenantId, 业务连接名)`，返回 `null` 时计入 `TopUpReport.SkippedTenants`。本地 EF 存储和远端 `/runtime` 用的是同一个 EF 存储（`TenantConnectionManagementService.GetRuntimeAsync`），都只排除已删除租户，停用租户在两种形态下都能路由。
- `Program.cs`：在报告里输出跳过行。
- 资源服务迁移身份的 scope：compose、`.env.example` 注释、DbMigrator 的 `appsettings.Development.json`、`backend/README.md`、`docs/standards/service-invocation.md`。
- 文档：在 `backend/README.md` 与 `docs/operations/runbooks/grant-new-permissions.md` 写明跳过与停用租户的语义。

## 3. 计划

| 任务 | 产物 | 验证 |
| --- | --- | --- |
| 跳过已删除租户 | runner、Program | Identity 集成测试（真实 PostgreSQL，Windows 执行端）；撤掉修复后测试失败 |
| 资源服务迁移身份 | compose、配置、文档 | E2E S12（CI） |
| 文档 | README、runbook、service-invocation | 模板符号检查、生成检查 |

### 不做的事

- 删除租户时不级联清理业务库数据：与 ABP 一致，软删除保留数据，可以恢复。
- 不让 `/runtime` 接受 `tenant-migration.read`：两档权限保持分开。

## 4. 结果

### 实际交付

- [#193](https://github.com/zengqinglei/leistd-net/pull/193)，合并提交 `96f74ca9`，develop 发布 `v1.3.0-beta.52`（框架源码与 beta.50 相同）。

### 与计划的偏差

- Codex 首轮实施审查指出 S12 写死了 scope：把部署配置退回旧值，S12 也发现不了。现改为从生成项目的 compose 读取迁移身份 scope，并断言与 DbMigrator 开发配置一致。复审时，Codex 用替身逐一试了单独回退 compose、单独回退开发配置、两处同时回退三种情况，都会被测试拒绝。
- PR 的 CI 没有选中 OIDC E2E：它按认证、租户等路径触发，本次改动的文件都不在其中，E2E 脚本本身也不在。因此在 PR 分支上手动触发了一次完整 CI，S12 在其中实际运行并通过。

### 验收记录

- Identity 集成测试：在 Windows 执行端的真实 PostgreSQL 上，`AdminGrantTopUpTests` 全部通过；撤掉跳过分支后，`Deleted_tenants_are_skipped_and_inactive_tenants_are_still_topped_up` 失败（执行端按退出码判定）。
- OIDC E2E：手动触发的 CI run 40444692322 中，S2–S12 全部通过（`OIDC E2E passed: … S11, S12, …`）。
- PR CI 与 `check-all.ps1` 的 50 道静态闸门通过。

### 遗留项

- 只改 `scripts/test-template-oidc-e2e.ps1` 时，CI 不会选中 OIDC E2E（`scripts/plan-quality-checks.py` 的 `OIDC_INPUT` 不含它）。是否补上，待用户决定。

### 已上收的常驻文档

- 生成项目：`template/backend/README.md`（数据库迁移、机器身份 scope）、`template/docs/operations/runbooks/grant-new-permissions.md`、`template/docs/standards/service-invocation.md`。
