# 认证中心短时不可用时资源服务的容错（下游反馈）

状态：已完成 · 风险档：高（框架公共 API 与运行时语义、Security 票据存储、模板认证会话、跨服务） · 关联：待开 PR

## 1. 需求

### 问题与背景

abroker 审视了 Identity 宕机约 1 小时期间 CRM、Foundation 的表现，写成反馈 `identity-outage-resilience-20261028/framework-feedback.md`（F1–F6，基线 `a7d1d285` / `1.3.0-beta.60`；本仓 develop 上相关文件此后未变）。按默认配置：

- 约 9 分钟内活跃员工全部被登出，恢复后要重新登录（模板 BFF 续期把临时故障当成会话失效）；
- 约 9 分钟后机器调用中断（机器令牌到期前 60 秒移出缓存，续取失败不用仍有效的旧令牌）；
- 约 10 分钟后全部租户业务 5xx（租户路由缓存到期后回源不可达即失败）；
- 逐库作业 30 秒后停摆（库清单到期即回源、不用旧清单）。

令牌验签（本地 JWT）、权限、令牌里的租户识别不受影响。问题集中在两类：缓存到期后不沿用最后确认状态；把"不可达"当成"拒绝"。

### 原则

- **认证中心是控制面，资源服务是数据面**：控制面不可用时，数据面在上限内按最后确认状态继续（AWS static stability、Google SRE graceful degradation、RFC 5861 `stale-if-error`）。第一道防线仍是认证中心自身高可用。
- **只在不可达时用旧值**。不可达只按真实观测判定：
  - 传输层连接失败、超时（`Unavailable`/`Timeout`）；
  - 远端真实 HTTP 状态为 408、429 或 5xx；
  - 签发方真实返回的 OAuth 错误 `server_error`、`temporarily_unavailable`（不论 HTTP 状态）。
  本地因响应无法解析、内容类型不支持等自行生成的错误不算不可达，按无效响应处理。
- **判定优先级**：签发方真实返回的 OAuth 错误优先于 HTTP 状态——`server_error`、`temporarily_unavailable` 为不可达，其余 OAuth 错误为明确拒绝（即使 HTTP 状态是 5xx、429）；没有 OAuth 错误时才按 HTTP 状态判定。
- **明确拒绝立即生效**：`Tenant:NotFound`、响应租户不一致、其他 OAuth 错误（`invalid_grant`、`invalid_client`、`unauthorized_client`、`invalid_scope` 等）、401/403 与其余 4xx。明确拒绝同时作废该键所有可回落的旧值。
- **用旧值有上限**：默认 1 小时、可配置；超过上限按现有方式失败；用旧值期间记 Warning（带依赖名与已陈旧时长，不含连接串、令牌），每个节流周期最多一条。
- **不放大请求**：用旧值期间同一键固定 30 秒内只回源一次，不让每个请求都先等一次超时；平时请求量不增加。
- **取消不是故障**：调用方取消照常传播，不清旧值、不计为不可达。
- **分区不等于宕机**：认证中心整体宕机时无人能撤销，旧值没有撤销代价；但单个服务与认证中心断网时，认证中心仍可停用租户或用户，该服务照旧服务到各自的截止：
  - 租户路由：确认后最长 `CacheLifetime + MaxStaleness`；库清单：确认后最长 `DatabaseDirectoryCacheLifetime + MaxStaleness`；
  - 已登录会话：续期不可达时，以会话主体继续服务到 `会话访问令牌 expires_at + RefreshGracePeriod`；该公式只限制故障期间沿用旧主体，不替代正常的 Cookie 与 refresh token 有效期；本地成员停用即时生效，不受影响；
  - 机器令牌：到该令牌到期前 60 秒为止。
- **依据"租户不存在 / 已退役"决定删除业务数据的路径不用旧值**，必须拿权威结果。按本地保留期清理（通知保留、操作记录归档、过期会话清理）不属此类，可用旧清单。
- 评审取舍：终局不留中间态、优先官方机制、不过度设计、不过度防御、组件只处理自己的类型（`docs/architecture/design-principles.md` §5）。

### 条目与决定

用户 2026-10-28 确认以下决定，并要求方案先经 Codex 审查通过再由 Codex 实施。

| 编号 | 问题 | 决定 | 落层 |
| --- | --- | --- | --- |
| F1 | 路由缓存到期后回源不可达，租户请求全部失败 | 采纳：不可达时在上限内用最后确认路由，节流回源，记 Warning；明确拒绝立即失败并作废旧值；连接串仍只在进程内 | 框架 multi-tenancy |
| F2 | 库清单 30 秒到期即回源、不用旧清单；清单不可用时宿主库也不处理 | 部分采纳：库清单同 F1 规则与上限用旧清单。不采纳"无可用旧清单时只处理宿主库并加不完整标记"：任何没有可用旧清单的情况（进程新启动、首次访问某个连接名或 `activeOnly` 键、旧值已超上限）统一整轮失败、下轮重试；为此改公开结果类型并让各逐库作业处理标记不值得 | 框架 multi-tenancy |
| F3 | 令牌端点不可达被归为 `RemoteFailure`，与签发方拒绝无法区分 | 采纳，方式调整：不按本地 `Reject(server_error)` 推断（网络失败、坏响应与签发方 500 都会得到这个码）。传输失败按真实异常归 `Unavailable`/`Timeout`；签发方的真实答复带上真实 HTTP 状态与真实 OAuth 错误码对外；本地生成的错误归 `InvalidResponse`。"不可达"判据由服务调用组件提供一个公开判定，各处共用 | 框架 service-client；模板偏好缓存改用共用判定 |
| F4 | 机器令牌不提前续取，续取失败不用仍有效的旧令牌；机器令牌与用户访问令牌同一有效期 | 采纳：机器令牌过半寿命后续取；续取不可达时继续用未到可用截止的旧令牌（记 Warning、节流），明确拒绝或 401 立即作废。Token Exchange 不采用新策略，只获得 F3 的分类。模板 Identity 增加机器客户端访问令牌有效期配置，默认 2 小时 | 框架 service-client；模板 Identity |
| F5 | BFF 续期遇非 2xx 或任何异常即删会话；续期无显式超时 | 采纳：明确拒绝立即删会话；不可达时保留会话、在宽限上限（默认 1 小时）内以会话主体继续服务，同会话 30 秒节流重试，远端续期整体 10 秒超时；超过上限按现有方式失效 | 模板 Api |
| F6 | 模板规范没有资源服务对认证中心的容错约定 | 采纳：分档目标与上述原则写进模板 `service-invocation.md`、`auth.md`；框架 multi-tenancy、service-client 组件文档同步 | 模板规范、框架随包文档 |
| 不改-1 | API readiness 启动确认一次后锁存，冷启动需认证中心 | 同意不改：冷启动没有公钥无法验签，属固有约束，运维约定故障期间冻结发布 | — |
| 不改-2 | 头像、偏好写入、Token Exchange 等需要认证中心当场参与的调用 | 同意不改：属于允许失败、明确提示的一档 | — |

**改路由的排空约定**（用户 2026-10-28 确认）：等待时长维持 `max(访问令牌有效期, CacheLifetime)`，流程加一步"确认没有服务在用旧路由"。确认的含义：承载该租户的各实例在变更开始后成功回源过（检索不到用旧值 Warning 不足为据）。用旧值期间旧路由最长沿用 `CacheLifetime + MaxStaleness`。

**机器令牌的 1 小时目标**：默认 2 小时、过半续取，续取由请求驱动。认证中心在一次成功续取之后宕机时，剩余寿命约为 1 小时减去 60 秒可用余量（约 59 分钟）；若进程长时间空闲后才首次请求，剩余寿命以实际为准。目标按"约 1 小时"表述，不在缓存中持有已过期令牌来凑足。

**版本**（用户 2026-10-29 确认）：令牌端点传输失败的 `FailureKind` 由 `RemoteFailure` 改为 `Unavailable`/`Timeout`（对外 502 → 503/504）按修复发布，不标 `!`：`Unavailable` 的既有契约就是"传输层无法连接"，原分类违背该契约；release notes 写明变化与下游必要动作。

### 验收标准

框架（单元或集成测试，测试名在实施时填）：

- Given 路由已确认且在新鲜期内，Then 不回源；Given 新鲜期已过、未超硬截止，When 回源不可达（连接失败、超时、5xx、408、429），Then 返回旧路由、记一条带陈旧时长的 Warning，30 秒内同键不再回源；重试时刻不越过硬截止。
- Given 旧路由已越过硬截止，或从未确认过，When 回源不可达，Then 按现有方式失败，对外状态码与现状一致（5xx 为 502、超时为 504、连接失败为 503）。
- Given 路由已确认，When 回源返回 `Tenant:NotFound`、响应租户不一致、401/403 或其他 4xx，Then 立即失败并作废旧值；之后再遇不可达也不回落。
- Given 调用方取消，Then 取消传播，旧值与确认时间都不变。
- Given 不同请求作用域，Then 共享同一份旧值（解析器为 Scoped、目录为 Transient，状态不能放实例字段）。
- Given 库清单已确认且未超硬截止，When 回源不可达，Then 返回旧清单（含当时的 `FailedTenants`，`activeOnly` 两个键各自独立）并记 Warning；越过硬截止或从未确认则整轮失败。
- Given 令牌端点连不上或超时，Then 抛 `FailureKind` 为 `Unavailable`/`Timeout` 的异常，事件 6182 照记；Given 签发方返回 HTTP 400 + `invalid_client`，Then 为 `RemoteServiceException`（400、错误码 `invalid_client`），判定为明确拒绝；Given HTTP 400 + `temporarily_unavailable`、HTTP 429 无 OAuth 正文，Then 判定为不可达；Given HTTP 200 坏 JSON，Then 为 `InvalidResponse`，不可达判定为否；业务接口返回同名 `ErrorCode` 时不按 OAuth 解释。坏响应类情形并入现有 `ResponsePayloadLoggingTests`，日志不含令牌与响应正文的断言保留。
- Given 机器令牌已过半寿命，When 并发多次调用，Then 只续取一次；When 续取不可达且旧令牌未到可用截止（到期前 60 秒），Then 用旧令牌并记 Warning，30 秒内不再续取；When 续取被明确拒绝，Then 作废旧令牌并抛出。
- Given 续取在途，When 下游返回 401 使令牌作废，且随后续取不可达，Then 不把已作废的旧令牌重新发布。
- Given Token Exchange，Then 缓存与续取时机与现状一致，只有失败分类按 F3。

模板（生成项目单元/集成测试）：

- Given 偏好缓存有旧值，When 取令牌不可达，Then 回落旧值；When 机器身份被明确拒绝，Then 清旧值并抛出（`IdentityAccountPreferenceCacheTests`）。
- Given 机器客户端走 `client_credentials`，Then 访问令牌有效期取 `OAuth:ClientCredentialsAccessTokenLifetime`（默认 2 小时，校验规则同 `AccessTokenLifetime`）；用户访问令牌与 Token Exchange 有效期不变。
- Given 会话需要续期：
  - When 令牌端点返回 `invalid_grant`、`invalid_client`、`unauthorized_client`、其他 OAuth 错误或续期得到的主体不一致，Then 删会话；
  - When 不可达（连接失败、10 秒超时、5xx、408、429、OAuth `server_error`/`temporarily_unavailable`，含 HTTP 400 承载的这两个错误），Then 保留会话、本次请求以会话主体继续；30 秒内同会话不再重试；
  - Given 同一张已越过 `expires_at + RefreshGracePeriod` 的票据（如登录后空闲 71 分钟再访问）：When Identity 正常且 refresh token 有效，Then 续期成功、会话继续；When Identity 不可达，Then 删会话、不重开宽限（含首次失败后不再访问、节流记录已过期的情形）；
  - When 在节流间隔内且已越过截止，Then 删会话；未越过截止，Then 以会话主体放行、不回源；
  - When 续期成功，Then 清除宽限状态，之后迟到的请求不能把它恢复；
  - When 请求被放弃或失去刷新锁，Then 取消照常传播，不进入宽限；锁或票据存储故障按现有方式处理，不算认证中心不可达；
  - When 续期返回 HTTP 200 但响应无法解析或访问令牌验签失败，Then 按现有方式删会话（不是不可达）。
- Given 宽限期内、会话里的用户访问令牌已过期，When 调用需要 Token Exchange 的接口，Then 该调用按依赖故障失败，不删会话、不回退为机器身份；机器调用照常。
- 并发：两个请求经真实 Cookie 处理器与票据存储并开启滑动续期；A 先完成认证、延后响应，B 成功续期并轮换 refresh token 后 A 才结束响应，Then 最终票据保留 B 的 access/refresh token，下一次续期使用新 refresh token，会话仍有效（`ResourceBrowserSessionTests` 一类集成测试；票据存储规则另有 Security 组件单测）。

e2e（`scripts/test-template-oidc-e2e.ps1`，快速配置档，只验一个 Resource 的跨进程接线；协议矩阵、并发与时间边界留在上面的测试）：

- 用缩短的令牌寿命、`CacheLifetime` 与宽限上限，断言生效值；Given 已登录且资源服务已确认过路由，When 停掉 Identity 并越过各自的新鲜期，Then：
  - 机器调用 probe 成功，日志出现令牌与路由的用旧值 Warning（证明发生了失败回源）；
  - 委托调用 probe（现有 `/api/e2e/billing`，Token Exchange）允许失败，会话不被登出；
  - 越过宽限上限后会话按现有方式失效。
- When 在宽限期内恢复 Identity，Then 下一次续期成功（日志或令牌变化可观察），无需重新登录。
- 场景在 finally 中恢复 Identity，不影响后续用例；不新增浏览器认证闭环或测试设施。

### 非功能要求

- 用旧值的 Warning 每类依赖、每个节流周期最多一条，不含连接串、令牌、刷新令牌。
- 平时对认证中心的请求量不增加（机器令牌过半续取使每个令牌寿命内的请求由一次变两次，属预期）。

## 2. 方案

### 现状事实

| # | 位置 | 现状 |
| --- | --- | --- |
| 1 | `framework/components/multi-tenancy/Leistd.MultiTenancy.Core/ConnectionStrings/RemoteConnectionStringResolver.cs` | Scoped；HybridCache 进程内缓存 `CacheLifetime`（默认 10 分钟，`TenantRouteCacheOptions` 上限 1 小时），失败不缓存，到期即回源 |
| 2 | `framework/components/multi-tenancy/Leistd.MultiTenancy.ServiceClient/Stores/CachedTenantDatabaseDirectory.cs` | Transient；缓存 `DatabaseDirectoryCacheLifetime`（默认 30 秒），注释写明"过期即回源、不返回陈旧清单" |
| 3 | `.../Leistd.MultiTenancy.ServiceClient/Stores/RemoteTenantConnectionStore.cs` | 只有带 `Tenant:NotFound` 的 404 翻成 null，其余错误原样上抛（5xx 为 `RemoteServiceException`，`FailureKind = RemoteFailure`，带 `RemoteStatusCode`） |
| 4 | `Leistd.MultiTenancy.Core.csproj` | 不引用 `Leistd.ServiceClient.Core`；ServiceClient 包引用两者 |
| 5 | `framework/components/service-client/Leistd.ServiceClient.OAuth/Handlers/SendCancellationGuard.cs` | 连接失败、超时与官方一致记事件 6182 并 `Reject(server_error)` |
| 6 | `.../Handlers/ResponsePayloadLoggingGuard.cs` | 响应无法解析、内容类型不支持、非成功且无 OAuth 错误时本地 `Reject(server_error)`；429 被转为 `slow_down` |
| 7 | `.../Handlers/TokenCache.cs` | 一切 `OpenIddictExceptions.ProtocolException` 包为 `RemoteFailure` 的普通 `ServiceClientException`，OAuth 错误码只在消息里；命中时直接返回，工厂内二次只读检查也直接返回；令牌缓存到 `ExpiresAt - buffer` |
| 8 | `.../Handlers/ClientCredentialsDelegatingHandler.cs`、`TokenExchangeDelegatingHandler.cs` | buffer 分别 60 秒、10 秒；401 时只移除当前键 |
| 9 | `Leistd.ServiceClient.Core/Exceptions/ServiceClientFailureKind.cs`、`ServiceClientFailureClassifier.cs`、`RemoteServiceException.cs` | `Unavailable`/`Timeout` 只由传输层推断；`RemoteServiceException` 有公开构造，携带 `RemoteStatusCode` 与 `ErrorCode` |
| 10 | `template/backend/src/CompanyName.ProjectName.Infrastructure/AccountPreferences/IdentityAccountPreferenceCache.cs` | `Unavailable`/`Timeout` 回落旧值；`RemoteFailure` 一律当"取令牌被拒"清旧值 |
| 11 | `template/backend/src/CompanyName.ProjectName.Api/Auth/Sessions/ResourceSessionRefresher.cs` | 续期非 2xx 即 `RejectAsync`，`catch` 一切非取消异常也 `RejectAsync`；先取发现配置再 POST，走 `options.Backchannel`，无单独超时 |
| 12 | `framework/components/security/Leistd.Security.AspNetCore/Cookies/DistributedTicketStore.cs` | 续写票据走 `:ticket` 锁，只比引用版本；官方 Cookie 处理器在 `OnValidatePrincipal` 前克隆票据、响应开始时写回 |
| 13 | `template/backend/src/CompanyName.ProjectName.Api/Controllers/ConnectController.cs` | `client_credentials` 分支不设有效期，取全局 `OAuth:AccessTokenLifetime`（默认 10 分钟）；Token Exchange 分支 `SetAccessTokenLifetime(120 秒)` |
| 14 | `framework/docs/components/multi-tenancy.md:353,367-369,381`、`service-client.md:122-133` | 写明当前缓存与失败语义、排空公式、"令牌请求的协议拒绝归类为 RemoteFailure" |
| 15 | 逐库与目录消费者 | `ITenantDatabaseRunner`：通知保留期、操作记录归档、模板过期会话清理；`ITenantDatabaseDirectory`：另有 `TenantConnectionManagementService`（控制面 EF 实现，保持权威读取，不受远端旧值影响）；`TenantDatabaseSet` 形状不变 |
| 16 | `scripts/test-template-oidc-e2e.ps1` | 已有快速令牌寿命配置、跨服务 probe（`/api/e2e/billing` 走 Token Exchange）与 S8"预热令牌在故障期间可用"；模板对刷新器有文件级裁剪 |

### 设计

**共用判据（service-client Core）**：在 `ServiceClientFailureClassifier`（`Exceptions/`）上增加一个公开静态判定"该失败是否属于依赖不可达"，只看组件自己的异常，按顺序判定：`FailureKind` 为 `Unavailable`/`Timeout` → 是；`RemoteServiceException` 带真实 OAuth 错误码时，`server_error`/`temporarily_unavailable` → 是，其余 → 否；无 OAuth 错误码时 `RemoteStatusCode` 为 408/429/5xx → 是；其余 → 否。OAuth 错误码与业务错误码共用 `ErrorCode` 字段，判据只在来源为令牌端点的异常上按 OAuth 码解释（区分方式由实施确定，例如判定入参或异常上的来源标记），业务接口的同名码不受影响。不引用 OpenIddict，不改全局异常映射。消费者：F1、F2、F4、F5、模板偏好缓存，以及下游 P2。

**F3（service-client OAuth）：失败信息的归一化**
- 原则：OAuth 适配层保存真实 HTTP 状态、签发方实际返回的 OAuth 错误、以及"本地生成"的来源，向外转成组件自己的异常；判定不依赖本地 `Reject` 的错误码。
- 传输失败：发送保护器记事件 6182 后让原始异常传出（或以等价方式让外层拿到原始异常），由 `TokenCache` 在执行回落策略前用 `ServiceClientFailureClassifier` 分类、包成 `Unavailable`/`Timeout`；不依赖外层业务 HTTP 管道代包装。
- 签发方真实答复的 OAuth 错误（含 HTTP 429 无正文、404 无正文等由保护器识别的非成功状态）：包成 `RemoteServiceException`，带真实 HTTP 状态与真实 OAuth 错误码（无 OAuth 正文时错误码为空）。
- 本地生成的错误（200 坏 JSON、不支持的内容类型、成功却取不出响应）：包成 `InvalidResponse`。
- 状态如何从 OpenIddict 事务传到 `TokenCache`（官方服务自建事务、拒绝后另建 `ProtocolException`）由实施确定，以 OpenIddict 7.7.2 源码与对照测试为准；日志不含令牌与正文的现有保护不变。
- 文档改为：传输失败 503/504，签发方答复 502，无效响应 502。

**状态模型（F1、F2、F4 共用的约定）**：每个键保存 `(value, confirmedAt)`；
- 新鲜截止 `freshUntil`：路由 `confirmedAt + CacheLifetime`、清单 `confirmedAt + DatabaseDirectoryCacheLifetime`、机器令牌 `签发 + 寿命/2`；
- 硬截止 `hardDeadline`：路由与清单 `freshUntil + MaxStaleness`，机器令牌 `到期 - 60 秒`；
- 重试时刻 `retryAt = min(失败时刻 + 30 秒, hardDeadline)`。
- 新鲜期内直接返回；新鲜期过后，第一个到达者单飞回源（其余等待同一次）；成功则更新 `confirmedAt`；不可达且未越过硬截止则返回旧值、记 Warning、设 `retryAt`，`retryAt` 前不再回源；明确拒绝或 401 作废该键全部状态；不可达回落时重读当前状态而不是用回源前捕获的值，已被作废则直接抛出，不另加代次机制；取消不改状态。
- 状态是进程内共享的（单例或 HybridCache L1），有界：条目寿命不超过硬截止，不用永久字典。若沿用 HybridCache，须像现有 `TokenCache` 一样禁止外层 `GetOrCreateAsync` 自动写入，让普通寿命与重试寿命各自生效（官方工厂完成时会按外层选项写 L1）。
- 测试时 `FakeTimeProvider` 与 `MemoryCacheOptions.Clock` 驱动同一时间线。
- 就近实现：multi-tenancy 内路由与清单共用一份，OAuth 内一份；不抽到更低层的共享类型，不新增通用缓存或重试框架。

**F1（multi-tenancy Core + ServiceClient）**
- 分层：判断不可达在 ServiceClient 包，用旧值策略在 Core 解析器。ServiceClient 远端存储遇共用判据为真时，抛 Core `Exceptions/` 下新增的路由依赖暂不可达异常（命名收窄到租户连接语义），内层为原异常。
- Core 解析器捕获该异常才用旧值；没有可用旧值时重新抛出内层原异常（保留堆栈），对外状态码与现状一致，不为新异常登记映射。其他存储实现不受影响。
- `TenantRouteCacheOptions.MaxStaleness`：默认 1 小时；`TimeSpan.Zero` 是宿主长期选择"不接受陈旧路由"的合法边界值，不是兼容开关；上限与校验按现有 Options 风格。
- 最后确认路由只放进程内（与现有 L1 同等敏感度）。

**F2（multi-tenancy ServiceClient）**：`CachedTenantDatabaseDirectory` 按同一状态模型，上限复用 `TenantRouteCacheOptions.MaxStaleness`（同属控制面路由信息，两个真实消费者，一个旋钮）；`TenantDatabaseEnumerator` 与 `TenantDatabaseSet` 不改；控制面 EF 目录实现不变。目录与远端存储同在 ServiceClient 包，直接对 `ServiceClientException` 用共用判据，不经 Core 包装异常；Core 包装异常只由远端存储的 `FindAsync` 抛出、只被路由解析器捕获，迁移目标等不走解析器的消费者不用旧值。

**F4（service-client OAuth + 模板 Identity）**
- 机器令牌按状态模型实现过半续取与回落；迟到 miss 的复查保留（避免重复请求）。
- Token Exchange 保留现有缓存策略（到期前 10 秒失效、不过半续取、不回落），只共享 F3 的分类。
- 模板 `OAuthServerOptions` 增加 `ClientCredentialsAccessTokenLifetime`（默认 2 小时，校验同 `AccessTokenLifetime`），`ConnectController` 的 `client_credentials` 分支 `SetAccessTokenLifetime`；部署变量与文档同步。

**F5（模板 Api）**
- 续期结果分类复用共用判据：把令牌端点的非成功响应读成一个 `RemoteServiceException`（真实状态 + OAuth 错误码），传输异常与自己的超时用 `ServiceClientFailureClassifier` 分类，然后交给共用判据；不另造分类表。HTTP 200 的解析失败、验签失败、主体不一致不经判据，按现有方式删会话。
- 远端续期操作整体（取发现配置、POST、读响应）固定 10 秒超时；只有这个超时进入宽限，请求放弃与失去刷新锁照常传播；锁与票据存储的故障按现有方式处理。
- 宽限截止不另存：以票据中访问令牌的 `expires_at` 为宽限起点，截止 = `expires_at + RefreshGracePeriod`。`expires_at` 只在续期成功时前移，因此同一轮故障不会重开宽限，缺少任何辅助记录也能可靠重建截止；状态天然随票据有界。
- 只有节流需要辅助状态：以票据键为键，在分布式缓存里存 `retryAt`（寿命 30 秒），只在 `:refresh` 锁内读写；丢失只会多一次重试，不影响截止判定。
- 顺序：需要续期，且没有重试记录或已到 `retryAt` → 先尝试续期；成功则采用新令牌（截止随新 `expires_at` 前移）；明确拒绝则删会话；不可达则未越过截止时以当前票据主体放行并设 `retryAt`，越过截止时删会话。在 `retryAt` 之前不回源：越过截止删会话，否则放行。截止只限制不可达时的回落，不会在判断依赖是否可达之前注销正常会话。
- 票据迟到写回（现有缺陷，本轮一并修）：官方 Cookie 处理器在验证前克隆票据、响应开始时被动续写；`DistributedTicketStore` 只比较引用版本，较早克隆的被动续写会把另一请求刚轮换的 access/refresh token 覆盖回旧值，下一次续期即 `invalid_grant`。修法在 Security 组件内、不认识 OAuth 字段：所有者显式替换票据内容的写入（刷新器的 `RenewAsync`）轮换内容版本；被动续写携带的内容版本与存储中的不一致时不写入。内容版本与现有 Cookie 引用版本职责分开：现有 `ExplicitSignInKey` 只对应原生重新登录，刷新内容不能借它绕过引用校验、复活已删除票据或覆盖新登录；现有引用版本、滑动续期、删除不复活、取消与失锁的测试断言全部保留。接口由实施按最小改动确定。
- 上限进现有会话相关 Options（`RefreshGracePeriod`，默认 1 小时，具体挂在哪个 Options 由实施按模板现有风格定）；30 秒与 10 秒为固定值，不做配置。

**F6（文档）**：模板 `service-invocation.md` 写分档目标与用旧值规则；`auth.md` 写会话续期的宽限与分区边界；框架两篇组件文档更新缓存、失败分类与排空约定。受模板规范读取成本闸门约束，超出时在同篇去重抵消。

### 备选与取舍

- 只延长各缓存 TTL：平时变更生效变慢，宕机超过 TTL 仍中断。不采用。
- 把 `server_error` 直接归为 `Unavailable`：与枚举"不由远端 HTTP 状态推断"的契约冲突，且把签发方 500、坏响应与网络失败混为一类。不采用。
- 新的路由不可达异常登记为 503：会把现有 502/504 改为 503，扩大行为变化。改为无旧值时重抛原异常。
- 宽限开始时间写进服务端票据或独立缓存条目：前者与被动续写竞争，后者寿命一过就丢失截止证据。改为由票据里的 `expires_at` 推出截止。
- 无旧清单时只处理宿主库并加不完整标记（F2 后半）：见条目表。不采用。
- 排空等待加长到含 `MaxStaleness`：改路由罕见且由运维执行，改为"确认各实例已成功回源"。不采用。
- 路由与连接串进分布式缓存以跨实例共享旧值：连接串含口令，框架有意只放进程内。不采用。

### 兼容与回滚

新增公共表面：
- `ServiceClientFailureClassifier` 的不可达判定；
- `TenantRouteCacheOptions.MaxStaleness`；
- MultiTenancy Core 的路由依赖暂不可达异常；
- 令牌失败对外改为 `RemoteServiceException`（已有类型）并带真实状态与错误码；
- 模板 `OAuth:ClientCredentialsAccessTokenLifetime` 与会话宽限上限。

已文档化行为的变化：
- 令牌端点传输失败 `FailureKind` 由 `RemoteFailure` 变为 `Unavailable`/`Timeout`，对外 502 → 503/504；令牌协议拒绝的异常类型由 `ServiceClientException` 变为其子类 `RemoteServiceException`（`FailureKind` 不变）。下游按 `RemoteFailure` 判断"取令牌被拒"的分支应改用共用判据。
- 路由与库清单在不可达时用旧值；对外失败状态码不变。
- `DistributedTicketStore`：内容版本落后的被动续写不再写入（原先会覆盖较新内容），属缺陷修复。
- 版本：按修复发布、不标 `!`（见 §1 版本），release notes 写明下游必要动作。

回滚按提交回滚；`MaxStaleness = 0` 是宿主可长期选择的边界值。

## 3. 计划

| # | 任务 | 产物 | 验证 |
| --- | --- | --- | --- |
| 1 | 方案审查 | Codex 审查本规格，明确"通过" | 审查结论 |
| 2 | 共用判据 + F3 + F4 框架部分 | service-client Core/OAuth 源码、测试、`service-client.md` | `OfficialClientTests`、`ResponsePayloadLoggingTests` 等框架单测；包消费 |
| 3 | F1 + F2 | multi-tenancy Core/ServiceClient 源码、测试、`multi-tenancy.md` | 路由/目录缓存测试，`TimeProvider` 驱动的新鲜、陈旧、硬截止与节流 |
| 4 | Security：票据被动续写不覆盖较新内容 | `DistributedTicketStore` 源码、测试、`security` 组件文档 | Security 单测 |
| 5 | 模板：偏好缓存、机器令牌有效期、F5 会话续期 | 模板源码与测试、部署变量 | 受影响场景生成、构建、测试，含 `ResourceBrowserSessionTests` 一类真实 Cookie 管道测试 |
| 6 | F6 文档 | 模板 `service-invocation.md`、`auth.md` | 文档检查、读取成本闸门 |
| 7 | e2e | OIDC 跨服务 e2e 增加"Identity 停止后"快速配置档场景 | `scripts/test-template-oidc-e2e.ps1` |
| 8 | 实施审查与交付 | Claude 审查至通过；PR；CI；回执 | CI 全绿 |

实施者：本工作树的 Codex 会话；方案、审查、进度与回执：Claude 会话。

不做的事：认证中心自身高可用；故障期间的新登录；跨服务租户变更事件；持久化公钥让冷启动不依赖认证中心；`TenantDatabaseSet` 不完整标记；通用重试/缓存框架；会话状态机。

## 4. 结果

- **实际交付**：本 PR；实施者为本工作树的 Codex 会话，方案审查四轮（第四轮通过）、实施审查一轮修订后通过（Claude）。
- **与计划的偏差**：
  - 无行为偏差。
  - 模板 `OAuthServerOptions`、`OAuthCertificate` 改为 public：公开控制器构造注入需要。
  - 内容版本比较按"两侧缺失视为相等"，升级前已存在的会话照常滑动续期。
- **验收记录**：
  - 框架测试：ServiceClient 192、MultiTenancy 420、Security 181 全部通过；本地打包 73 个包，5 个受影响包隔离消费通过。
  - 模板测试：Resource、Identity 生成项目目标单测与集成测试通过（会话宽限矩阵与迟到写回回归在真实 Cookie 管道上运行）。
  - 生成与静态检查：22 场景条件生成与相关静态闸门通过。
  - OIDC 跨服务 e2e：13 场景、483 断言通过，含 S13"Identity 停止后"，覆盖机器与路由回落、委托失败不登出、恢复后实际续期、宽限耗尽失效。
  - 修复前失败的回归：库清单回落、票据迟到写回。
- **遗留项**：
  - F5 的失锁、节流过期、节流期内越过截止等细分组合，没有逐一新增时间矩阵用例，由实现分流加现有测试覆盖。
  - 本地未跑 22 场景完整构建与运行时，由 CI 承担。
- **已上收的常驻文档**：
  - 框架 `multi-tenancy.md`、`service-client.md`、`security.md`；
  - 模板 `service-invocation.md`、`auth.md`、`deploy/configuration.md`。
