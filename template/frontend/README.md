# 前端项目

本项目基于 Angular、Spartan UI 和 Tailwind CSS 构建。目录结构、依赖方向与编码准则见 [前端开发规范](../docs/standards/coding-frontend.md)。本文的路径以 frontend 目录为根。

## 快速开始

先按 [后端说明](../backend/README.md) 启动本机后端（默认 `http://localhost:5240`），再安装依赖并启动前端：

```bash
npm ci
npm start
```

打开 `http://localhost:4200`。`npm start` 使用 `development` 配置和 `src/environments/environment.ts`，无需创建环境文件。

<!--#if (OpenIddictServer)-->
开发服务器按 `proxy.conf.mjs` 把 `/api`、`/hubs`（SignalR，含 WebSocket）以及授权服务的 `/connect`、`/.well-known` 转发给本机后端。
<!--#else-->
开发服务器按 `proxy.conf.mjs` 把 `/api`、`/hubs`（SignalR，含 WebSocket）转发给本机后端。
<!--#endif-->
浏览器只访问 4200，Cookie 与回调地址都按它生成，不需要跨域配置。后端不在默认端口时，启动前设置 `API_PROXY_TARGET`：

```bash
API_PROXY_TARGET=http://localhost:5300 npm start
```

PowerShell：

```powershell
$env:API_PROXY_TARGET = "http://localhost:5300"; npm start
```
<!--#if (RemoteTokenAuth)-->

### 联调本机 Identity 服务

本服务的登录在 Identity 服务上完成。本机联调时先按 Identity 服务自己的说明启动它（默认后端 5240、前端开发服务器 4200），本服务改用其他端口：

```bash
# 后端（在 backend 目录）
dotnet run --project src/CompanyName.ProjectName.Api --urls http://localhost:5250
# 前端（在 frontend 目录；PowerShell 写 $env:API_PROXY_TARGET = "http://localhost:5250"; npm start -- --port 4201）
API_PROXY_TARGET=http://localhost:5250 npm start -- --port 4201
```

后端 `Authentication:Issuer` 默认指向 Identity 的前端开发服务器 `http://localhost:4200/`，浏览器在那里登录。在 Identity 按 [服务间调用](../docs/standards/service-invocation.md#identity-与资源服务对接) 登记本服务的 API 资源与浏览器依赖方，本机回调为 `http://localhost:4201/api/v1/auth/signin` 与 `http://localhost:4201/api/v1/auth/signout`；ClientId/ClientSecret 按 [后端说明](../backend/README.md#对接-identity) 写入 user-secrets。
<!--#endif-->

### Mock

`environment.ts` 的 `useMock` 控制 Mock：`true` 全部由 Mock 应答；对象形态按接口开关，`include` 列出的接口走 Mock（列了 `include` 时以它为准），
命中 `exclude` 的一律走真实后端。只有 `development` 构建会把 Mock 编进包里，其他构建里 `useMock` 不起作用。

## 环境配置

配置文件位于 `src/environments/`：

- `environment.ts` - 本机开发（`npm start` 使用，Mock 只在这个配置下可用）
- `environment.prod.ts` - 部署构建（`npm run build` 默认使用）

前端产物不区分部署环境：页面与所属 API 同源，各环境的差异由后端配置承担，同一份构建产物可部署到任意环境。

### 主要配置选项

需要按环境改动的通常只有这几项（完整类型见 `src/environments/environment.base.ts`）：

```typescript
export const environment: Environment = {
  ...environmentBase,
  production: false,
  useMock: false, // true 开启全部 Mock；也可按模块传对象
  api: {
    ...environmentBase.api,
    gateway: '', // 保持空值：请求以相对路径访问同源 API，由同源部署或开发代理转发
  },
};
```

访问同源下按路由前缀分流的其他微服务时，给请求带上服务名（`GATEWAY_SERVICE_NAME` 由 `src/app/core/interceptors/url-format-interceptor.ts` 导出），拦截器会把它作为路径前缀：

```typescript
http.get('/api/v1/orders', {
  context: new HttpContext().set(GATEWAY_SERVICE_NAME, 'order-service'),
});
// → /order-service/api/v1/orders（同源，由部署代理转发到对应服务）
```
<!--#if (LocalIdentity)-->

哈希路由用 `useHash: true`（部署在无法配置回退规则的静态宿主时用得上）。
<!--#if (ExternalLogin)-->

**外部登录要求普通路径路由**：回调地址是无 fragment 的 `/auth/external-callback/{provider}`（OAuth 不允许回调地址带 fragment），
反向代理或静态宿主要把它与其余 SPA 深链一并回退到 `index.html`。启用 `useHash: true` 时外部登录不可用。
<!--#endif-->
<!--#else-->

前端不配置 OAuth 客户端，也不持有 access/refresh/id token。OIDC 的 Issuer、Audience、ClientId、ClientSecret 和 Scope 只在后端配置。
登录导航至 `/api/v1/auth/login`，回调由后端 `/api/v1/auth/signin` 消费；前端通过 `/api/v1/auth/me` 读取同源 Cookie 会话。
本形态固定使用普通路径路由，同源后端托管与开发代理仍保留；部署需为 SPA 深链回退到 `index.html`。
详见 [浏览器认证](../docs/standards/auth.md#浏览器认证)。
<!--#endif-->

## 构建项目

构建产物输出到 dist 目录，各部署环境共用同一份产物。

```bash
npm run build                   # 默认 production 配置
```
<!--#if (IncludeLocalization)-->

构建要走 `npm run build`，不要直接 `ng build`：构建完成后 `postbuild` 会用 `transloco-optimize` 预先展平并压缩词条，
生产配置据此开启 `flatten.aot`，运行时不再展平。直接 `ng build -c production` 产出的是未展平的原文件，界面上的词条会全部找不到。
<!--#endif-->

### 部署

页面与所属 API 必须同源：默认由后端同镜像托管构建产物，也可分进程部署、由网关统一外部源。同源要求见 [浏览器认证](../docs/standards/auth.md#浏览器认证)，镜像构建见 [部署说明](../docs/deploy/README.md)，转发头见 [配置参考](../docs/deploy/configuration.md#部署环境的必填项)。
<!--#if (IncludeLocalization)-->

## 多语言

词条位于 `public/i18n/`，文案归属、scope 登记与接线见 [前端多语言规范](../docs/standards/frontend-i18n.md)。
<!--#endif-->
<!--#if (IncludeNotifications || IncludeRealTime)-->

## 实时连接（SignalR）

`src/app/core/services/signalr-service.ts` 使用同源 Cookie 会话建立连接；浏览器不持有服务端访问令牌，也不将其放入 Hub URL。
<!--#if (IncludeRealTime)-->

业务事件通过 `/hubs/realtime` 发布。管理列表订阅当前作用域的资源键，后端验证对应资源权限；列表销毁或作用域变化时释放订阅。
<!--#if (IncludeNotifications)-->
通知复用同一 Hub 和同一条前端连接。
<!--#endif-->
<!--#else-->

通知通过 `/hubs/notifications` 推送。
<!--#endif-->

客户端日志默认使用 `Warning`。排障时可以临时提高等级；生产日志与代理访问日志仍须遵循项目的凭据脱敏规范。
<!--#endif-->

## 代码质量

```bash
npm run lint       # TypeScript、HTML、CSS 与格式检查
npm run lint:fix   # 自动修复
```

单独检查用 `lint:ts`、`lint:style` 或 `format`，对应修复命令加 `:fix`。Husky 与 lint-staged 在提交前执行相同检查。

## 运行测试

```bash
npm test -- --watch=false
```

测试范围与写法见 [测试规范](../docs/standards/testing.md#3-前端)。
