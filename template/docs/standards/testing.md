# 测试规范

> 测试命令、框架和门槛以当前解决方案、测试配置和 CI 为准。本文定义选择与执行原则；项目特有的测试规则写在 `docs/project/testing.md`（首次需要时创建），不改本文。
<!--#if (SpaFrontend)-->
> 前端命令同时以本项目的 `package.json` 为准。
<!--#endif-->

## 1. 按风险选择测试

| 变化 | 最低验证 |
| --- | --- |
| 纯函数、领域规则、权限判断 | 单元测试 |
| 数据库、DI、序列化、外部适配 | 集成测试 |
| API 或前后端契约 | 集成测试，并验证成功、失败和越权 |
| 缺陷修复 | 能在修复前失败、修复后通过的回归测试 |
| 认证、授权、输入或敏感数据 | 正向与拒绝路径的安全验证 |

改共享组件、公共契约或基础设施时扩大回归范围；局部低风险改动先跑最小相关测试。

注释和文档里承诺的行为（如"刷新即重新判定"），尤其是跨导航、刷新或重试才会显现的，要有覆盖它的单测或 E2E；写下承诺时补测试，行为变了一起改。

按时机分层执行，不每次改动都跑全量：

| 时机 | 目标耗时 | 运行 |
| --- | --- | --- |
| 编辑中 | ≤ 1 分钟 | 受影响的单个测试（收窄命令见各端小节） |
| 阶段完成 / 提交前 | 以实际范围计时，通常目标几分钟 | 按 §1.1 选择测试范围；相关静态检查和构建 |
| 已配置的 CI | 由流水线定 | 当前项目配置的完整测试、静态检查、构建和关键流程 |

### 1.1 完整验证由谁承担

完整回归入口 `pwsh scripts/verify.ps1`（需 Docker），本地与 CI 共用；增删步骤只改它。
`pwsh scripts/verify.ps1 -Fast` 是本地快速检查：只跑随包静态检查、后端还原、构建与风格检查和单元测试，不跑集成测试，不需要 Docker；`-List` 列出两种模式的步骤。
<!--#if (SpaFrontend)-->
`-Fast` 的前端只跑 lint，不做依赖安装、单测、构建与端到端，须已安装依赖（缺失时先在 `frontend/` 执行 `npm ci`）。
生产依赖审计不在 `verify` 内（CI 的 `verify` 作业在锁文件变化、定时、手动时另跑一步）；改前端依赖后在 `frontend/` 运行 `npm audit --omit=dev --audit-level=high --package-lock-only`。
<!--#endif-->

阶段完成时默认运行受影响测试项目全量，**包含集成测试**，并执行相关 lint、构建及关键运行验证；可用已有隔离环境，不能假定 CI 已接替。

<!--#if (Ci == "github")-->
`.github/workflows/ci.yml` 调用 `verify`；分支保护或规则集把 `verify` 设为必需检查后才算必过。推荐同时要求分支保持最新或用 merge queue（工作流已含 `merge_group` 触发器）：待合并的结果已验证。默认分支推送仍完整验证，通过后才发布镜像。
<!--#elseif (Ci == "gitlab")-->
`.gitlab-ci.yml` 调用 `verify`（Runner 前提见文件头）；开启 Pipelines must succeed 后才算必过。推荐快进合并：被测提交即合入提交。默认分支流水线仍完整验证，通过后才发布镜像；缓存默认不在受保护与非受保护分支间共享，命中取决于项目缓存设置。
<!--#endif-->

项目把 CI 设为**必过检查**后（流水线文件存在不算；在本规范记录入口、范围与阻止合并的设置），本地以 `pwsh scripts/verify.ps1 -Fast` 加目标集成测试为准，完整回归由 CI 承担。

数据库、事务、租户、HTTP 授权、协议或网络取消变化尽早验证对应真实路径。

### 1.2 按开发场景选择范围

| 场景 | 编辑中 | 阶段完成 / 提交前 | E2E |
| --- | --- | --- | --- |
| 小 bug 修复 | 目标回归测试证明修复前失败、修复后通过；必要的目标构建 | 按 §1.1 承担受影响测试项目回归 | 受影响分组 |
| 新功能模块 | 领域/应用规则单测、相关接口或组件测试 | 模块成功/失败路径、需要认证的接口 401/403；新增注册验证生命周期与重复登记 | 受影响分组 |
| 跨模块需求 | 修改规则及直接调用方测试 | 修改模块及依赖方集成测试，跨模块 API/事件契约；涉及事务或副作用时验证回滚或一致性 | 受影响分组 |
| 数据库、迁移或租户路由 | 目标真实 PostgreSQL 集成测试 | 按变化验证迁移、模型快照、约束、查询和隔离；并发/独立事务变化补相应路径 | 完整 |
| 认证、授权或协议 | 目标规则及拒绝路径 | 真实宿主、授权边界；涉及 Cookie、跳转、令牌、跨服务或退出时执行已有协议/浏览器闭环或真实运行验证 | 完整 |
<!--#if (SpaFrontend)-->
| 前端局部变更 | 目标 spec | 受影响测试、lint、构建；共享组件、状态或 API 契约变化扩大到调用方 | 受影响分组 |
<!--#endif-->
| 文案、项目规范或可执行示例 | 引用、格式及对应静态检查 | 涉及可执行示例、生成或运行契约时检查实际交付内容，不仅按扩展名选择 | 不跑 |

E2E 列三个取值依次对应项目 Skill 审查与测试流程中的低、中、高风险档；分组即用例的 tag。

新模块涉及权限才验证权限登记与种子，涉及持久化模型才验证迁移与快照，涉及本地化才检查词条键。关键用户流程没有已配置的 E2E 入口时，写明启动条件、操作、预期结果并真实运行验证，不杜撰命令；引入 E2E 工具先获确认。

## 2. 后端

从 `backend/` 的解决方案或目标测试项目执行：

```bash
dotnet test                                                       # 全部
dotnet test tests/CompanyName.ProjectName.UnitTests               # 只跑单元测试，秒级
dotnet test tests/CompanyName.ProjectName.IntegrationTests --filter "FullyQualifiedName~<测试类名>"
```

### 2.1 两个测试项目，分工由成本决定

```text
backend/tests/
├── CompanyName.ProjectName.UnitTests/          不建宿主、不连库、不发 HTTP
│   ├── Domain/          领域规则、值对象、策略判定
│   ├── Application/     应用层契约与纯逻辑，依赖用假实现从构造函数传入
│   ├── Infrastructure/  基础设施纯逻辑（错误翻译、选项校验、访问口径），不连库
│   ├── Api/             宿主层里的纯逻辑（协议交互凭据、限频装饰器），不建宿主
│   └── Registration/    在 IServiceCollection 上断言注册结果
└── CompanyName.ProjectName.IntegrationTests/   经真实宿主与真实 PostgreSQL 验证端到端行为
    ├── Fixtures/        ProjectWebApplicationFactory、PostgreSqlTestDatabase、共享装配基类
    └── <各 *Tests.cs>
```

集成测试跑在真实 PostgreSQL 上，需要本机 Docker：`PostgreSqlTestDatabase` 每次运行起一个容器并用项目迁移建好模板库，每个 `ProjectWebApplicationFactory` 克隆独立库，唯一约束、查询翻译、事务回滚与独立事务因此与生产一致。单元测试不连库。

远端运行时把源码与测试进程放到同一执行节点，使用该节点的 Docker；不要仅切换 Docker context 后继续使用本机路径。集成测试支持可选的执行器归属环境变量 `RPR_DOCKER_LABELS`（JSON 字符串标签）和 `RPR_DOCKER_NAME_PREFIX`。两者必须同时提供，标签包含 managed=true、task、attempt 的 `remote-project-runner.*` 字段；PostgreSQL 容器使用唯一名称并保留这些标签。未提供时沿用 Testcontainers 默认行为。错误或不完整的协议在创建容器前失败。

Testcontainers 的 Ryuk 辅助容器继续由框架管理，不套用 PostgreSQL 的名称和标签；保留 Resource Reaper，执行器验收需同时确认 PostgreSQL 与本次 Ryuk 均已回收。测试进程被取消或断链时也不能关闭 Resource Reaper 来绕过归属检查。

每个宿主都要初始化、迁移、克隆库与播种，因此：

- 能在单元测试里验证的规则不放进集成测试（分界见 §1）。
- 集成测试每类一个 `IClassFixture<ProjectWebApplicationFactory>`，**不逐用例建宿主**。
- 改配置的用例用 `WithWebHostBuilder` 派生宿主，**同类配置变体归组复用**；断言精确用户名、全表计数或修改租户级设置等依赖空库的用例例外，保留独立宿主。
- 注册阶段读取的键（连接串、锁与缓存选择、密钥路径）用 `UseSetting` 注入；`ConfigureAppConfiguration` 在 `Program` 注册服务时尚不可见，会被静默忽略。
<!--#if (LocalIdentity)-->
- 与被测行为无关的宿主固定成本要压低：`ProjectWebApplicationFactory` 把 `PasswordHash:IterationCount` 调到 1000，生产值由 `PasswordHashingTests` 验证。
<!--#endif-->

### 2.2 通用要求

- 测试方法名用英文句子、首字母大写、单词以下划线分隔，写出行为与条件；不用中文标识符。名字装不下的前因写进 XML 注释。断言说明、失败消息和测试里抛出的异常（含种子、夹具）等开发者诊断消息用英文，由 `scripts/check-test-diagnostics.py` 检查；被断言的本地化内容、注释、测试数据与人工验收输出不受此限。
- 实体配置、唯一索引与全局查询过滤器在集成测试里验证，不用 EF InMemory 或 SQLite 代替（前者放过违反的约束与不可翻译的查询，后者锁死独立事务）。
- 批量 `ExecuteUpdate` / `ExecuteDelete` 不经过变更跟踪器：断言结果时换一个作用域读，否则读到仍被跟踪的旧实体。
- 外部服务用 fake、mock 或明确的测试环境；日志用 `FakeLogger`，不手写替身。
- 时间边界（锁定、验证码与令牌有效期、限频窗口）用 `FakeTimeProvider` 或显式时刻验证，不靠真实时间流逝。集成测试在 `ConfigureTestServices` 里 `RemoveAll<TimeProvider>()` 后登记假时钟（起点取当前时刻），`IClock`、Cookie 认证与 OpenIddict 随之跟随。缓存过期不跟随该时钟：测 TTL 保留真实 MemoryCache/HybridCache，经 `MemoryCacheOptions.Clock` 接入同一假时钟，不用主动删除冒充到期；Redis、Data Protection 限时保护器与 OIDC 处理器的寿命校验用各自机制，不手写判定快进。PostgreSQL 时间只存到微秒：固定时钟起点按微秒对齐，持久化时刻换新作用域读回再比较，不放大容差。
- 端到端只验接线与生效值，不等安全窗口过期；确需观察到期时用配置缩短窗口（如 `OAuth:AccessTokenLifetime`，见[配置参考](../deploy/configuration.md#生产边界)）并断言已生效；有上限的等待（网络、取消、超时）不在此列。
- 领域规则、状态变化、权限和错误语义应通过可观察行为断言。
- 各层注册入口覆盖注册结果、生命周期与相同登记重复调用不重复生效，有意覆盖组件默认实现的登记验证两种调用顺序；有注册或配置决策（选择实现、派生 Options）的宿主扩展测行为，单纯转调由启动集成测试覆盖（见[后端开发规范 §4](./coding-backend.md#4-依赖注入)）；注册面是契约，编译期看不出错。

### 2.3 后台维护任务

见 [后台任务规范 §2](./background-jobs.md#2-测试)。

<!--#if (SpaFrontend)-->
## 3. 前端

从 `frontend/` 使用项目已声明的脚本：

```bash
npm test                                            # 全部 spec
npm test -- --include src/app/core                  # 只跑某个目录或文件
npm run lint
npm run build
```

单测由 Vitest 在 Playwright 驱动的真实 Chromium 里运行（无头）。新机器首次运行前安装一次浏览器：`npx playwright install chromium`。`angular.json` 的 test 目标开启 `isolate`（每个 spec 文件独占页面，避免全局桩跨文件串扰）并使用不做 Mock 替换的 `unit-test` 构建配置，不要关闭或改用开发配置。单测 setup 已预载全局网页字体，量宽不必再等；用例另外登记的字体须先加载。

保存状态最短时长与搜索防抖使用 Vitest 假计时器验证边界，不睡真实业务时长。先完成宿主／路由初始化，再伪造 Date、timeout 与 interval（RxJS 防抖使用 interval），保留原生 rAF、performance 和微任务；用 `vi.advanceTimersByTimeAsync` 推进，配合 fixture 稳定与 DOM 断言。边界期望独立于生产常量，teardown 清理业务计时器并恢复真实计时器；HTTP 验证与真实 Chromium 隔离保留。物理指针不会自动重置：需排除非本次操作的悬停时（提示由焦点打开、悬停后关闭、断言无提示），渲染或假计时器前调 `frontend/src/app/shared/utils/pointer.testing.ts` 的 `parkPointer()`；合成事件不能代替。

- `describe` 写被测对象；`it` 用英文句子，小写开头，写出行为与期望（如 `keeps the dialog open when saving fails`）；中文只出现在注释与测试数据里。名字装不下的前因后果写进上方注释。
- service、pipe、复杂状态和共享组件覆盖输入、输出、空态与错误态。
- HTTP 调用使用 mock，除非当前任务明确执行前后端集成验证。
- 表单覆盖校验、提交、防重复操作和失败反馈。
<!--#if (LocalIdentity)-->
- 端到端：`npm run e2e`（Playwright），需先完成后端 Release 与前端构建；环境由 `frontend/e2e/global-setup.ts` 自建。只有登录用例走界面，其余复用接口登录的会话；按 role/label 定位。本地不重试，失败 trace 在 `test-results/`；CI 上失败用例最多重试两次并在首次重试时抓 trace，重试后才通过的标为 flaky，不阻断但要排查（报告与 trace 在 CI 产物里）。按分组运行：`npx playwright test --grep @组名`。
<!--#endif-->

<!--#endif-->

## 4. E2E 的范围

E2E 只放真实部署才能证明的东西：真实界面的登录与会话、跨进程或跨服务的接线（含启用时的实时推送）和一条主流程冒烟。
权限矩阵、越权、审计字段与软删除由集成测试负责（真实 PostgreSQL + `WebApplicationFactory`）。

判定原则：

- 共享实现只验一次。
- 同一主题的变体只留主路径。
- 时间边界交给假时钟或假计时器，截止前、恰好截止、截止后三点各验一次，不等真实时间。
- 等待有上限并观察目标完成，不用固定睡眠；断言"没发生"前先建立因果屏障（同链路回执、目标初始化完成），建不了的下沉到组件或单元测试。
- 用例按行为命名，不按轮次命名。

下沉或删除测试时，指出已覆盖同一行为的测试。

## 5. 静态闸门

有些错漏不让任何断言变红，只让契约或界面悄悄不对；这类判据做成脚本，跟测试一起跑（Windows 上把 `python3` 换成 `py`）；每个脚本另带 `--self-test` 检查判据本身是否还成立，判据见各脚本文件头：

```bash
python3 scripts/check-error-codes.py                      # 错误码形态、归属与引用
python3 scripts/check-test-diagnostics.py                 # 测试诊断消息不含中日韩字符
<!--#if (IncludeLocalization)-->
python3 scripts/check-i18n.py                             # 词条、引用与写死文案
<!--#endif-->
<!--#if (SpaFrontend && IncludeOperationRecords)-->
python3 scripts/check-operation-action-i18n.py            # 每个动作码都有句子模板
<!--#endif-->
```
<!--#if (SpaFrontend && IncludeOperationRecords)-->
每个动作码都须有 `operationRecords.actions.<码>` 词条（单向，多余词条不报）；漏配时界面只把那一行显示成裸码（`role.deleted`）。新增动作码时同一提交补中英句子。
<!--#endif-->

后端代码风格（日志模板、命名空间、未使用的 using 等）由 `backend/.editorconfig` 在构建期检查，以 0 警告为准（IDE0001/IDE0002 由 `verify` 的 `backend-format` 核对），确需违反用 `[SuppressMessage]` 写明理由；控制器授权、路由前缀与实体租户归属由三个 `*ConventionTests` 检查，豁免写在测试白名单里并注明理由。

新增这类闸门时一并写 `--self-test`：判据自己失效后，它给出的每次"通过"都是假的。脚本之间传递判定结果用退出码、结构化字段或稳定标识，不匹配展示文字；闸门自身的输出可随时改写。

## 6. 数据与结果

- 测试数据可重复构造，不依赖执行顺序，不使用生产密钥或真实客户数据。
- 会写数据库的测试说明前置条件并清理可变数据。

## 7. 测试环境验收

按规格文档 §1 的验收标准（Given/When/Then）验收，没有规格时按 PR/MR 的预期行为：

- 已由自动化测试覆盖的条目只做冒烟；
- 标注"人工验收"的条目逐条取证；
- 结论（环境、版本、结论、验收人）写进规格 §4 或 PR/MR，截图和日志放 MR 附件；
- 测试账号和测试数据只写获取方式，不写凭据。

跑完一条关键业务流程后核对日志链路，任一条不满足即视为日志链路未就绪：

1. 同一请求（含跨服务调用的两侧）的日志带同一个 traceId；
2. 关键业务操作的入口与出口日志成对，出口带结果与耗时；
3. 关键业务日志带可读的业务键（如单号），不只有 GUID；
4. 后台作业的日志同样带 traceId。
