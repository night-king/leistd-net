# 账户偏好跨服务同步的适配反馈（下游反馈）

状态：进行中 · 风险档：中（框架 UnitOfWork 日志语义、模板前后端行为） · 关联：待开 PR

## 1. 需求

### 问题与背景

下游三仓按 #191（`1.3.0-beta.46`）适配账户偏好跨服务同步，在演示站验收时发现若干问题，写成反馈 `framework-feedback-preferences-sync-20261027.md`：9 条待处理、2 条仅记录，另有 1 条下游用户决策的补充项。用户 2026-10-27 确认全部按推荐方案执行，补充项也采纳，并入同一个 PR。

### 条目与决定

| 编号 | 问题 | 决定 | 落层 |
| --- | --- | --- | --- |
| K9-1 | 写入失败回来时主体已离开，仍返回 rejected/unconfirmed，提示落到下一个人的界面上 | 采纳：catch 分支同样核对主体，离开即 `discarded` | 模板前端 |
| K9-2 | 设置区把 `discarded` 当成"已保存" | 采纳：保留草稿，"保存中"退回常态，不显示已保存 | 模板前端 |
| Identity-1 | Token Exchange 的 Audience 与 Scope 共用一个变量 | 部分采纳：模板 Identity 以 `OAuth:Resource` 同名登记自身 API 的 scope（`OAuthScopes.cs`），两者在模板里相等，部署变量不拆；文档说明 Identity 另起 scope 名时分开填 | 模板文档 |
| 资源-1 | 写入客户端没有时限，Identity 挂起要等 `HttpClient.Timeout` 的 100 秒 | 采纳：写入也挂标准弹性管道，`Retry.DisableForUnsafeHttpMethods()`，保留超时与熔断 | 模板后端 |
| 资源-2 | 机器身份被拒时 `/me` 整体 502 | 采纳降级：当前用户读取遇依赖故障时偏好按暂时读不到并记 Error；服务端读取（给收件人产出文本）照常抛出 | 模板后端 |
| 资源-3 | 读取遇 408/429 直接抛出 | 采纳：与 5xx 一样回落旧值 | 模板后端 |
| 资源-4 | 资源服务 PATCH 的强 ETag 与 Identity 的不同 | 不采纳：强 ETag 对应本次返回的字节表示，资源服务按自己的序列化输出就该用自己的值；转发 Identity 的值反而违反强 ETag 语义。ETag 是不透明的、按源各自有效，前端也不使用 | 不改 |
| 资源-5 | 偏好加字段要改三处 | 采纳为文档：设置规范写明扩展点 | 模板规范 |
| 演示-1 | 工作单元把提交失败一律记 Error，调用方预期并重试的并发首访冲突留下假故障 | 采纳方案 (a)：提交开始前的失败事务型记 Debug、非事务型记 Warning，异常照抛，未处理的由全局异常处理记 Error；提交开始后的失败仍记 Error。与 ABP `UnitOfWork.CompleteAsync`（只记录异常再抛出，不写日志）一致。不做"预期异常判定钩子" | 框架 UnitOfWork |
| 演示-1 附 | 只刷新访问时间改为条件更新 | 不适用：模板投影只在资料变化时写，没有访问时间刷新 | 项目级 |
| 补充-1 | 页签回到前台并行重查 `/me`、权限与设置 | 采纳：只重查 `/me`，且不静默，401 由拦截器清会话并转去登录；权限与设置在刷新页面或重新登录时更新 | 模板前端与规范 |
| 审查-F1 | 设置区在自己的队列轮到时才调用 `preferences.write()`，期间换人会以下一个人的会话发出上一个人的意图（Codex 实施审查发现，基线已有） | 与 K9 同类，一并修复：操作当下即交给偏好服务捕获主体，组件队列只按序发布结果 | 模板前端 |
| 资源-0、演示-2 | nuget.org 索引延迟；304 的日志分级 | 仅记录 | — |

### 验收标准

- Given 写入在途时主体离开，When 请求以 400 或 503 失败，Then 返回 `discarded`（`account-preferences-service.spec.ts`）。
- Given 偏好写入返回 `discarded`，When 设置区处理结果，Then 行上既不显示已保存也不报错（`setting-section.spec.ts`）。
- Given Identity 挂起，When 资源服务代表本人写偏好，Then 按弹性管道超时以 `ServiceClientFailureKind.Timeout` 失败；暂时性失败只发一次（`IdentityAccountPreferencesWritePipelineTests`）。
- Given 已缓存旧值，When Identity 返回 408 或 429，Then 回落旧值（`IdentityAccountPreferenceCacheTests`）。
- Given 机器身份被拒，When 当前用户读取，Then 返回 null；When 服务端读取，Then 抛出（`IdentityAccountPreferencesTests`）。
- Given 事务型工作单元在保存阶段失败，Then 记 Debug、无 Warning 以上；非事务型记 Warning；提交阶段失败仍记 Error（`UnitOfWorkLoggingTests`）。
- Given 页签变为可见，Then 只请求 `/me`，且请求不是静默的（`session-context-service.spec.ts`、`auth-service.spec.ts`）。
- Given 两项偏好写入排队，When 组件销毁、主体离开后首请求失败，Then 不再发出第二个 PATCH（`setting-section.spec.ts`）。
- Given Token Exchange 取令牌挂起，When 写偏好，Then 同样按管道超时失败（`IdentityAccountPreferencesWritePipelineTests`）。

## 2. 方案

- 框架：`DefaultUnitOfWork.CompleteAsync` 的通用 catch 按 `commitStarted` 与 `IsTransactional` 分级；随包文档 `framework/docs/components/unit-of-work.md` 同步。
- 模板后端：`Infrastructure/DependencyInjection.cs` 给写入客户端挂管道；`IdentityAccountPreferenceCache` 把 408/429 并入回落旧值的分支；`IdentityAccountPreferences.ReadAsync` 在 `CurrentUser` 模式下捕获 `RemoteServiceException`/`ServiceClientException` 并返回 null。
- 模板前端：`AccountPreferencesService.write` 的 catch 核对主体；`SettingSection` 处理 `discarded`；`SessionContextService.revalidate` 只调 `fetchUser(false)`；`AuthorizationService` 删去仅供旧重查使用的 `revision` 与 `fetch()`。
- 文档：`service-invocation.md`、`.env.example`、`settings.md`、`auth.md`、`deploy/README.md`。

## 3. 计划

| 任务 | 验证 |
| --- | --- |
| 框架日志分级 | UnitOfWork 全部测试；新测试修复前失败 |
| 模板后端三项 | 资源服务场景单元测试；四处修复逐一变异，测试均失败 |
| 模板前端四项 | 受影响 spec、lint、prettier、tsc；撤修复时新用例失败 |
| 文档 | `check-all.ps1` |

### 不做的事

- 不给工作单元加"预期异常判定"扩展点。
- 资源服务 PATCH 不转发 Identity 的 ETag。

## 4. 结果

（实施后补写）
