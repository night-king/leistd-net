# 前端开发规范

遵循[通用约定](./coding-common.md)；界面与样式见[前端界面规范](./frontend-ui.md)。
<!--#if (IncludeLocalization)-->
多语言见[前端多语言规范](./frontend-i18n.md)；`scripts/check-i18n.py` 检查词条、引用与 scope 登记。
<!--#endif-->

## 1. 技术栈

Angular（Signals、zoneless、Angular CLI）、TypeScript、Spartan UI（`@spartan-ng/brain` 无头基元 + 项目持有的 helm 样式层）、Tailwind CSS 4、Angular Signal Forms（`@angular/forms/signals`）、TanStack Table。版本以 `frontend/package.json` 与锁文件为准。表单写法见[前端表单规范](./frontend-forms.md)，`FormsModule`/`ReactiveFormsModule`/`ngModel` 由 eslint 拦截。

## 2. 目录与依赖方向

目录按职责划分，按需建子目录，不预建空层级。

```text
frontend/
├── _mock/                 # 独立开发用的 API 处理器与数据
├── libs/ui/               # 项目持有的 Spartan Helm 组件
├── src/app/
│   ├── core/              # 应用级服务、认证、拦截器、启动、错误处理、应用级组件
│   ├── features/<x>/      # 业务功能：components/<page>/widgets、services、dtos、models、guards、<x>.routes.ts
│   ├── layout/            # 布局、导航与布局内组件
│   ├── shared/            # 无状态的展示组件、指令、管道、工具与跨功能契约（dtos、models、constants、components、directives、pipes、utils）
│   ├── app.config.ts、app.interceptors.ts、app.routes.ts、app.ts
├── src/environments/      # 公共配置与各部署环境的覆盖
└── public/                # 构建后映射到站点根的静态资源
```

`frontend/eslint.config.mjs` 按解析出的真实文件检查依赖方向，规则以报错为准：`shared` 只依赖自身，`core` 只依赖 `core` 与 `shared`，`layout` 与各功能另可依赖自身，功能之间只有路由文件可经 `loadComponent`/`loadChildren` 懒加载；业务代码中只有应用装配点（`app.*.ts`）引入 `_mock`，各目录可读取 `environment`，测试文件可引用 `_mock`，不放开其他方向。导入路径写普通字符串字面量。

- 多个功能共用的服务或契约下沉：有状态或应用级的放 `core`，无状态的展示组件与契约放 `shared`；依赖 `core` 服务的组件不放 `shared`。功能内复用的组件放该页面的 `widgets/`。
- 业务代码判断请求是否走 Mock 时注入 `frontend/src/app/core/mock/mocked-url.ts` 的 `MOCKED_URL`（默认 `false`，Mock 构建由 `provideMock` 提供），不调用 `_mock` 中的函数。

## 3. 命名

文件名 kebab-case，遵循 Angular 简化风格：

| 类型 | 文件 | 类名或导出 |
| --- | --- | --- |
| 组件 | `{name}.ts`（模板 `.html`） | 不带后缀 |
| 服务 | `{name}-service.ts` | 带 `Service`，如 `UserService` |
| 指令 | `{name}.ts` | 不带后缀；selector 为 `app` + camelCase 属性 |
| 管道 | `{name}-pipe.ts` | 如 `AppDate`；名称见 `@Pipe` |
| 守卫 | `{name}-guard.ts` | 函数式守卫，如 `authGuard` |
| 拦截器 | `{name}-interceptor.ts` | 函数式拦截器，如 `httpErrorInterceptor` |
| 错误处理器等类 | `{name}-handler.ts` | 类名带 `Handler` |
| 布局 | `{name}-layout.ts` | 如 `DefaultLayout` |
| API 契约 | `{name}.dto.ts` | `*Dto` 接口 |
| 前端模型 | `{name}.model.ts` | 需要行为或派生字段的类型 |
| 常量 | `{name}.constants.ts` | 跨功能放 `shared/constants` |
| 路由 | `{name}.routes.ts` | |
| 测试装配 | `{name}.testing.ts` | |

组件 selector 用 `app-` + kebab-case（eslint 检查）；API 请求与响应须有类型定义。

- JSDoc 按需补服务边界、状态归属和协议契约，不逐成员翻译名称；`@param`、`@returns` 只补单位、空值、失败等非显然信息，不重复 TypeScript 类型。
- 实现原因用独占行 `//`，保留订阅清理、竞态、安全与可访问性约束；模板、样式不写显然的区块标签。
- 上游 Skill 与 helm 原样内容遵循上游风格，不为注释精简制造定制差异；本地改动见[Spartan 维护约定](./frontend-spartan.md)。

注释原则参考 [Angular 框架规范](https://github.com/angular/angular/blob/main/contributing-docs/coding-standards.md#write-useful-comments)与 [TypeScript JSDoc](https://www.typescriptlang.org/docs/handbook/jsdoc-supported-types.html)。

## 4. 分层与依赖注入

- **API 数据访问服务**（功能的 `services/*-service.ts`）只封装 HTTP，返回 DTO 的 `Observable`，不持有页面状态。
- **应用级服务**（认证、主题、语言、设置上下文、启动）放 `core/services`，可持有自身状态与行为。
- **页面状态**优先放组件 signal；状态复杂且有复用或生命周期收益时，抽成组件级 `@Injectable()` 状态类并在组件 `providers` 提供。
- **DTO 与模型**：API 契约放 `dtos/`，只有需要行为或派生字段时才在 `models/` 建前端模型并显式转换；不在 `models/` 放 DTO。
- 路由守卫与按钮显隐只改善交互体验；服务端必须对每个请求独立校验权限。
- **作用域选择**：跨页面共享或应用级单例用 `providedIn: 'root'`；随页面销毁的状态用组件 `providers`；需要跨子路由保留的用路由 `providers`（如 `provideTranslocoScope`）。
- 依赖注入一律用 `inject()`。构造函数只做属性赋值、需要注入上下文的生命周期接线（`effect()`、`takeUntilDestroyed()`、随 `DestroyRef` 释放的订阅）与进入页面的首次只读查询；写请求、多步流程和清理会话放 `ngOnInit`（判据：构造不改变服务端或会话状态）；在注入上下文之外（如 `ngOnInit`）调用这些 API 时显式传入 `destroyRef` 或 `{ injector }`。

## 5. 状态

- 组件内部与简单父子组件间的状态用 Signals。
- 确认写入口点击即禁用并显示 spinner（含确认框打开期间），实际请求结束才复位；确认即关框，不等请求。
- 媒体查询（断点、系统暗色偏好）用 CDK `BreakpointObserver` 转成信号，不手写 `matchMedia`。
- 启动流程、拦截器判断当前路由用 `frontend/src/app/core/routing/entry-route-service.ts`，不读 `Router.url`（初始导航前恒为 `/`）。

## 6. 错误处理

- `http-error-interceptor` 负责认证处置（401 跳转）并把 RFC 9457 Problem Details 归一化为 `ApplicationHttpError`（`errors` 兼容框架的数组与官方字典两种形状）；页面不自己猜测响应形状。
- 发起操作的 feature 决定反馈：字段错误优先回填到表单，其余错误以归一化后的 `message` toast（`@spartan-ng/brain/sonner`），或显示空状态、静默；同一错误不重复提示。
- 公开文案只取 Problem Details 里校验过的字段错误、`detail`、`title`（5xx 限带码读请求），不从 `statusText`、正文或原始异常回落；其余按 `category` 用本地化通用文案，有 `traceId` 时附上。拦截器传入方法：写请求遇到无响应、5xx 或 2xx 解析失败归为 `uncertain`，提示可能已完成、先核实，不自动重放。
- `GlobalErrorHandler` 只兜底未处理的非 HTTP 错误，识别并忽略已归一化的 HTTP 错误。
- 状态码与 `code` 的分工：认证与协议层处置（401 跳转、受限会话的 403、启动时的未登录判定）由 `http-error-interceptor` 与 `startup-service` 按状态码处理，eslint 只对它们放行 `local/no-status-code-branch`；feature 使用归一化后的错误反馈，需要特定业务交互时按稳定 `code` 分支。业务错误缺少 `code` 不构成 feature 按状态码分支的理由，应在后端补码。
- 启动失败（非 401）时守卫取消导航并记下目标，重试成功后进入；`/forbidden` 与 404 页地址栏保留目标（刷新与前进/后退时的行为见[前端路由规范](./frontend-routing.md)）。
- `catchError` 只处理特定错误，不吞掉错误。
- 前端不兼容框架可选的响应信封（`AddResponseWrapper()`），开启须同时改拦截器。

## 7. Mock

- 每个后端新端点同步补 Mock（`_mock/data` + `_mock/api` + `frontend/_mock/index.ts` 注册），前端可脱离后端运行。
- Mock 代码只放 `_mock`，引入规则见第 2 节；Mock 单测在 `_mock/` 与被测文件相邻。
- Mock 与后端保持可观察行为一致：认证、权限、参数校验、过滤语义与失败响应；不复制后端内部实现。
- Mock 业务数据存本页内存，重载回初始数据；会话与设置覆盖存本页 `sessionStorage`（设置用 `frontend/_mock/utils/stored-map.ts`，读写直达存储），刷新不丢。新页面初始存储由浏览器决定（保留 opener 打开、复制或恢复标签页会带入原存储），不保证新标签页未登录；之后各页会话与设置改动互不影响。

**跨层字段同步**：贯穿前后端字段的增改须逐环同步：后端入参 DTO → 应用层逻辑 → 前端 DTO → 前端服务传参 → Mock 处理器 → Mock 数据。

## 8. 测试

- 含参数映射、分支或状态的服务、`pipe` 与含复杂业务逻辑的函数必须有单元测试；只把参数原样转发给 `HttpClient` 的服务由使用它的页面测试覆盖。用例名规则见 [测试规范](./testing.md)。
- 单测跑在真实 Chromium 里，不得触发真实的下载、打印或页面跳转：对副作用那一步打桩（`saveBlob`，或 `vi.spyOn(HTMLAnchorElement.prototype, 'click').mockReturnValue(undefined)`——`vi.spyOn` 默认仍调用原实现），断言"发起了什么"，见 `frontend/src/app/shared/utils/download-file.spec.ts`。

## 9. 日期、时区与数字

时间以 UTC 传输与存储，只在展示时按会话时区换算。业务日期用 `appDate` 管道（不用 Angular 的 `date`），业务数字用 `appNumber` 管道，非模板调用方用同一套 `formatAppDate`、`formatAppNumber`，不在组件里直接 new `Intl.*Format`。展示时区的判定、日历日、格式槽位与相对时间见 [前端时间、数字、图表与推送规范](./frontend-data.md)。
