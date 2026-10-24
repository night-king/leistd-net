# 先确认再写的操作从点击起进入进行中

状态：已完成 · 风险档：中（模板前端交互与规范，影响所有生成项目） · 关联：https://github.com/zengqinglei/leistd-net/pull/170

## 1. 需求

### 问题与背景

CRM V2 三仓在 2026-10-24 反馈：模板通知铃铛的"清空全部"可以重复进入。连点会弹两个确认框；确认后、请求返回前再点一次，会再发一次删除。"全部标为已读"已经用 `markingAllRead()` 禁用了按钮，两者不一致。

审视结论见协调目录 `/Users/quincy/orca/workspaces/.crm-v2-adaptation/backlog-20261019/framework-receipt-notification-bell-guards.md`。要点：

- Spartan 没有专门的防重复点击 API。官方写法是触发按钮 `[disabled]` 绑定进行中状态，按钮内放 `<hlm-spinner data-icon="inline-start" />`。`BrnButton` 只有 `disabled` 一个输入。
- 模板里的普通按钮、弹窗表单、整页表单都已符合这个写法。缺口只在"先确认、再写"的流程：
  - 进行中状态在确认之后才置位，或者根本没有；
  - 确认框打开期间、以及确认后请求在途期间，触发按钮都还能点。
- 后果：
  - 重复删除，多一条 404 提示；
  - 重置密钥重复执行，先生成的密钥会失效，两次响应乱序时界面展示的可能正是已失效的那一个；
  - 模拟登录重复执行，会多开一个会话。
- 写请求不在前端取消：前端中断请求撤销不了服务器上的操作。确认框点"确定"即关闭，不在框里等待请求，也就不会把用户困在慢请求里。

用户 2026-10-24 确认采用收敛方案。不采纳的部分及理由见回执 §2.2–§2.5。

### 目标与非目标

- 目标：所有"先确认、再写"的调用点，进行中从点击起算，直到请求结束；前端规范补一句约定。
- 非目标：
  - 不改 `ConfirmService` 与 `ConfirmDialog`；
  - 不在确认框内执行请求，不中断写请求；
  - 不做跨标签页主体检测，不按主体过滤失败提示，不按组件销毁吞掉回调；
  - 不改后端、框架、Mock 数据。

### 功能要求

**统一写法**：
1. 入口判断：进行中则直接返回。
2. 同步置位进行中，在第一个 `await` 之前。
3. 打开确认框。
4. 用户确认后发请求。
5. 在**实际 HTTP 请求结束时**复位，取消、成功、失败都要复位。

触发按钮 `[disabled]` 绑定进行中状态，进行中时按钮内显示 `hlm-spinner`。行内动作的粒度沿用 `login-devices` 的 `busy`（在途行 id）：本行显示 spinner，同类按钮一并禁用。

**异步链的释放责任**（Codex 阶段 1 意见 1）：
- 复位必须跟在实际的 HTTP 请求之后，不能跟在 `subscribe()` 的调用或返回 `void` 的辅助方法之后。
- 可以 `await firstValueFrom(请求)` 后在 `finally` 复位；也可以保持 `subscribe` + `finalize`，确认被取消时立即复位，确认后把复位交给请求链。不强制统一语法。
- `try` 的范围要包住确认框的打开与取消返回。
- 走身份重认证的链（`external-logins.unlink`、`two-factor-email-settings.turnOff`、`users.handleResetTwoFactor`），进行中状态要跨过重认证与带凭证重试，直到最终请求结束。重试入口不能被新加的入口判断挡掉。
- 已有忙碌状态的辅助方法（`users` 的 `runBatch` / `batchBusy`），沿用它的置位与释放，不另加第二个状态，也不能让两处同时释放。
- 不新增通用的任务执行器，不改 `ConfirmService`。

**确认后的写请求不随组件销毁取消**（Codex 阶段 1 意见 2）：
- 本次涉及的确认后写链，移除写订阅上的 `takeUntilDestroyed`，或改用不受销毁影响的 `firstValueFrom`，让请求在离开页面后照常完成，并照常给出成功或失败反馈。
- 只改这些链。读取、查询、定时器等仍沿用原有的销毁清理，不扩成全仓写请求改造。
- 不加 `destroyed` 判断。

**触发控件的接线**：
- `open-application-table`、`role-table`、`tenant-table`、`user-table` 四个 widget 的按钮，要经现有的父子输入接到父页面的进行中状态。
- 宽屏的直接按钮和窄屏"更多"菜单里的入口都要覆盖。
- 原有的禁用条件保留并合并，例如内置角色、非 confidential 客户端、停用租户；不能用单一的 busy 取代权限或资格条件。

**调用点**：全仓 `git grep -n -E 'ConfirmService|confirm\.open|confirmService\.open\('`，生产代码共 9 个文件、18 处确认调用（Codex 2026-10-24 核对，scripts 与 `.github` 中没有）：

| 文件 | 方法 |
| --- | --- |
| `layout/components/notifications/notifications.ts` | `clearAllNotifications`（新增 `clearingAll`）；另外 `markAllNotificationsRead`（不弹确认框）入口补 `markingAllRead()` 判断 |
| `features/account/components/login-devices/login-devices.ts` | `revoke`、`revokeOthers`（`busy` 改为点击时置位） |
| `features/account/components/external-logins/external-logins.ts` | `unlink`（经重认证） |
| `features/account/components/two-factor-email-settings/two-factor-email-settings.ts` | `turnOff`（经重认证） |
| `features/platform/components/open-applications/open-applications.ts` | `handleDelete`、`handleResetSecret` |
| `features/platform/components/roles/roles.ts` | `onDelete` |
| `features/platform/components/tenants/tenants.ts` | `onToggleActive`、`onImpersonate`、`onDelete` |
| `features/platform/components/tenants/widgets/tenant-detail-dialog/tenant-detail-dialog.ts` | `removeConnection` |
| `features/platform/components/users/users.ts` | `handleToggleActive`、`handleDelete`、`handleUnlock`、`handleResetTwoFactor`（经重认证）、`batchSetActive`、`batchUnlock`（沿用 `batchBusy`） |

不扩展到单条通知的标已读、删除，也不顺带改写普通表单。

**规范**：在 `template/docs/standards/coding-frontend.md` §5"状态"补一句，大意："需要确认的写操作，进行中从点击起算，确认框打开期间也在内，直到实际请求结束；确认框点'确定'即关闭，不在框内等待请求。"按钮的禁用与 spinner 在同一句点明，不在 forms、ui、spartan 多份文件里重复写。
- 读取成本余量很小（fullstack、UI 两类只剩 1 字符），要压缩同一文件的冗余措辞来抵消，不提高上限，并以最终正文重新测量。
- 文档里的条件块（`<!--#if-->`）按现有规则处理。

### 验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| V1 | Given 铃铛面板，When 点"清空全部"，Then 按钮立即 `disabled`；确认框打开期间再点，不出现第二个框；确认后请求在途期间再点，不发第二次请求；请求结束后恢复 | `notifications.spec.ts` |
| V2 | Given 确认框打开，When 用户取消，Then 按钮恢复，可以再次操作 | `notifications.spec.ts` |
| V3 | Given 其他调用点，When 点击触发按钮，Then 立即 `disabled`，取消后恢复 | 9 个组件都已有 spec，在其中补断言；四个 table widget 改了输入与模板的，同步核对它们的 spec |
| V3a | Given 一条 Observable 在途的链（如 `roles.onDelete`），When 确认后请求未返回，Then 状态仍为进行中；请求返回后复位 | 已有 spec，用未结束的请求观察 |
| V3b | Given 一条重认证链（如 `external-logins.unlink`），When 首次请求要求身份确认并带凭证重试，Then 重试照常执行，进行中直到最终请求结束 | 已有 spec |
| V3c | Given 一条确认后写链，When 请求在途时组件销毁，Then 写订阅仍收到结果（请求不被取消） | 一条代表链的已有 spec |
| V3d | Given 铃铛清空全部失败，Then 提示错误，且状态复位、可以再次操作 | 复用 `keeps the panel open and surfaces an error when clearing all fails`，补断言 |
| V4 | Given 规范新增的这一句，Then 读取成本闸门与文档引用闸门通过，上限不变 | `check-all -Only` 相关闸门 |
| V5 | Given Mock 站点，When 逐个触发上表 18 个入口（用 `MockConfig.delay` 在临时验证配置里模拟慢响应，不改 Mock 数据与持久配置），Then 点击即禁用并显示 spinner，确认即关闭，按钮持续禁用到请求结束，页面其他操作不受影响；窄屏"更多"菜单至少验证一个代表入口；模拟登录成功会整页跳转，观察到跳转为止 | 浏览器人工验收，逐项记录，附截图 |
| V6 | Given 选择器给出的场景，Then `-GenerateOnly` 与受影响的 spec、lint 通过 | 本地验证配方 |

### 已确认的决策

用户 2026-10-24 确认：
- 采用回执中的收敛方案；
- 沿用此前的流程：方案交 Codex 审查直到明确通过，再由 Codex 实施与验证，Claude 审代码后合入 develop。

### 方案

- 按上表逐个调用点补齐统一写法，复用各组件已有的进行中状态名（如 `busy`、`submitting`、`batchBusy`），没有的才新增。
- spec 写法：
  - 用可以手动 resolve 的确认 Promise 和未结束的请求，分别观察"确认中"和"HTTP 在途"两个窗口；
  - 同步再次调用入口并核对确认与请求的次数，证明入口判断有效；只对已 `disabled` 的元素再 click，证明不了这一点；
  - DOM 稳定后断言真实按钮的 `disabled` 与 spinner；
  - 键盘的 Enter / Space 在真实按钮上验证。
- 确认即关闭，指立即开始既有的关闭流程，不要求去掉正常的退出动画。
- spinner 的位置与大小沿用同组件里已有的写法：模板里普遍是 `class="text-base" data-icon="inline-start"`。
- 只改模板前端，不出框架 beta；模板正式包随下一次 develop→main 同号发布。

## 4. 结果

### 实际交付

- 9 个调用组件、18 个确认入口按统一写法补齐。
  - 新增的状态名：铃铛 `clearingAll`；open-applications `deletingId`、`resettingSecretId`；roles `deletingId`；tenants `activationId`、`impersonatingId`、`deletingId`；tenant-detail-dialog `removingConnection`；users `activationId`、`deletingId`、`unlockingId`、`resettingTwoFactorId`。
  - 复用的状态：login-devices、external-logins、two-factor-email-settings 的 `busy`，users 批量操作的 `batchBusy`。
- 四个 table widget 新增同名输入，宽屏按钮与窄屏菜单都接上，原有的资格与权限禁用条件保留。
- 直接的写链用 `firstValueFrom` 等到实际响应；重认证链由请求的 `finalize` 与身份确认框的关闭交接释放。这次涉及的确认后写链移除了 `takeUntilDestroyed`。
- `template/docs/standards/coding-frontend.md` §5 新增一句；在同一文件精简同义措辞抵消篇幅，读取成本上限不变。

### 与计划的偏差

- 阶段 1 审查（Codex）两条意见：异步链的释放责任、确认后写链不随组件销毁取消。另补充了 18 个入口的清单、widget 接线、规范位置与测试写法，全部采纳，规格据此修订（V3a–V3d）。
- 代码审查（Claude）一处：规范压缩时删掉了实质内容（命名约定出处、"简单父子组件"的限定、Angular CLI），已恢复，只保留同义压缩。

### 验收记录（证据在 `.tmp/confirm-in-flight/`，未提交）

| 项 | 结果 |
| --- | --- |
| V1–V3d | 目标 spec 13 个文件、183 个用例通过。铃铛的新测试在修改前红灯（确认框打开 2 次，应为 1 次） |
| V4 | 文档锚点、引用存在性、读取成本闸门通过。读取成本 fullstack 70570/70570、UI 52862/52862，上限不变 |
| V5 | Mock 站点 4 秒延迟，18 个入口逐项通过，含窄屏键盘的 Enter / Space；模拟登录观察到整页跳转为止 |
| V6 | 选择器给出 6 个场景，`-GenerateOnly` 通过；lint 通过（`styles.css` 有 4 条既有 Stylelint warning，未改动） |

### 遗留项

- 读取成本 fullstack、UI 两类已用满上限。下一次增加前端规范内容时，需要先压缩，或者按 `docs/template/development-guide.md` §4 连同理由提高上限。
- 其他写请求（非确认后的写链）仍有挂着 `takeUntilDestroyed` 的，不在本次范围。

### 已上收的常驻文档

`template/docs/standards/coding-frontend.md`。
