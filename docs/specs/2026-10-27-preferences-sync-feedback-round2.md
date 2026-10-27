# 账户偏好跨服务同步的适配反馈第二轮（下游反馈）

状态：进行中 · 风险档：低（模板前端单一服务的在途合并） · 关联：[2026-10-27-preferences-sync-feedback.md](2026-10-27-preferences-sync-feedback.md)

## 1. 需求

### 问题与背景

下游按 #196（`1.3.0-beta.60`）回执完成适配后，提出一条新反馈。用户 2026-10-27 确认按推荐方案执行。

### 条目与决定

| 编号 | 问题 | 决定 | 落层 |
| --- | --- | --- | --- |
| 前端-1 | `SessionContextService.revalidate` 在途时直接返回在途那次，`clear()` 不放下它：A 的重查在途时换成 B，B 回到前台的可见事件并入 A 那次；A 那次回来时按 `AuthService.revision` 与偏好读取号作废，B 实际少一次重查 | 采纳：`clear()` 放下在途重查；结束时只在仍是自己那次时才置空。换一个下游同样会遇到 | 模板前端 |

### 验收标准

- Given A 的重查在途，When 主体离开、B 的页签回到前台，Then 另起一次 `/me`；When A 那次随后结束、页签再次回到前台，Then 不再另起（B 那次仍在途），A 的结果不写回；When B 那次回来，Then 应用 B 的用户与偏好（`session-context-service.spec.ts`）。

## 2. 方案

`template/frontend/src/app/core/services/session-context-service.ts`：`revalidate` 以局部变量持有本次 Promise，结束时（成功或失败）比较后再置空；`clear()` 首先置空 `revalidating`。迟到结果的作废沿用现有两个号，不改。

## 3. 计划

| 任务 | 验证 |
| --- | --- |
| 回归测试与修复 | 修复前新用例失败；`src/app/core` 全部 spec、eslint、prettier、tsc；两处修复分别变异，新用例均失败 |

## 4. 结果
