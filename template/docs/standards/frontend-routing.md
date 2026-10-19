# 前端路由规范

路由守卫、跳转与地址栏的规范。启动失败与错误页的处置见[错误处理](./coding-frontend.md#6-错误处理)。

## 1. 路由守卫与地址栏

- 权限不足时，`permissionGuard` 跳到 `/forbidden?target=<被拒的目标>`，地址栏保留目标（`browserUrl`）。Router 会把"显示的地址与实际路由不同"记进历史条目，刷新、前进或后退回到这一条时，直接还原出 403 页，不经过目标路由的守卫。`/forbidden` 上的 `forbiddenGuard` 把这类还原送回目标重新判定：其间获得的权限刷新后即生效；仍无权限则回到 403 页。整个过程替换当前历史条目，不新增。
- 内部地址带上目标，是为了让相邻两条 403 历史彼此不同，否则 Router 视为同一地址而跳过导航；路由同时声明 `runGuardsAndResolvers: 'paramsOrQueryParamsChange'`，复用无权限页时也重新执行入口守卫。不为此改 Router 全局的 `onSameUrlNavigation`。
- 原地显示 404 页的通配路由（模板中为根通配）不用 `browserUrl`：地址栏就是用户输入的地址，刷新时重新匹配。
- 新增带 `browserUrl` 或 `skipLocationChange` 的跳转，要写明刷新与前进/后退时的预期，并补测试。
- 判断"这次导航从哪来"只用公开的导航信息（`Router.currentNavigation()` 的 `extras`，含只属于本次导航的 `info`），不读写 Angular 的私有状态键（`ɵ` 前缀，如 `history.state` 里的 `ɵrouterUrl`）：它们随版本变化，没有兼容承诺。
- 权限守卫的测试覆盖放行、拒绝，"拒绝 → 授权 → 刷新后进入目标"与仍被拒的刷新，以及在两条相邻 403 历史之间前进/后退（见 `frontend/src/app/core/guards/permission-guard.spec.ts`）。
