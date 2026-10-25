# CompanyName.ProjectName 后端

后端基于 .NET 10 和 Leistd.* NuGet 包，采用单向依赖的 DDD 四层结构。

## 分层

```text
src/
|-- CompanyName.ProjectName.Domain/          # 领域模型和内层抽象
|-- CompanyName.ProjectName.Application/     # 用例编排，只依赖 Domain
|-- CompanyName.ProjectName.Infrastructure/  # EF Core 和外部适配器
|-- CompanyName.ProjectName.Client/          # 供其他服务消费的强类型 SDK
|-- CompanyName.ProjectName.DbMigrator/      # 独立、一次性数据库迁移入口
`-- CompanyName.ProjectName.Api/             # 组合根和 HTTP 入口
```

`Api` 显式引用并组合 Application 与 Infrastructure；Application 不依赖 Infrastructure。

## Leistd 框架 API

`Directory.Build.props` 统一声明 `LeistdFrameworkVersion`。使用 `Leistd.*` API 前，优先使用 `leistd-net-framework` Skill 按实际还原版本定位包内文档。未安装该 Skill 时：

1. 从项目 obj 目录下的 project.assets.json 或 `dotnet list package --include-transitive` 确认实际还原的包与版本；
2. 用 `dotnet nuget locals global-packages --list` 找到 NuGet 缓存根目录；
3. 读取 `{缓存根}/{小写包名}/{版本}/docs/*.md`，精确签名以匹配项目 `TargetFramework` 的 `lib/{tfm}/{包名}.xml` 为准。

不读缓存中其他版本的文档，也不根据模型记忆猜测类型、签名或注册方法。

## 开发配置

启动配置使用 `ASPNETCORE_ENVIRONMENT=Development`（`DbMigrator` 为 `DOTNET_ENVIRONMENT=Development`）。配置按下表分层，后者覆盖前者：

| 层 | 放什么 | 是否进仓库 |
| --- | --- | --- |
| `appsettings.json` | 与环境无关的基线；凭据位置留空 | 是 |
| `appsettings.Development.json` | 所有开发者共用的开发配置：指向本机开发 compose 的连接串、开发证书开关、公开的演示管理员口令 | 是 |
| `dotnet user-secrets` | 机密与只属于本机的覆盖：共享开发库的连接串、连接共享库时的管理员口令、机器客户端密钥 | 否，只在开发环境加载 |
| 环境变量 / 密钥系统 | 部署环境的全部机密与差异 | 否 |

Api 与 `DbMigrator` 共用同一个 `UserSecretsId`，两边的开发连接串都指向 `../deploy/docker-compose.dev.yml` 起的本机库（只绑定 127.0.0.1，含 Redis）。连接其他开发库时用 user-secrets 覆盖，两边都会读到：

```bash
dotnet user-secrets set "ConnectionStrings:Default" "<共享开发库的连接串>" --project src/CompanyName.ProjectName.Api
dotnet user-secrets list --project src/CompanyName.ProjectName.Api
```

除开发 compose 的本机口令与演示管理员口令这类公开的本机开发值外，不要把密码、证书或生产连接字符串写进任何 `appsettings*.json`。集成测试宿主跑在 `Testing` 环境，不加载 user-secrets 与 `appsettings.Development.json`，所需配置由测试夹具显式给出。

开发环境以外，只在单机上成立的配置回落不再生效，缺配即启动失败，逐项见 [配置参考](../docs/deploy/configuration.md#部署环境的必填项)。
<!--#if (LocalIdentity && IncludeMultiTenancy)-->

控制库使用 Data Protection 加密租户连接串，API 与 `DbMigrator` 必须共用密钥环与应用名。本机两边的内容根不同，需要时把密钥目录指向同一处：`dotnet user-secrets set "DataProtection:KeysPath" "<本机目录>" --project src/CompanyName.ProjectName.Api`。
<!--#endif-->
<!--#if (OpenIddictServer)-->

默认只监听 HTTP（`http://localhost:5240`）。本机的 OIDC 流程（开放应用的授权码流程、资源服务联调）经前端开发服务器 `http://localhost:4200` 访问授权端点，签发方地址即为它；`appsettings.Development.json` 因此关闭了授权端点的 HTTPS 要求，并以 `OAuth:UseDevelopmentCertificates` 使用开发证书，两者只作用于开发环境。
<!--#endif-->
<!--#if (RemoteTokenAuth)-->

### 对接 Identity

`appsettings.Development.json` 的 `Authentication:Issuer` 默认指向本机 Identity 的前端开发服务器 `http://localhost:4200/`，联调别处的 Identity 时用 user-secrets 覆盖；本服务 API 的受众与依赖方须先在 Identity 登记，见 [服务间调用](../docs/standards/service-invocation.md#identity-与资源服务对接)。访问令牌权限以本服务授权事实为准。
<!--#if (ResourceBrowserSession)-->
浏览器依赖方登记后配置 ClientId/ClientSecret，缺失会按键名启动失败；本机端口与回调地址见 [前端说明](../frontend/README.md#联调本机-identity-服务)：

```bash
dotnet user-secrets set "Authentication:Issuer" "<Identity 服务的地址>/" --project src/CompanyName.ProjectName.Api
dotnet user-secrets set "Authentication:ClientId" "<已登记的机密客户端>" --project src/CompanyName.ProjectName.Api
dotnet user-secrets set "Authentication:ClientSecret" "<机密客户端密钥>" --project src/CompanyName.ProjectName.Api
```
<!--#endif-->
<!--#if (IncludeMultiTenancy)-->

租户路由与迁移作业以机器身份回源同一个 Identity（开发配置的 `Leistd:ServiceAuth:Authority` 与 `Leistd:ServiceClients:Identity:BaseAddress` 同样指向 `http://localhost:4200/`，联调别处时一并覆盖）。在 Identity 登记一个 client credentials 机器客户端，授予 `tenant-routing.read`（运行时回源）与 `tenant-migration.read`（`DbMigrator` 枚举独立库租户），把凭据写进 user-secrets，Api 与 `DbMigrator` 共用。本机可用一个客户端同时持有两个 scope，部署环境按 `../deploy/docker-compose.yml` 分开。迁移前先启动本机 Identity：

```bash
dotnet user-secrets set "Leistd:ServiceAuth:ClientId" "<机器客户端>" --project src/CompanyName.ProjectName.Api
dotnet user-secrets set "Leistd:ServiceAuth:ClientSecret" "<机器客户端密钥>" --project src/CompanyName.ProjectName.Api
```
<!--#endif-->
<!--#endif-->
<!--#if (Email)-->

### 邮件

`Leistd:Email:Smtp` 默认指向开发 compose 里的本机邮件捕获器，收件箱在 `http://localhost:8025`。邮箱验证默认关闭（`UserRegistration:EnableEmailVerification`）；开启后没有可达的 SMTP 会**发信失败并向调用方报错**，不会静默跳过，注册流程据此撤回已占用的限流槽位；注册时通过了发往该地址的验证码，账号邮箱即为已验证，关闭时注册只过图形验证码、邮箱保持未验证。宿主管理员可以在「系统设置 → 邮件发送」里在运行期覆盖配置文件的发信参数（口令加密落库、界面只写不读）并发送测试邮件；清除覆盖值即回到配置文件里的值。部署时的必填项见 [配置参考](../docs/deploy/configuration.md#部署环境的必填项)。

账号邮箱被本人或管理员换掉时，修改提交之后向**旧的已验证地址**直接发送一封账号安全邮件（`EmailChangedEventHandler` 经 `IEmailSender` 发送）：不受普通通知偏好影响，未验证的旧地址不发；投递尽力而为，发送失败只记日志、不让已提交的修改报错。规则见 [换邮箱的提醒](../docs/standards/auth.md#换邮箱的提醒)。
<!--#endif-->

## 启动与验证

```bash
dotnet restore CompanyName.ProjectName.sln
dotnet build CompanyName.ProjectName.sln
dotnet run --project src/CompanyName.ProjectName.Api
```

启动后检查：

```text
GET http://localhost:5240/api/health/live
GET http://localhost:5240/api/health/ready
```

测试命令与分工见 [测试规范](../docs/standards/testing.md#2-后端)。

## 数据库迁移

模板携带可审查的基线迁移。API 启动时不自动迁移，也不使用 `EnsureCreatedAsync`；先运行独立的 `DbMigrator`，成功后再启动 API。发布时的 DDL/DML 身份分离与执行顺序见 [部署说明](../docs/deploy/README.md#环境晋级)。

`DbMigrator` **默认只读预演**：列出待执行的迁移与 SQL，不改库；确认后再加 `--apply` 施加。

```bash
dotnet run --project src/CompanyName.ProjectName.DbMigrator             # 预演
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --apply  # 施加
dotnet run --project src/CompanyName.ProjectName.Api
```

默认目标始终执行业务迁移；OIDC 存储由 Identity 的独立 DbContext 管理。任一目标迁移失败时进程以非零码退出。
<!--#if (IncludeMultiTenancy)-->
`DbMigrator` 先迁移服务默认目标，再从 Identity 获取 DedicatedDatabase 覆盖并按物理连接去重。每个服务使用自己的固定 schema 和迁移历史表：SharedDatabase 中各服务共用数据库实例并以 `TenantId` 隔离；DedicatedDatabase 由租户配置覆盖连接，各服务仍共用该租户连接并写入自己的 schema。Identity 还会先迁移固定宿主库的 Control DbContext（租户、连接配置、OpenIddict），再迁移可按租户路由的业务 DbContext。

**首次安装**时控制库尚未迁移，租户注册表还不存在，预演只列出此刻能确定的目标（控制面、OIDC 存储、默认业务库），这时本就不可能有独立库租户，计划是准确的；`--apply` 先建好控制表，再枚举独立库租户目标并一并施加。控制库**已迁移**之后仍读不到租户注册表（表被误删、schema 配错、迁移与模型不一致）属于损坏而不是首装：预演与 `--apply` 都以非零退出码结束，不会当作"没有独立目标"。

**单个租户出问题不挡住其他租户**：某个租户取不出本服务的连接（只登记了别的服务的连接名、连接串解不开），或它的独立库迁移出错（连不上、迁移失败），只记在它名下，其余独立库照常预演或施加；跑完后逐个报出这些租户与库（租户标识、库的指纹与原因，不含连接串），并以非零退出码结束。控制库、OIDC 存储与默认业务库出错仍立即结束本次运行。因此非零退出可能意味着**部分库已经迁移**：迁移不会回滚，修好问题后重跑 `--apply`，已迁移的库不会重复执行；发布如何据此停下见 [部署说明](../docs/deploy/README.md#环境晋级)。

新建 DedicatedDatabase 租户前，必须先对新目标执行一次业务 schema 迁移，再在 Identity 中建立租户与 Secret Reference：

```bash
ConnectionStrings__MigrationTarget='<migration connection string>' \
  dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --apply
```

`MigrationTarget` 模式只迁移该服务的业务 schema，不迁移 Identity Control schema，也不枚举已登记租户。它用于打破“先登记租户才能枚举目标、但租户初始化前又必须先有表”的首次建库循环；日常发布仍使用不带该配置的全目标模式。
<!--#endif-->

修改 EF Core 模型后生成并审查迁移文件：

```bash
dotnet ef migrations add <MigrationName> \
  --context MyProjectDbContext \
  --project src/CompanyName.ProjectName.Infrastructure \
  --startup-project src/CompanyName.ProjectName.Api \
<!--#if (LocalIdentity)-->
  --output-dir Persistence/Migrations/Identity
<!--#else-->
  --output-dir Persistence/Migrations/Resource
<!--#endif-->
```

漏生成迁移时，单元测试 `MigrationSnapshotTests` 会失败：它不连库，按关系型模型与已提交的迁移快照比对。新增 DbContext 时在其中补一条对应断言。

本服务的 schema 名与迁移历史表名集中在 `src/CompanyName.ProjectName.Infrastructure/Persistence/DatabaseSchema.cs`，所有 `HasDefaultSchema` 与 `MigrationsHistoryTable` 都引用它。多个服务共用一个数据库时两者必须成对区分：只改 schema 会让各服务争用同一张历史表，一方的迁移被另一方当成已执行而跳过，部署后才暴露。已生成的迁移文件保存的是快照值：尚未部署时改完重建基线迁移；已部署的数据库须走正式迁移，不改写已施加的迁移。
<!--#if (LocalIdentity)-->

## 认证配置

库里还没有超级管理员时，启动会按 `DefaultAdmin` 创建一个（用户名 `admin`）。`DefaultAdmin:Password` 在基础配置里没有默认值，只在创建那一刻校验：缺失或不满足密码策略（至少 12 个字符）即启动失败并报出键名；已有管理员的部署不必再提供，部署环境注入 `DefaultAdmin__Password`。开发环境取 `appsettings.Development.json` 里公开的演示口令 `Admin!Local2026`，用它建出的账号会一直保留这个口令直到被修改；**连接共享的真实开发库之前**先覆盖：

```bash
dotnet user-secrets set "DefaultAdmin:Password" "<至少 12 个字符的口令>" --project src/CompanyName.ProjectName.Api
```

角色、权限和超级管理员属于不同授权维度；业务接口应同时覆盖允许、拒绝和超级管理员旁路场景。
<!--#if (OpenIddictServer)-->

OpenIddict 的 issuer、证书和 HTTPS 要求通过 `OAuth` 配置，部署要求见 [配置参考](../docs/deploy/configuration.md)，证书轮换见 [令牌证书轮换](../docs/operations/runbooks/token-certificate-rotation.md)。`OAuth:Resource`、`OAuth:ApiResources` 与各类客户端的登记见 [服务间调用](../docs/standards/service-invocation.md#identity-与资源服务对接)。
<!--#endif-->
<!--#if (ExternalLogin)-->

外部登录凭据通过 `ExternalAuth` 配置或密钥系统提供，不写入仓库，宿主管理员也可在系统设置里运行期覆盖。
每个提供商（`Github` / `Google`）配置 `Enabled`、`ClientId`、`ClientSecret`：开关默认关闭，打开且凭据齐全才可用，凭据不全只记警告、不阻止启动。
提供商后台登记后端 HTTPS 回调 `/api/v1/external-auth/{github,google}/signin`，由官方处理器在后端验证 code/state。
组合根直接使用 AddGoogle、AddGitHub 与短时服务端外部票据；Application 接收规范化 ExternalUserInfo 与可用提供商名称，不读取适配器凭据。
完整流程、Cookie 与部署规则见 [浏览器认证](../docs/standards/auth.md#浏览器认证)。
<!--#endif-->
<!--#if (IncludeNotifications)-->

通知持久化、未读状态及实时送达由通知组件提供；业务实时开启时共享一个 Hub 和一条前端连接。修改订阅时验证实际资源权限与作用域隔离。
<!--#endif-->
<!--#endif-->
<!--#if (RemoteTokenAuth)-->

## 资源管理员首次授予

在完成迁移后，以部署授权执行正式引导命令，把远端用户加入 Admin 角色；`sub` 必须为远端自然人的非空 GUID，允许在其首次访问之前执行：

```bash
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --grant-admin <sub>
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --grant-admin <sub> --apply
<!--#if (IncludeMultiTenancy)-->
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --grant-admin <sub> --tenant <tenant-id> --apply
<!--#endif-->
```

默认 dry-run 不改变任何数据或操作记录，只说明将要做的事；`--apply` 在持有初始化锁的同一事务内完成：首次初始化角色（Admin 角色首次播种当前作用域全部权限，之后按普通角色管理），本地还没有该用户时建立只含主体标识的最小用户行（首次访问时按令牌补齐资料），再把该用户加入 Admin 角色。已有用户不改资料与启停，已删除的用户不恢复。

权限经角色取得：之后调整 Admin 角色的权限、或把此人移出 Admin，都按普通角色管理生效。此人已在 Admin 中时重复执行无操作；曾被移出、被停用或被删除时普通引导一律不改，要恢复走角色管理与用户管理。记录来源是部署身份，授权依据为 `DeploymentBootstrap`。
<!--#if (IncludeMultiTenancy)-->
租户必须已经登记，目标连接经正式路由解析。回源机器身份需要租户路由读取权限。
<!--#else-->
此应用只接受宿主目标，`--tenant` 参数会被拒绝。
<!--#endif-->
<!--#if (IncludeOperationRecords)-->
授权与成功操作记录在同一数据库事务内持久化。
<!--#else-->
成功安全记录在提交之后输出；提交后、输出前进程退出仍可能丢记录。
<!--#endif-->

### 管理员恢复

管理员不能停用自己（本服务没有删除用户的端点），但移出角色、收窄 Admin 的权限属于正当的自我降级，界面不拦；没有其他可授权的管理员时（最后一位管理员被停用、删除、移出 Admin，或 Admin 角色的权限被撤空），在同一命令上加 `--restore`：

```bash
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --grant-admin <sub> --restore
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --grant-admin <sub> --restore --apply
```

预演逐项列出要修的内容，不写数据或操作记录；`--apply` 在持有初始化锁的同一事务内撤回该用户的删除（按原主体标识）、重新启用、补回 Admin 成员关系（只补这一条，曾被撤销的其他角色不复活），再把当前作用域可用、Admin 角色却没有的权限补授给它，并留下动作码为 `resource.admin-restored` 的成功记录，任何一步失败整体回滚。本地还没有该主体时，与普通引导一样先建只含主体标识的最小用户行，不创建远端身份或凭据——这与租户管理员恢复"找不到原记录就失败"不同。没有要修的就什么也不写，可以放心重复执行。

**这是共享角色的恢复**：补授落在 Admin 角色上，所有持有 Admin 的人都随之得到这些权限；补授之外，此前对 Admin 做过的其他授予原样保留。恢复不复活已作废的会话与令牌，本人重新登录即可；远端账号本身的问题（签发方停用、口令丢失）不在本服务的恢复范围内。
<!--#endif-->
<!--#if (LocalIdentity && IncludeMultiTenancy)-->

## 租户管理员恢复

宿主有内置超级管理员兜底，租户没有：租户的管理员能力全部来自本租户的 Admin 角色。管理员不能停用或删除自己，但移出角色、收窄 Admin 的权限属于正当的自我降级，界面不拦；租户把自己锁死后（最后一位管理员被停用、删除、移出 Admin，或 Admin 角色的权限被撤空，宿主的模拟登录进的也是这个账号），由部署方执行：

```bash
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --restore-tenant-admin <tenant-id>          # 预演
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --restore-tenant-admin <tenant-id> --apply  # 施加
```

命令按正式路由进入该租户的实际落点（共享库按租户过滤，独立库用控制库里登记的连接，因此与 API 共用 Data Protection 密钥环），找回开通时建的 `admin` 账号的原记录，在持有租户初始化锁的一个事务里：撤回删除（按原 Id，不新建账号）、重新启用、补回 Admin 成员关系（只补这一条，曾被撤销的其他角色不复活），再把租户侧当前可用、Admin 角色却没有的权限补授给它，并留下动作码为 `tenant.admin-restored` 的成功记录；任何一步失败整体回滚。预演逐项列出要修的内容，不写数据或操作记录；没有要修的就什么也不写，可以放心重复执行。

- **这是共享角色的恢复**：补授落在 Admin 角色上，该租户所有持有 Admin 的人都随之得到这些权限；补授之外，此前对 Admin 做过的其他授予原样保留。
- 口令与两步验证不重置，已作废的会话不复活：恢复后用原口令重新登录；口令也遗失时，登录后由本人或其他管理员按正常流程重置。
- `admin` 账号已改名或被永久清除时命令直接失败、不改任何数据：它只恢复原记录，从不替人新建账号。这时由该租户其他管理员在用户管理里建管理员并分配 Admin 角色。
<!--#if (IncludeOperationRecords)-->
- 记录的操作人是部署身份（`deployment:<用户>@<主机>`），授权依据为 `DeploymentBootstrap`，与恢复在同一事务内写进该租户的库，租户管理员在本租户的操作记录里看得到。
<!--#else-->
- 记录的操作人是部署身份（`deployment:<用户>@<主机>`），授权依据为 `DeploymentBootstrap`，在提交之后写进安全行为日志。
<!--#endif-->
<!--#endif-->
<!--#if (LocalIdentity)-->

## 两步验证的再认证与恢复

改写两步验证凭据的入口都假设会话可能被盗用，按下面的规则把关（其他敏感变更同一套规则，入口清单见 `../docs/standards/auth.md`“敏感变更的近期认证”）：

- **确认是本人**：绑定验证器、换绑、管理员重置他人的两步验证之前，会话的认证时间（`auth_time`）在 15 分钟内即放行；否则出示操作者自己的一种凭证——当前口令、身份验证器上的验证码或通行密钥，三选一（`Auth:ReauthenticationRequired`；给了多种是字段错误）。出示了凭证就只按它判定，不回落到会话时间：错误与登录共用失败计数和锁定；账号没有这种方式（没有口令却给口令、没开两步验证却给验证码、没有可用的通行密钥却给断言）返回 `Auth:ReauthenticationMethodUnavailable`，不计数。既没有口令、也没有身份验证器和通行密钥的账号拿不出凭证，只能重新登录（`Auth:SignInAgainRequired`），新的认证时间满足同一条判据。"组织要求设置"的受限会话只是受限，不算刚认证；模拟登录的会话也不算。
- **验证码只用一次**：确认身份的验证码与登录第二步同一个判定，同一个 30 秒步只认一次——刚在登录时用过的码要等下一个。通过即用掉并独立提交，随后的操作失败（如邮箱已被占用）也不退回。确认只对这一次操作有效，不改写会话的认证时间，没有宽限期。
- **停用与重发**：停用两步验证每次都要验证码，有口令的账号另要口令，没有口令的账号只要验证码；重发恢复码认验证码或通行密钥，不认恢复码。恢复码是账号级的，规则见 `../docs/standards/auth.md`“通行密钥”与“恢复码是账号级的”。
- **一次设置一个 Id**：开始设置返回 `setupId`，确认时原样带回。别处重新开始过、或之后凭据变过（改口令、重置等换发了安全版本）的设置都作废（`Auth:TwoFactorSetupExpired`），界面清掉二维码与密钥、请本人重新开始。
- **换绑**：已启用时再开始设置就是换绑，要出示现有验证器上的码，或者验证器丢了时用一个恢复码（当场用掉）。现有验证器上的码同时算作确认身份，只校验一次，不必再带口令；恢复码不算确认身份，会话开久了仍要口令。确认新验证器后发一组新恢复码，旧验证器与旧恢复码随即作废，其他设备退出。恢复码只能换得一次重新绑定，不能直接换出新的恢复码；整个过程不经过"未启用"，组织强制两步验证时同样可用。
- **组织要求两步验证**：要求按每个请求判定，不写进会话：打开后，没启用两步验证的人（包括打开它的管理员）下一个请求即受限，只能完成设置；关闭或本人完成设置后，下一个请求即解除，同一个 Cookie、同一个会话，认证时间不变。要求开着时不能去掉最后一个独立因子：没有通行密钥时不能停用身份验证器，没有身份验证器时不能删掉最后一把通行密钥（`Auth:TwoFactorRequiredByPolicy`）。规则与界面接续见 `../docs/standards/auth.md`“敏感变更的近期认证”末尾。
- **管理员重置**：不能重置自己（`User:SelfTwoFactorResetForbidden`）——本人走个人设置的换绑或停用；重置他人前管理员要刚认证过，或出示自己的口令或验证码（校验与用掉的步都落在管理员自己的账号上）。重置清掉全部第二步方式（身份验证器、通行密钥、恢复码、邮箱验证码）与首选，部署级重置同样如此。
- **并发**：启用、换绑、停用、重发、登录第二步、管理员重置，连同改口令、设置与重置口令、改邮箱与确认、外部绑定与解绑、口令登录与外部登录登记会话，都先取同一把用户级凭据锁（`UserCredentialLock`），在锁内读、写并提交，互相不会覆盖，撤销之后也不会冒出凭旧状态登记的会话（见 `../docs/standards/auth.md`“凭据并发”）。确认身份在操作者自己的锁内独立提交：本人入口在同一把锁里先确认、再另开事务重读并改写；管理员入口与外部绑定的预检先在自己的锁里确认并释放，再取目标（或外部票据）的锁，不嵌套、两位管理员互相操作时也不交叉持锁。
<!--#if (ExternalLogin)-->
- **没有口令的账号丢了验证器**：会话开久了时，恢复码不能用来确认身份，对话框提供"重新登录"：重新走外部登录、第二步用掉一个恢复码得到新会话，回到换绑再用掉另一个恢复码完成换绑，一共两个。只剩最后一个恢复码时，登录后已无可出示的第二因素，换绑被拒，由管理员在用户管理里重置两步验证；内置超级管理员或在线没人能重置时用下面的部署级重置。不提供免验证的停用入口。
<!--#endif-->

验证器与通行密钥都丢了、恢复码也用尽、在线又没人能替他重置时（典型是内置超级管理员——只能由本人操作），由部署方执行：

```bash
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --reset-two-factor <user-id>          # 预演
dotnet run --project src/CompanyName.ProjectName.DbMigrator -- --reset-two-factor <user-id> --apply  # 施加
```

<!--#if (IncludeMultiTenancy)-->
租户里的用户加 `--tenant <tenant-id>`，命令按正式路由进入该租户的实际落点（与 API 共用 Data Protection 密钥环）。
<!--#endif-->
施加时在该用户的凭据锁内、一个事务里停用两步验证、清掉密钥、通行密钥与恢复码、结束全部会话，并留下动作码为 `user.two-factor-reset`、授权依据为 `DeploymentBootstrap` 的成功记录；本人随后用口令登录并重新设置。边界：

- 结束的会话经提交后的事件作废校验缓存：与 API 共享 Redis 时立即生效，不共享时运行中的 API 最多滞后一分钟。
- 已签发给应用的访问令牌不在这里撤销，按有效期自然过期；安全提醒也只在 API 里发送。这条命令用于本人找回，不用于账号失窃——失窃时先在线停用账号。
<!--#endif-->

<!--#if (SpaFrontend)-->
## 前后端联调

本机开发时浏览器访问前端开发服务器（`http://localhost:4200`），由它把 API 与 Hub 请求转发到本服务，见[前端说明](../frontend/README.md)。
本服务不托管开发中的前端；部署时前端构建产物放进 wwwroot 由本服务托管，同源要求见 [浏览器认证](../docs/standards/auth.md#浏览器认证)。
<!--#endif-->
