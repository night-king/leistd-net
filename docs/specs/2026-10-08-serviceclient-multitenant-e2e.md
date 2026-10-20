# 多租户与 ServiceClient 多系统集成验收

状态：已完成（F01 修复见 PR #75）

**当前状态：F01 已修复并完成复测。** 下文保留首轮 9 通过、3 未通过的原始结论；修复后的包消费、I01–I12 复测及有头证据见文末「F01 官方依据、修复与复测」。I08/I09 的真实到期与会话收敛沿用首轮证据，本轮重新验证受影响的新交换错误路径。

## 基线与任务

基线为远端 `develop`：`0c71ea591bbb7ebc1a512c4be6f032191929d116`，2026-10-08 再次通过 `git ls-remote` 核对。沿用[模板功能验收](2026-10-07-template-scenario-e2e.md)的裁剪与模块事实，补齐多系统业务调用、租户停用传播及数据库布局的独立场景维度。先登记下列清单，再按编号执行；不能以登录、SSO 或生成检查代替 ServiceClient 业务闭环。

测试夹具只写入隔离快照和生成目录，不修改模板或框架实现。生成产品经隔离 NuGet 配置消费该提交实际打包的 Leistd 0.12.0。本次声明命名业务连接的生成夹具属于对框架现有契约的消费示例，不是模板默认配置，也不是产品功能修复。

## 常用业务场景与项目清单

| 场景 | 项目清单 | 能力与目标 |
| --- | --- | --- |
| 单系统租户 SaaS | 1 × Identity 或 Standalone | 本地账号、宿主租户控制面、租户级权限与设置；共享库或独立库 |
| 多租户集团业务门户 | 1 × Identity + N × Resource 前端 | 统一登录、各系统独立角色权限；跨系统业务调用由 ServiceClient 单跳委托 |
| 门户与独立 API 服务 | Identity + Resource 前端 + N × Resource 纯 API | 门户保留服务端 Cookie/令牌；纯 API 验证 Bearer；ServiceClient 交换令牌传递用户与租户 |
| 多租户 API 与后台作业 | Identity + N × Resource 纯 API + Worker/调度宿主 | ServiceClient 机器身份、最小 scope；作业显式选择租户/库，核对启停策略，不把机器身份冒充用户 |
| 共享与独立库混合 SaaS | 上述系统 + 控制库 + 各系统共享库 + 租户独立库 | 小租户共享并按行隔离，大租户分库；命名连接允许同一租户在不同系统采用不同物理库 |
| 多系统严格停用 | 上述系统 + 租户令牌撤销/内省或在线状态检查设施 | 适用于要求即时拒绝的业务；这些补充机制不是当前模板内置能力，不能宣称已实现 |

Worker 不由 `ServiceRole` 直接生成，本轮用运行宿主中的实际远端库目录调用验证相关契约，不能冒称已验收独立 Worker 产品。模板没有行业订单/财务实体，本轮通过模板已有角色实体验证跨服务写、读、删除、权限与数据库持久化。

## 实际拓扑与数据库布局

运行 Identity、Orders（Resource，带前端）、Billing（Resource，无前端），另有原协议夹具 Standalone。Orders → Billing 的全部业务对接均由 `AddRefitServiceClient` 注册，用户调用挂 `AddTokenExchange()`，机器调用挂 `AddClientCredentials()`。Resource → Identity 的租户连接/库目录对接使用 `Leistd.MultiTenancy.ServiceClient`。Billing → Orders 的第二跳同样使用 ServiceClient，检验当前单跳限制。

| 租户 | Identity 业务数据 | Orders 业务数据 | Billing 业务数据 | 隔离目标 |
| --- | --- | --- | --- | --- |
| alpha、gamma | Identity 共享库 | Orders 共享库 | Billing 共享库 | 同系统内行级隔离；不同系统拥有自己的共享库 |
| beta | 默认连接指定的独立库 | 同一独立库、Orders schema | 同一独立库、Billing schema | 默认连接回落；独立库不污染共享库 |
| delta | 独立 Identity 库 | `OrdersData` 指定的独立库 | `BillingData` 指定的独立库 | 同租户、各系统独立物理库，命名连接优先于默认连接 |

Identity 控制面与 OpenIddict 固定在控制库，不能随租户业务连接迁移。各数据库先执行正式 DbMigrator，再登记租户，运行 API 不负责 DDL。

## 场景清单、目标与验收标准

| 编号 | 场景 | 操作与通过标准 |
| --- | --- | --- |
| I01 | 两租户共享库 | 经 ServiceClient 创建、读取、删除角色；真实 Billing 库按 TenantId 落值；同租户可见，另一租户不可见 |
| I02 | 默认连接独立库 | 写读删落到 beta 独立库；共享库查不到相同记录；默认连接支持不同服务 schema |
| I03 | 各系统命名独立库 | delta 的主体投影分别落到各系统指定库，角色落到 Billing 指定库；不回落默认库或共享库 |
| I04 | 连接变更与失败关闭 | 启用状态改连接 409；停用后正确版本写入、版本递增，旧版本 409；缺所需连接且无默认连接时失败，不写共享库 |
| I05 | Resource 本地授权 | Orders 本地管理员未经 Billing 授权调用失败；Billing 正式管理员引导后，ServiceClient 写读删成功；不继承上游管理员权限 |
| I06 | 机器身份 | 入口有用户/租户也不向机器 token 自动传播；机器端点成功，自然人端点失败；无已验证用户 token 不降级为机器认证 |
| I07 | 缓存与并发隔离 | 交替和并发调用 alpha/gamma/beta/delta；校验 sub、tenant、工作负载身份、令牌摘要与数据列表/按 ID 隔离 |
| I08 | 共享租户停用 | Identity 立即拒绝；记录既有 JWT/热交换缓存的存续窗口、新交换失败、续期失败；真实到期后各 Resource 拒绝，重新启用可恢复 |
| I09 | 独立库租户停用 | 重复停用链路；远端机器库目录 `activeOnly=true` 排除停用独立租户，维护枚举可显式包含；重新启用后原数据可访问 |
| I10 | 下游故障/401/恢复 | 下游 400/403/401 按安全默认映射 502；401 只发一次业务请求并清理交换缓存；停机 503，恢复后调用成功 |
| I11 | 第二跳用户委托 | Orders → Billing → Orders 的真实 ServiceClient 第二次交换失败；不透传原 token 或降级机器；视为当前契约的预期拒绝 |
| I12 | 有头用户闭环 | 可见 Chrome 实际租户登录、门户调用 Billing 角色写入、重载读取、删除；停用后观察会话收敛；浏览器不接触客户端 secret 或 OAuth token |

清单之外的租户共享库→独立库数据迁移，不是修改连接就自动完成：必须停用、排空、自行迁移完整数据、登记连接、重新启用。本轮不把连接更新测试认作数据迁移验收。多副本/Redis、跨 Identity 联邦、跨地域容灾也不在此次单实例拓扑中。

## 租户停用对接策略

| 策略 | 当前行为或前提 | 本轮验收判断 |
| --- | --- | --- |
| Identity 控制面即时拒绝 | 每请求查租户注册表；停用后登录、授权、刷新和新交换不能获得可用新凭据 | 实际测试即时拒绝及错误路径 |
| Resource 本地 JWT 验签 | 已签发 token 在有效期内可信；不每请求回源查租户状态 | 允许短暂存续是当前契约；以实际 exp 核对到期拒绝 |
| 门户服务端会话 | Resource 后端在临近到期时续期，Identity 拒绝后撤销本地票据并回登录 | 实际观察浏览器/API 会话收敛；不能写成停用瞬间退出所有系统 |
| ServiceClient 用户委托 | 热缓存可继续使用尚有效的交换 token；缓存失效/新 subject 需向 Identity 交换，停用后失败 | 分开观察热缓存和冷交换；不把 502 当作调用者自己的权限 403 |
| 机器调用/后台任务 | 机器 token 不携带入口用户/租户；租户作业须显式选目标和生命周期策略 | 远端库目录验证 activeOnly；已启动作业不自动中止，shared 库仍需逐租户业务策略 |
| 即时全系统停用 | 停用时撤销该租户令牌，并让所有 Resource 内省；或实现可靠在线状态验证/失效通知 | 当前未内置；只开启内省、只停用租户、只清前端 Cookie 均不足以证明即时停用 |
| 停用后改路由 | 排空时间至少 `max(Access Token 有效期, Routing CacheLifetime)`，还要考虑在途工作与实际数据迁移 | 连接写入条件与版本实际验收；不宣称自动迁移或自动排空 |

规范依据：[多租户组件](../../framework/docs/components/multi-tenancy.md)、[ServiceClient 组件](../../framework/docs/components/service-client.md)、[生成项目部署说明](../../template/docs/deploy/README.md)。

## 首轮执行结果与证据

I01–I12 已全部实际执行：**9 项通过，3 项未通过；0 跳过，0 未执行**。三个未通过项共享下述 F01 缺陷，不能写成整体全绿。组件测试：最新快照 `Leistd.ServiceClient.Tests` 149 通过；多租户中 ServiceClient、Remote、TenantDatabase 相关测试 76 通过，合计 225，通过且无跳过。TRX 位于 `.tmp/serviceclient-supplement/component-results/`。组件测试不等同于有头浏览器或真实 PostgreSQL 跨进程测试。

| 编号 | 结果 | 实际证据与边界 |
| --- | --- | --- |
| I01 | 通过 | alpha/gamma 经 ServiceClient 写读删；数据库落值与软删除核对；共享库列表及跨 ID 查询隔离 |
| I02 | 通过 | beta 经默认连接落到 `oidc_dedicated` 的 Billing schema；共享 Billing 库无相同记录；写读删及恢复后数据可读 |
| I03 | 通过 | delta 写读删落到 `sc_delta_billing`；主体分别投影至 `sc_delta_orders`/`sc_delta_billing`，各系统共享库无对应主体 |
| I04 | 通过 | 启用时修改 409，停用后更新/版本递增、旧版本 409；删除专属与默认连接后实际角色查询失败，未回落共享库；恢复连接、启用、调用成功 |
| I05 | 通过 | 新测试租户用户在 Orders 有管理员权限而 Billing 无权限，跨服务创建 502（下游 403）；正式 CLI 引导 Billing 权限并重启权限缓存后，写读删通过 |
| I06 | 通过 | ServiceClient 机器身份 `orders-api`，不带入口 user/tenant；自然人端点拒绝；匿名调用交换客户端失败，不降级机器 |
| I07 | 通过 | 四租户列表/按 ID/伪造头隔离，缓存令牌摘要区分；24 个并发请求逐一核对身份、租户及缓存摘要 |
| I08 | 未通过（F01） | 共享租户停用的安全与时间边界均完成：Identity 即时拒绝、热 token 存续、新交换被拒、真实 90 秒到期后 Orders/Billing 401、门户票据删除并回登录、重新启用读取原数据。新交换错误期望 502，实际 500 |
| I09 | 未通过（F01） | 独立库租户同样完成上述链路及有头流程；远端 ServiceClient 库目录的 activeOnly 排除停用 beta，维护枚举包含它；新交换错误仍返回 500 |
| I10 | 未通过（F01） | 下游 HTTP 400/403/401 转 502、401 清缓存、业务请求不自动重放均通过；停止 Billing 两次均应 503 却返回 500，恢复后再次 ServiceClient 调用成功 |
| I11 | 通过 | 实际第二跳交换收到 Identity `invalid_grant`，未抵达 Orders leaf，无机器降级；Billing 的错误状态问题仍归 F01 |
| I12 | 通过 | alpha 和 beta 各自完成有头租户登录、创建、查询、重载查询、删除、数据库核对、停用后 401/服务端票据移除/返回 Identity 登录；最终截图点无未处理页面异常、无 OAuth token 落入浏览器存储 |

停用测试使用实际配置 `OAuth__AccessTokenLifetime=00:01:30`，从签发 token 验证 `exp - iat = 90`，再等待其真实过期。共享和独立库最终完整流程都实际等待，没有使用手工签名 token 代替生命周期。路由缓存测试配置为 1 秒；生产默认是 10 分钟，不能据此宣称生产路由立即切换。

### 运行与复现

实际运行目录：`.tmp/develop-latest-audit/.tmp/oidc-e2e/1971-20261008012640615/`。生成四个后端并实际启动，生产前端是相同最新基线、相同生成输入的 Identity/Orders 构建；从此次较早构建夹具的输出复用，没有冒称最终运行再次执行了 npm 构建。Billing 实际裁剪掉 frontend，根路径 404。

执行入口及组件命令如下；首条准备环境，由 `next-scenario.ps1` 队列按清单运行附件中的场景函数。F01 最小复现不需要 UI：热身一次 ServiceClient 调用，停止 Billing 再调用；或者在停用前取得不同 subject，停用后首次调用交换客户端。

```powershell
pwsh -NoProfile -File .tmp/develop-latest-audit/scripts/test-serviceclient-supplement.ps1 -SkipPack -LocalFeedPath .tmp/local-feed -IncludeBrowserScenarios
dotnet test .tmp/develop-latest-audit/framework/tests/components/service-client/Leistd.ServiceClient.Tests/Leistd.ServiceClient.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=serviceclient.trx' --results-directory .tmp/serviceclient-supplement/component-results
dotnet test .tmp/develop-latest-audit/framework/tests/components/multi-tenancy/Leistd.MultiTenancy.Tests/Leistd.MultiTenancy.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ServiceClient|FullyQualifiedName~Remote|FullyQualifiedName~TenantDatabase' --logger 'trx;LogFileName=multitenancy.trx' --results-directory .tmp/serviceclient-supplement/component-results
```

这套补测入口是当前工作区的隔离附件，包含本轮前端/包缓存路径，不是新增发布或 CI 入口。场景函数位于 `.tmp/serviceclient-supplement/scenarios.ps1`，生成夹具注册位于 `.tmp/develop-latest-audit/scripts/serviceclient-supplement-fixtures.ps1`。测试角色写入由实际 Billing RoleController/Application/EF Core 执行；测试专用入口只负责显示过程与经 ServiceClient 转调，未加入行业实体或修改权限算法。

### 原始失败与证据

三次环境准备失败均留在 `71819-20261008011800659`、`78404-20261008012044664`、`83553-20261008012240786`。原因是夹具采用了模板源文件名而不是替换后的生成文件名，以及 Refit 的 Authorize/ProblemDetails 同名类型导致编译歧义。正式环境为上述 `1971` 运行；这些均未修改产品实现。

首轮场景也保留失败：代理 Task 删除返回 200 而脚本误写 204；机器探针叠加自然人默认策略；JSON 忽略空值，脚本却要求 null 字段必存在；身份探针缓存无法代替数据库查询。定向复测纠正后，目标行为均有通过来源。浏览器夹具另纠正 CSP 下的脚本加载、脚本保留字、会话启动竞态与只认识工作台/登录页的截图等待。这些失败没有从原始 results/日志删除；F01 的 500/502/503 断言也没有调宽成通过。

累计原始断言 1,208 条：1,194 通过、14 失败，包含前置、夹具尝试和复测，不能当作 1,208 个独立用例。按登记的 12 个场景汇总才是 9 通过/3 未通过。

- [逐场景执行索引](../../.tmp/serviceclient-supplement/evidence/execution-index.json)：每个编号对应原始 results 的具体位置，F01 与清理结果；选择复测来源但不改写失败。
- [失败断言](../../.tmp/serviceclient-supplement/evidence/failure-assertions.json)：不可达及共享/独立租户新交换的错误状态。
- [共享租户写读截图](../../.tmp/serviceclient-supplement/evidence/I12-alpha-created-read.png)、[停用会话截图](../../.tmp/serviceclient-supplement/evidence/I12-alpha-disabled-cookie.png)、[回 Identity 登录截图](../../.tmp/serviceclient-supplement/evidence/I12-alpha-returned-to-identity.png)。
- [独立库租户写读截图](../../.tmp/serviceclient-supplement/evidence/I12-beta-created-read.png)、[删除截图](../../.tmp/serviceclient-supplement/evidence/I12-beta-deleted.png)、[停用会话截图](../../.tmp/serviceclient-supplement/evidence/I12-beta-disabled-cookie.png)、[回 Identity 登录截图](../../.tmp/serviceclient-supplement/evidence/I12-beta-returned-to-identity.png)。

原始 `results.json`、`assertions.jsonl` 和服务日志在运行目录。两个有头流程的浏览器 OAuth 存储检查及最终截图点 errors 检查均通过。公开附件不含密码、客户端 secret、连接串或原始令牌。

### 首轮清理与判断

本轮四个 API 进程已停止，PostgreSQL 容器/卷已删除，有头会话已关闭；`cleanup.json` 的 `processesStopped=true`、`errors=[]`。仅剩原有 `default` 浏览器会话，未关闭它。临时明文凭据、测试 PFX 和本轮 Data Protection 密钥已移除，[清理记录](../../.tmp/serviceclient-supplement/evidence/cleanup.json)已核对。主脚本因保留真实失败和夹具尝试以非零退出，不能将它写成全绿运行。

首轮已完成清单驱动的集成测试，当时阻碍整体通过的是 F01。即时全系统停用、多副本/跨地域、完整数据迁移与独立 Worker 产品仍属于上述未内置或未选范围，未冒称通过。没有修改用户工作区中其他并行变更，没有修改 develop 产品代码。

### F01：Refit 包装传输/认证异常后丢失 HTTP 分类（P2）

已在真实生成产品中复现两种失败：

- 停止 Billing 后，Orders 经 `AddRefitServiceClient<IBillingProbe, BillingOptions>("Billing").AddTokenExchange()` 调用 Billing；期望 503，实际 500，两次独立停止/恢复均复现。
- 停用租户后，用停用前取得、尚未交换过的新 subject token 调用相同客户端；Identity 正确返回 `invalid_grant`，期望门户 502，实际 500。单跳后的第二跳拒绝也在 Billing 边界落为 500，Orders 再把下游 HTTP 500 转成 502。

框架和生成产品消费 Refit 15.2.0。日志中最外层异常为 `Refit.ApiRequestException`，内层是已经分类的 `Leistd.ServiceClient.Exceptions.ServiceClientException`，再内层为连接拒绝或 OpenIddict `ProtocolException`。`ServiceClientExceptionMappings` 只登记 `ServiceClientException`；当前 `ServiceClientRefitSettings.ExceptionFactory` 处理 HTTP 响应错误，没有恢复请求阶段的包装异常。因此收到下游 HTTP 400/403/401 时能正确映射 502，尚未拿到 HTTP 响应的传输/认证异常却变成未分类的 500。

影响是对调用方暴露的错误契约与故障诊断失真；已观察到拒绝仍然生效，没有据此认定越权、跨租户泄漏或绕过停用。整体验收不能标记全通过。

建议在 Refit 接入层恢复框架异常分类，并补上真实 Refit → API 异常边界的回归测试，覆盖不可达、OAuth 拒绝、超时与调用者取消；不能仅测试手动抛出 `ServiceClientException` 的映射。首轮是分析/测试任务，未修改产品实现；后续按用户要求实施的修复见下节。

对应源码：[Refit 设置](../../framework/components/service-client/Leistd.ServiceClient.Refit/Options/ServiceClientRefitSettings.cs)、[异常映射](../../framework/components/service-client/Leistd.ServiceClient.Core/ExceptionMappings/ServiceClientExceptionMappings.cs)、[传输分类处理器](../../framework/components/service-client/Leistd.ServiceClient.Core/Handlers/TransportFailureHandler.cs)。原始断言、复现和恢复记录均留在运行目录。

先前完整模板验收中，S4/MT2 确实使用 ServiceClient 验证单跳用户/租户传递，S5 验证远端租户路由，MT3–MT5 覆盖 SSO/退出/停用；它们仍有效，但不能代替此次扩展后的多系统集成清单。


## F01 官方依据、修复与复测

查阅 Refit 官方文档，并以当前 NuGet 包元数据所指向的 15.2.0 源码提交 `96316a6b121c72e8a77abb0cb1f38c2a0839cd86` 核对行为。Refit 默认将 `HttpClient.SendAsync` 抛出的异常包装为 `ApiRequestException`；调用者主动取消是默认透传的例外。官方提供 `TransportExceptionFactory` 决定发送阶段最终抛出的异常，`ExceptionFactory` 则仅处理收到的 HTTP 响应。这是既定设计，我们的适配层遗漏了发送阶段的配置。[官方异常工厂文档](https://reactiveui.net/documentation/refit/results/errors/#choose-exception-factories)、[15.2.0 设置与默认实现](https://github.com/reactiveui/refit/blob/v15.2.0/src/Refit/RefitSettings.cs#L258)、[官方扩展点测试](https://github.com/reactiveui/refit/blob/v15.2.0/src/tests/Refit.Tests/GeneratedRequestRunnerTests.TransportExceptionFactory.cs)。

官方提供的是扩展机制，没有替我们的框架规定 502/503 映射。结合 ServiceClient 标准管道已完成分类的事实，本次在 `ServiceClientRefitSettings.Create()` 中配置 `TransportExceptionFactory = static (_, exception, _) => exception`，保留原异常身份、分类和原生取消语义。Core 与全局异常处理不新增对 Refit 的依赖，不递归拆任意 `InnerException`，不升级第三方包。同步随包组件文档说明 `Task<ApiResponse<T>>` 的发送阶段组件异常也会直接抛出。

修复基于工作区 HEAD `f03e155af9a687e832d3fa48e752f0e36a9b51e2` 加本次未提交改动；生成用模板与该 HEAD 相同，框架从当前源码重新打包 69 个包，经独立 Leistd 包缓存实际消费。第三方依赖复用原缓存；8 个生成产物内的 Refit 组件 DLL 均与候选包匹配，包含实际运行的 Orders/Billing API。[候选包证明](../../.tmp/refit-fix-evidence/candidate-package-proof.json)。

| 验证 | 实际结果 |
| --- | --- |
| 修复前回归 | 新增 14 项真实 Refit 测试中 10 失败、4 通过；复现 API 502/503/504 → 500、组件异常被包装、外层 HttpClient 超时被包装 |
| 修复后组件测试 | ServiceClient 全量 163 通过，0 失败、0 跳过；测试收尾补充等待上限后，新增 14 项再次全部通过 |
| 静态与包验证 | 45 道仓库静态闸门通过；69 包重新打包；Refit 包隔离还原/构建通过；缩小包选择未执行文档代码块编译，新增文档不含代码块 |
| I01–I07、I11 | 新包上重跑 CRUD、三种库布局、连接控制、授权、机器身份、24 次并发隔离及第二跳拒绝，全部通过 |
| I08、I09 受影响路径 | 共享/独立租户停用后 Identity Cookie 立即 401、旧 Orders JWT 与热交换缓存仍在有效窗口；新的真实授权码 subject 首次交换返回 502；重新启用后读取原数据成功。独立租户 activeOnly 作业枚举排除、维护枚举包含均通过 |
| I10 | 下游 400/403/401 → 502；401 只发一次业务请求并清理交换缓存；停止真实 Billing 进程 → 503；重启后 → 200，全部通过 |
| I12 有头 | alpha/beta 两种租户实际登录、创建、重载读取、删除、物理数据库校验通过；通过正式下游 401 失效交换缓存，随后停用的新交换在页面显示 502；实际停机页面显示 503，恢复显示 200。OAuth token 保持服务端、页面错误数为 0 |

本轮 14 个执行组（含初始化及两种租户的 I12）全通过，748 条断言全通过，主执行脚本退出码 0。[执行组](../../.tmp/refit-fix-evidence/results.json)、[实际断言](../../.tmp/refit-fix-evidence/assertions.jsonl)、[修复前 TRX](../../.tmp/refit-fix-results/refit-red.trx)、[全量通过 TRX](../../.tmp/refit-fix-results/serviceclient-green.trx)、[最终新增测试 TRX](../../.tmp/refit-fix-results/refit-green-final.trx)。

有头关键截图：[共享租户停用新交换 502](../../.tmp/refit-fix-evidence/F01-alpha-disabled-exchange-502.png)、[独立租户停用新交换 502](../../.tmp/refit-fix-evidence/F01-beta-disabled-exchange-502.png)、[下游停机 503](../../.tmp/refit-fix-evidence/F01-beta-downstream-503.png)、[恢复 200](../../.tmp/refit-fix-evidence/F01-beta-recovered-200.png)。本轮未重复等待 90 秒 JWT 真实到期；到期后拒绝与门户票据收敛沿用首轮实际证据，不把本轮新交换验证冒称为重新执行整个到期窗口。

本轮四个 API、专用 PostgreSQL 容器与卷、两次有头会话已清理，清理错误为 0；临时明文凭据、PFX 与 Data Protection 密钥已移除，公开证据无凭据/token/连接串值。[清理证明](../../.tmp/refit-fix-evidence/cleanup.json)。修复验收时尚未提交或推送；提交合并状态以关联 PR 为准，本报告不表示正式包已发布。
