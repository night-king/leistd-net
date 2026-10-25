# 技术栈规范

## 1. 用途

本文记录项目技术栈、最低版本约束、升级策略和例外处理。精确版本以项目文件与锁文件为准；覆盖默认栈时说明原因和影响。

## 2. 默认技术栈

| 分类 | 默认技术 | 版本 | 说明 |
| --- | --- | --- | --- |
<!--#if (SpaFrontend)-->
| 前端框架 | Angular | 22+ | Web/UI 实现 |
| UI 组件库 | Spartan UI | 1+ | `@spartan-ng/brain` 无头基元 + helm 样式层（复制进 `frontend/libs/ui/`） |
| CSS | Tailwind CSS | 4+ | 原子化样式与布局 |
| 字体 | Geist | 5+ | `@fontsource/geist` 自托管（离线可用，回退 `system-ui`），模板设计系统一部分，项目可替换 |
| 前端语言 | TypeScript | 6.0+ | 类型安全 |
| 前端状态 | Angular Signals | 当前框架版本 | 组件级/局部状态 |
| 前端表单 | Angular Signal Forms | 当前框架版本 | `@angular/forms/signals`（仍为 experimental，需锁定 Angular 版本） |
<!--#if (IncludeLocalization)-->
| 前端多语言 | Transloco | ~8.4.0 | 运行时功能 scope；失败处理依赖 8.4 加载失败管道（回落策略抛出 `TranslationLoadError`、清理 `LoadOptions.failedCounter`）。升级次版本前须重新核对 `handleFailure`、inline loader Promise 缓存及缺词回落加载源码，并运行语言服务失败与并发用例 |
<!--#endif-->
| 数据表格 | TanStack Table | 9+ | `@tanstack/angular-table` headless 引擎；服务端 `manualPagination`/`manualSorting`，列表分页/排序/筛选状态以 URL query params 为唯一来源 |
<!--#endif-->
| 后端框架 | .NET / ASP.NET Core | 10+ | API 与业务服务 |
| ORM | EF Core | 10+ | 数据访问 |
| 动态查询 | System.Linq.Dynamic.Core | 中央包版本 | 应用层动态排序；规则见 [API 规范 §5](./api.md#5-分页规范) |
<!--#if (LocalIdentity)-->
| 通行密钥 | Fido2NetLib（`Fido2`） | 中央包版本 | WebAuthn 注册与断言的协议校验，只在 Infrastructure 的 `IPasskeyProtocol` 适配器里使用；ASP.NET Core Identity 的通行密钥绑定其自身存储且不作第二步，故不采用 |
<!--#endif-->
| 架构 | DDD 分层 | 项目约定 | Api/Application/Domain/Infrastructure |
| 数据库 | PostgreSQL | 15+ | 默认主数据存储，可覆盖 |
| 缓存 | Redis | 7+ | 可选缓存/队列 |
| 容器化 | Docker / Docker Compose | 当前稳定版 | 本地和中小规模部署 |

## 3. 覆盖与升级

- 数据库、缓存、消息队列、UI 组件库、部署平台与外部服务可以按项目实际覆盖；覆盖时在需求或架构决策中写明原因、对开发测试部署的影响，以及迁移与回滚方案。
- 引入新依赖说明收益、成本、风险和替代方案；版本升级验证构建、测试与部署兼容性。
<!--#if (SpaFrontend)-->
- 前端依赖门槛是生产依赖无 high/critical 公告（CI 审计作业，命令见 testing.md §1.1）。开发工具链的公告逐条记下引入工具、可达性、暂时接受的理由与复查时机；不用 `npm audit fix --force` 降级或未经验证的 `overrides` 清零，优先配套升级。
<!--#endif-->

规范入口见 [文档索引](../README.md)。
