# 部署说明

项目根 `Dockerfile` 和 `deploy/` 是当前容器部署配置的事实来源。部署前先核对实际镜像、环境变量、数据库迁移、健康检查和回滚能力，不使用文档中的假定值替代配置。

## 配置与机密的分层

<!--#if (SpaFrontend)-->
各环境共用同一套配置键，只换值的来源。浏览器页面与所属 API 必须同源（同镜像托管，或分进程经部署代理统一外部源），前端构建不注入 API 地址；同源、Cookie 与 SameSite 规则见 [浏览器认证](../standards/auth.md#浏览器认证)。
<!--#else-->
各环境共用同一套配置键，只换值的来源。纯 API 通过 Bearer 认证，不配置浏览器 Cookie、OIDC 回调或前端网关。
<!--#endif-->
<!--#if (LocalIdentity)-->

口令哈希使用 Identity 原生 IdentityV3 格式（PBKDF2-HMAC-SHA512），模板默认迭代 220,000 次。通过 `PasswordHash__IterationCount` 按部署硬件调整成本；提高成本后，存量口令在正确且允许登录时自动重新哈希，保留安全版本与已有会话。算法与格式由原生实现维护，参数政策见 [OWASP](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2)。
<!--#endif-->
<!--#if (OpenIddictServer)-->

多系统退出不会即时通知其他依赖方：模板没有 OIDC back-channel logout。登记为会话绑定的依赖方在 Identity 会话结束后，于访问令牌到期时续期失败而收敛；未绑定的依赖方持有的刷新令牌不受影响。

授权与退出请求缓存在控制库的令牌表里（request token），重新认证证明、退出确认凭据与 Cookie 由 Data Protection 保护；重新认证证明比较签发时刻与会话开始时间，各副本的时钟须同步。多实例 Identity 之间一次登录或退出可能落在不同副本，因此除共享缓存外，还要共享同一个控制库、同一套令牌证书、同一个 Data Protection 密钥环与应用名，以及分布式锁。修改开放应用登记后滚动重启 Identity：OpenIddict 应用缓存只在本进程失效。
<!--#endif-->

部署时记录 API 和 DbMigrator 的固定版本或 digest，不用 `latest` 充当发布身份。模板 Compose 中的 `:latest` 是示例值，实际发布需要由项目流水线明确替换。

| 环境 | `ASPNETCORE_ENVIRONMENT` | 非机密配置 | 机密 |
| --- | --- | --- | --- |
| 本机开发 | `Development` | `appsettings.Development.json`（随仓库提交，全队共用） | `dotnet user-secrets`（每人一份，见 [后端说明](../../backend/README.md#开发配置)） |
| 集成测试 | `Testing` | 测试夹具显式给出 | 测试夹具给出确定性的假值 |
| 测试 / 预发 | `Staging` | 环境变量，需要时加 `appsettings.Staging.json` | CI/CD 的 secret 注入为环境变量 |
| 生产 | `Production` | 环境变量与 `appsettings.Production.json` | 密钥管理系统，经环境变量或挂载文件注入 |

开发环境以外，下列只在单机上成立的回落缺配即启动失败：

<!--#if (SpaFrontend)-->
- Data Protection 密钥必须落在 Redis 或共享持久目录（`DataProtection:KeysPath`），并随数据一同备份。`KeysPath` 只替代密钥的存储位置，不替代 Redis 承载的分布式缓存与锁。存储位置应只允许本服务访问：Redis 不对外发布端口，跨主机或使用托管 Redis 时设口令并开启 TLS；目录用文件系统权限限制到运行身份。显式指定存储位置后框架不再自动加密密钥，需要静态加密时按官方 `ProtectKeysWith*` 在 `AddMyProjectDataProtection` 里追加。
<!--#endif-->
<!--#if (LocalIdentity && IncludeMultiTenancy)-->
- 控制库用 Data Protection 加密租户连接串，API 与 `DbMigrator` 必须使用同一个密钥环与应用名。
<!--#endif-->
<!--#if (SpaFrontend)-->
- 未配置 `ConnectionStrings:Redis` 不阻止启动：单实例配 `KeysPath` 是合法部署。此时分布式缓存与分布式锁回落到进程内存，启动日志各有一条 Warning；多副本必须配置 Redis，并共享 Data Protection 密钥。
<!--#else-->
- 未配置 `ConnectionStrings:Redis` 不阻止单实例启动，分布式锁回落到进程内并输出 Warning；纯 API 不注册浏览器会话、Data Protection 或其 `KeysPath` 配置。多副本必须配置 Redis。
<!--#endif-->
- TLS 在网关或 ingress 终结时，所有形态（含 Standalone）都须配置 `ForwardedHeaders:KnownProxies` / `KnownNetworks` 还原原始协议与主机。
<!--#if (SpaFrontend)-->
  浏览器写请求的 Origin 校验同样依赖这些转发头；来源拒绝返回 Problem Details，Warning 日志给出收到的 Origin 与计算出的本源。
<!--#endif-->
<!--#if (OpenIddictServer)-->
- 令牌签名与加密证书必须显式提供（`OAuth:SigningCertificates`、`OAuth:EncryptionCertificates` 各至少一项，每项 `Path`、`Password`），与 HTTPS 证书分开；开发证书只用于本机开发。任何一项缺路径、无法加载或没有 RSA 私钥，启动失败并指出带下标的键名。
- TLS 在网关或 ingress 终结时不要打开 `OAuth:DisableHttpsRequirement`：OpenIddict 要求生产环境即使在反向代理后也不关闭传输安全检查，按上一条配置转发头即可。
<!--#endif-->
<!--#if (Email)-->
- 发信基线指向本机邮件捕获器（`localhost:1025`）。部署时覆盖 `Leistd:Email:Smtp` 的 `Host`、`Port`、`EnableSsl`、`DefaultFromAddress`，`Username` 与 `Password` 成对提供或都不提供；compose 以 `SMTP_HOST`、`SMTP_PORT`、`SMTP_FROM_ADDRESS` 为必填，账号口令只填一项时启动失败。
- 开启注册邮箱验证（`UserRegistration:EnableEmailVerification`，compose 的 `ENABLE_EMAIL_VERIFICATION`）时必须提供 `Leistd:Security:VerificationCodes:Key`（compose 的 `VERIFICATION_CODES_KEY`）：Base64、至少 32 字节，所有实例相同且跨重启不变，可用 `openssl rand -base64 32` 生成。缺失或无效即启动失败并报出键名；关闭时可不提供；非空无效密钥始终在启动时失败，缺失时无法在系统设置里开启。
<!--#endif-->
<!--#if (ExternalLogin)-->
- 外部登录提供商（`ExternalAuth:Github`、`ExternalAuth:Google`）的 `ClientId` 与 `ClientSecret` 成对提供，只给一项启动失败；都不给则不启用该提供商。提供商后台登记的回调地址是 `https://<站点对外地址>/api/v1/external-auth/{github,google}/signin`。
<!--#endif-->

## 本地验证

本机开发时，依赖服务用 `deploy/docker-compose.dev.yml` 起在本机（只绑定 127.0.0.1），后端用 `dotnet run` 启动。
<!--#if (SpaFrontend)-->
前端另用 `npm start` 启动。
<!--#endif-->
完整容器形态用生产 compose 验证：

```bash
cp deploy/.env.example deploy/.env    # 逐项填写，deploy/.env 已被 Git 忽略
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.override.yml up --build
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.override.yml down
```

compose 里的 `db-migrator` 固定带 `--apply`。施加前先预演：用 `--entrypoint` 重新指定入口时 compose 不再附加服务的 `command`，迁移作业不带参数运行，只列出待执行的迁移与 SQL，不改库。不加 `--entrypoint` 直接 `run db-migrator` 会照常施加。

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.override.yml \
  run --rm --build --entrypoint "dotnet CompanyName.ProjectName.DbMigrator.dll" db-migrator
```

生产配置只在 HTTPS 下签发会话 Cookie（`Secure`、`__Host-` 前缀），compose 本身只提供 HTTP（`http://localhost`）。
<!--#if (SpaFrontend)-->
Chrome 等浏览器把 `http://localhost` 视为安全来源，本机经它登录可用；经局域网地址或其他主机名以 HTTP 访问时浏览器丢弃会话 Cookie，登录请求成功但随后的请求仍是未登录。
<!--#endif-->
<!--#if (OpenIddictServer)-->
OIDC 端点（授权码流程、令牌）在生产配置下拒绝 HTTP 请求，验证开放应用或资源服务联调须在前面加一层终结 TLS 的反向代理，并按上文配置转发头。
<!--#endif-->
只验证镜像能启动、迁移与健康端点时不需要 HTTPS。

命令和文件存在性应以当前项目为准。

## 确认已部署版本

构建镜像时由流水线传入发布身份：

```bash
docker build --build-arg APP_VERSION=1.4.0 --build-arg APP_REVISION="$(git rev-parse HEAD)" --target api -t <仓库>/companyname-projectname:1.4.0 .
```

部署后按两处核对，不按镜像标签推断：

- 运行中的 API 返回自己的版本：`GET /api/v1/service-info` 的 `version`（程序集版本，`APP_VERSION` 补齐为四段）。
- 镜像记录源码提交：`docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' <容器或镜像>`。为空说明镜像不是由流水线按上面的方式构建的。

## 部署形态

- **Docker Compose 单机**：机密写在 `deploy/.env` 或由流水线导出为环境变量，清单见 `deploy/.env.example`；必填项在 compose 里以 `${VAR:?}` 引用，漏填时带着变量名失败。
<!--#if (OpenIddictServer)-->
  令牌证书以 compose `secrets` 挂载到 `/run/secrets`，只有口令走环境变量。单机 compose 的文件型 secret 是按宿主机文件权限的绑定挂载，`mode`、`uid` 不生效：证书文件必须对容器用户（UID 1654）可读，例如 `chown 1654 certs/*.pfx && chmod 400 certs/*.pfx`，或 `chmod 640` 并把属组设为 1654。宿主机上 `600` 且属主 root 的证书会让 API 启动失败。
<!--#endif-->
- **容器用户**：API 与迁移镜像以镜像自带的非 root 用户（UID 1654）运行。挂进容器的文件要对它可读。
<!--#if (SpaFrontend)-->
  Data Protection 密钥改用文件目录（`DataProtection:KeysPath`）而不是 Redis 时，挂载的目录要对它可写。
<!--#endif-->
- **Kubernetes**：非机密配置放 ConfigMap，机密放 Secret，以环境变量（`Section__Key`）注入，证书类文件以卷挂载。`DbMigrator` 作为发布前的一次性 Job（带 `--apply`），成功后再滚动发布 API；就绪与存活探针分别指向 `/api/health/ready` 与 `/api/health/live`。多副本必须配置 Redis：分布式缓存与分布式锁依赖它。
<!--#if (SpaFrontend)-->
  Data Protection 密钥默认保存在 Redis 中；各副本还须使用一致的应用名。
<!--#endif-->
<!--#if (IncludeRealTime || IncludeNotifications)-->
  实时推送多副本须配置 SignalR 背板（模板未内置），否则只有连在发布方节点的客户端收到推送。
<!--#endif-->
- **云平台（容器服务、应用服务）**：配置写应用设置，机密放托管密钥库并以托管身份读取（如 Key Vault 引用）。这类接入与平台绑定，确定平台后再加，不预置在模板里。

<!--#if (OpenIddictServer)-->
## 令牌证书轮换

签名与加密证书都按集合登记，轮换期间新旧同时在册（重叠轮换），不要直接替换文件硬切。OpenIddict 把全部签名证书发布进 JWKS；
签名时优先用已生效且到期最晚的一张，尚未生效（`NotBefore` 在未来）的排在后面（顺序在启动时排定）；加密用优先的一张，解密时全部可用。
集合里至少要有一张当前有效的签名证书与加密证书，否则 OpenIddict 拒绝启动。

1. **发布新公钥**：新签名证书的 `NotBefore` 设在未来（留出第 2 步的时间）、`NotAfter` 晚于旧证书、使用新的 kid，作为新的一项加入
   `OAuth:SigningCertificates` 后滚动重启 Identity。此时 JWKS 已含新 kid，签名仍用旧证书。
2. **确认各资源服务已取得新 kid**：所有资源服务都有 OpenIddict 访问令牌验证链路；启用浏览器会话时另有 ASP.NET Core OIDC 处理器的 id_token 链路，登录回调与续期也会验证访问令牌。两条链路各自缓存配置。缓存过期（默认 12 小时）后要等下一个请求才会重新抓取，单靠时间过去不能确认，
   所以滚动重启各资源服务副本，并经真实 Bearer 请求预热；启用浏览器会话的资源服务还须执行一次登录，确认每个副本的适用验签链路都已取得新 kid。
3. **切换签发**：新证书过了 `NotBefore` 后滚动重启 Identity。证书的优先顺序在启动时排定，不重启就一直用旧证书签名。
4. **撤掉旧证书**：授权码、刷新令牌（默认 14 天）与授权、退出请求的 request token 由 Identity 自己签名并加密，
   兑现时既要验签也要解密，所以旧的签名证书和加密证书都要保留到它们全部过期。旧签名证书还要覆盖访问令牌（`OAuth:AccessTokenLifetime`）
   与依赖方保存的 id_token：后者在退出时作为 `id_token_hint` 回到 Identity，验不了签会让退出失败，所以至少保留到依赖方会话的最长寿命。
   撤掉时从集合中删除该项并滚动重启。

第 2 步漏做或资源服务在窗口内才启动时，资源服务遇到未知 kid 会先向配置的签发方刷新一次再验证：访问令牌链路合并并发、
抓取超时 10 秒、请求刷新每分钟至多一次，id_token 链路由 IdentityModel 自身刷新重试一次，因此通常只多一次网络往返；
但签发方不可达、或刚刷新过（IdentityModel 的 5 分钟间隔、本服务的 1 分钟限频）时，这些请求仍会以 401 失败，
不能把它当作省略第 2 步的理由。

签发方证书泄露时不走重叠流程：从集合中移除并重启 Identity，用它签发、加密的授权码、刷新令牌与 request token 在 Identity 上随即失效。
资源服务已经缓存了这张证书的公钥，认得的 kid 不会触发刷新，只重启 Identity 不能让它们拒绝旧签名的访问令牌：
要接着滚动重启各资源服务副本（或等缓存过期后的首次抓取），并用旧证书签的令牌确认每个副本都已拒绝。

<!--#endif-->
## PostgreSQL 与 Redis 大版本升级

模板 compose 的 PostgreSQL 卷名带主版本号（`pgdata-18`），挂载在 `/var/lib/postgresql`：PG18 起官方镜像的数据目录是 `/var/lib/postgresql/18/docker`。新主版本不能直接打开旧版本的数据目录，官方镜像发现旧数据目录或旧挂载点会拒绝启动；换成新卷则得到一个只有初始化内容的空库，业务数据要靠导出、导入迁移。Redis 从 7 升到 8 直接换镜像，Redis 8 能读入 7 写出的数据，但 8 写出的数据不保证能被 7 读回。

从旧模板（`postgres:15-alpine`、卷 `pgdata` 挂在 `/var/lib/postgresql/data`；`redis:7-alpine`）升级时，下面的命令都在生成项目根目录执行，`dc` 代表这次部署实际使用的 compose 调用。项目名、env 文件和 override 必须与升级前一致，否则 compose 会另建一套容器和卷：

```bash
dc() { docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.override.yml "$@"; }
```

Compose 给卷名加项目名前缀，下文的 `<旧 PG 卷>`、`<Redis 卷>` 指实际卷名，升级前用 `docker inspect $(dc ps -q db) $(dc ps -q redis) --format '{{range .Mounts}}{{.Name}} {{end}}'` 记下。按下面的顺序操作，不要在导入之前启动整套服务：

1. 停止写入：只保留旧的 `db` 与 `redis`，其余服务全部停掉（`dc stop $(dc config --services | grep -vxE 'db|redis')`），另行部署的迁移、恢复作业也一并停掉。
2. 备份 Redis：选几个代表键作为核对基准，普通键记下值（`dc exec redis redis-cli get <键>`）。
<!--#if (SpaFrontend)-->
   Data Protection 密钥也存在 Redis 里，它不是可以丢弃的缓存；这类机密键只记键名，事后用 `exists` 核对，不输出值。
<!--#endif-->
   然后 `dc stop redis`，备份卷：`docker run --rm -v <Redis 卷>:/data -v "$PWD":/backup alpine tar czf /backup/redis-data.tgz -C /data .`。
3. 列出要迁移的库：`dc exec db psql -U postgres -Atc "select datname from pg_database where not datistemplate and datname <> 'postgres'"`。默认只有业务库 `MyProject`；独立库租户与控制库在同一台服务器上时，这里还会列出租户库。
4. 逐库导出为归档格式，并另存 `deploy/.env` 等机密：`dc exec -T db pg_dump -U postgres -Fc <库名> > <库名>.dump`。角色不在库的导出里：模板只用 `postgres` 与应用角色（`APP_DB_USER`），新库由初始化脚本按同样的环境变量重建；手工另建过角色的，先用 `pg_dumpall -U postgres --roles-only` 导出，在新库上按需创建。
5. 换用新版本的 compose 文件（`dc` 的参数不变），只启动数据库：`dc up -d db`，等健康检查通过。
6. 导入：业务库已由初始化脚本创建，清掉初始内容后导入，`dc exec -T db pg_restore -U postgres --clean --if-exists --exit-on-error -d MyProject < MyProject.dump`；其他库连库一起建，`dc exec -T db pg_restore -U postgres --create --exit-on-error -d postgres < <库名>.dump`。
7. 核对后放行：抽查关键表的行数，用应用角色连接业务库读写一次；`dc up -d redis` 后逐个核对第 2 步的代表键，`dbsize` 只作辅助；最后 `dc up -d` 启动其余服务，确认健康检查通过、已登录的会话照常可用。
8. 回退：先像第 1 步一样停掉所有写入方。PostgreSQL 在放行写入之前，把 `db` 换回旧镜像与 `<旧 PG 卷>` 就回到升级前的数据；放行之后旧卷已经落后，不能无损回退，只能从新库的备份恢复。Redis 回退先 `dc stop redis`，确认已停止后把 `redis-data.tgz` 解回 `<Redis 卷>`，再用 7 启动并核对代表键，不要在运行中的 Redis 卷上解包，也不能只把镜像标签换回去。整套换回旧 compose 文件时，两项都要做，核对通过后再放行应用。
9. 清理：新库稳定运行、备份就绪后，再 `docker volume rm <旧 PG 卷>`。

本机开发库用 `docker compose -f deploy/docker-compose.dev.yml` 调用，库名是 `companyname-projectname`，没有 backend 与应用角色，Redis 也不挂卷。旧卷 `dev-pgdata` 里的数据需要保留时，先停掉本机运行的 API 与迁移、恢复进程，再按上面第 3–6 步导出、`up -d db`、导入（业务库换成 `companyname-projectname`），核对后重新 `dotnet run`；不需要就直接删除该卷（同样带项目名前缀）。

## 升级后新增的权限

Admin 角色只在首次初始化时播种当时的全部权限，之后按普通角色管理：升级不会把新版本增加的权限（如仪表盘 `App.Dashboard`）补给已部署库里的 Admin 角色，持有它的人在被授予之前看不到对应菜单、调用对应接口得 403。发布新版本后由管理员在角色权限里授予：

<!--#if (LocalIdentity)-->
- 宿主：内置超级管理员不受权限限制，用它给 Admin 等角色勾选新权限。
<!--#if (IncludeMultiTenancy)-->
- 租户：此后开通的租户自动带上；已有租户由其管理员给本租户的 Admin 角色授予（Admin 持有权限配置权限）。
<!--#endif-->
<!--#else-->
- 持有权限配置权限（`App.Roles.ManagePermissions`）的管理员给 Admin 等角色勾选新权限。
<!--#endif-->
<!--#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))-->
- 没有人还能配置权限时，用下文的管理员恢复命令：它会把 Admin 角色缺少的当前可用权限一并补齐。
<!--#endif-->

<!--#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))-->
## 管理员恢复

管理员把自己锁在门外（最后一位管理员被停用、删除、移出 Admin，或 Admin 角色的权限被撤空）时，按正式命令恢复，不手改数据库：
<!--#if (RemoteTokenAuth)-->
`DbMigrator --grant-admin <sub> --restore`，命令与语义见 [后端说明](../../backend/README.md#管理员恢复)。
<!--#else-->
`DbMigrator --restore-tenant-admin <tenant-id>`，命令与语义见 [后端说明](../../backend/README.md#租户管理员恢复)。宿主有内置超级管理员，不需要这条命令。
<!--#endif-->

<!--#if (LocalIdentity)-->
- 以一次性作业运行，使用与迁移作业相同的镜像、数据库身份与 Data Protection 密钥环（独立库租户的连接串靠它解开）；先不带 `--apply` 预演，核对输出逐项列出的修复内容，再加 `--apply` 施加。
<!--#else-->
- 以一次性作业运行，使用与迁移作业相同的镜像与数据库身份；先不带 `--apply` 预演，核对输出逐项列出的修复内容，再加 `--apply` 施加。
<!--#endif-->
- 恢复补授的权限落在共享的 Admin 角色上，持有该角色的所有人都随之得到；变更窗口需要相应的审批。
- 命令持有初始化锁，直到自己的事务提交之后才释放。锁只在共用同一锁实现的进程之间互斥：恢复作业是独立进程，只要它可能与在线 API 或另一个恢复作业同时运行，就必须配置与 API 相同的 `ConnectionStrings:Redis`，与 API 副本数无关。锁键还带前缀：默认值由代码给出、各进程一致；部署侧覆盖 `Leistd:Lock:Redis:KeyPrefix` 时，API 与所有迁移、恢复作业必须设成同一个值，否则锁键不同、照样不互斥。
- 作业收到 SIGTERM 等停止信号时，等锁与提交前的工作随之取消并以非零码退出；提交一旦开始不再响应取消，可能照常完成。被取消后先核实实际状态再重跑（命令幂等）。
<!--#if (LocalIdentity)-->
- 不用 Redis 时各进程只有进程内锁、彼此不互斥，这时串行运维：同一时间只跑一个恢复作业，并避开对同一租户的开通。
- 锁的保证限于恢复一侧：恢复从读取到提交全程持锁。在线租户开通只在写入开通数据期间持锁，返回后由租户管理在外层提交，提交前锁已释放；恢复恰在这个窗口取得锁时读不到尚未提交的开通数据。因此不要对创建请求尚未返回的租户执行恢复，锁不承诺两边整个事务互斥。
<!--#else-->
- 不用 Redis 时各进程只有进程内锁、彼此不互斥，这时串行运维：同一时间只跑一个引导或恢复作业，并避开 API 启动（启动初始化取同一把锁）。
<!--#endif-->

<!--#endif-->
## 生产边界

- 密钥和生产凭据由环境变量或密钥管理系统提供，不写入仓库。
<!--#if (OpenIddictServer)-->
- 浏览器整页进入授权端点，发现文档、JWKS、令牌与 UserInfo 由 Resource 后端访问，不要求为这些服务端协议通信开放浏览器 CORS。本机 Angular 开发代理将 `/api/**` 转到 API。
  生产 API 的 `Cors:AllowedOrigins` 默认为空；仅供显式需要的其他 API 集成使用，不负责浏览器认证导航。
- 下游资源服务与各类客户端的登记（含 `OAuth:ApiResources`）见 [服务间调用](../standards/service-invocation.md#identity-与资源服务对接)。
- 各类撤销在下游资源服务上生效的时间不同。资源服务只验签，不回本服务查状态；Access Token 有效期由 `OAuth:AccessTokenLifetime` 决定（默认 10 分钟）。
  调短它让下面的窗口收敛更快，代价是依赖方续期更频繁；它必须是整秒且长于 1 分钟——资源服务的浏览器会话在令牌剩余 1 分钟时续期，
  不长于这个窗口时每个请求都要续期，启动时拒绝并指出该键。测试与 CI 要观察真实到期时，用环境变量把它缩短（如 `OAuth__AccessTokenLifetime=00:01:30`），
  并从实际签发的令牌（`exp - iat`）确认已生效，不要靠等满默认寿命。
  - **停用或删除账号**：同时撤销该用户已签发的令牌，本服务立即拒绝；资源服务要等 Access Token 过期，之后也刷新不到新令牌。
  - **停用或删除租户**：本服务每个请求都查注册表，立即拒绝，刷新令牌也换不到新令牌；资源服务同样要等 Access Token 过期。
  - **撤销会话（含"退出其他设备"、退出登录与空闲到期）**：作废本服务的登录会话，不撤销已签发的访问令牌。之后是否还能刷新取决于开放应用的"会话绑定"：绑定的客户端（授权时记下了会话）刷新随即被拒，下游访问在访问令牌到期时结束；未绑定的客户端不受影响，刷新令牌默认 14 天有效且滑动续期，持续刷新可以一直访问下去，调短 Access Token 有效期也改变不了这一点。

  停用或删除账号、租户之后无法再刷新，所以这两类的窗口可以靠调短 Access Token 有效期来缩短。资源服务改用令牌内省（`UseIntrospection()`）能让**已撤销的令牌**即时失效，所以对停用或删除账号有效；租户停用还要先在停用时撤销该租户的令牌才行。
<!--#endif-->
<!--#if (RemoteTokenAuth)-->
- 跨服务客户端、Issuer、Audience 与机器身份配置见 [服务间调用](../standards/service-invocation.md#identity-与资源服务对接)。
- readiness 是启动门禁：首次成功解析发现文档、精确比对 issuer 并取得非空 JWKS 后锁存成功。
  冷实例在 Identity 不可达时 `/api/health/ready` 返回 503，`/api/health/live` 仍为 200；
  日志指出发现文档或签名密钥依赖（探针请求时健康检查与请求日志各记一条），Identity 恢复后自动重试并进入就绪。
  热实例保持就绪；只有 OpenIddict 验证器已缓存所需密钥时，才能在 Identity 停机期间继续本地验签。
  该探针不表示 Identity 持续可达，也不检查租户路由。
  采用 [ASP.NET Core 分离 readiness/liveness 的启动任务示例](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0#separate-readiness-and-liveness-probes)的门禁语义，
  不周期探测共享 Identity，避免其短暂故障使所有资源实例同时摘流。
  代价是就绪不保证新密钥获取、新令牌获取或租户回源成功，这些依赖失败在对应请求上暴露。
<!--#if (ResourceBrowserSession)-->
- 浏览器使用后端 OIDC 机密客户端与 Cookie 会话，服务端保存并刷新令牌；配置、回调、缓存/密钥与 CSRF 边界见 [浏览器认证](../standards/auth.md#浏览器认证)。
<!--#endif-->
- 本服务只验证身份服务签发的令牌，不回去查账号与租户状态。身份服务那边停用账号、撤销会话、停用或删除租户，已签发的 Access Token 在本服务仍然有效，直到过期（有效期由身份服务决定）。各类撤销的完整边界见身份服务的部署文档。改用令牌内省（OpenIddict 验证端的 `UseIntrospection()`）只能让身份服务**已撤销的令牌**即时失效，代价是每个请求多一次往返。
- 本服务自己禁用成员（用户管理里的禁用）不依赖身份服务，下一个请求即答 403，边界见 [资源服务的成员启停](../standards/auth.md#资源服务的成员启停)。
<!--#endif-->
<!--#if (IncludeNotifications && Email)-->
- 通知邮件要附可点开的站内链接时配置 `Leistd:Notifications:Email:PublicBaseUrl`（站点对外地址；哈希路由以 `/#` 结尾），不配则不附。
<!--#endif-->
<!--#if (IncludeOperationRecords)-->
- 操作记录默认**只增不减**。需要保留期时打开 `Leistd:OperationRecords:Retention:Enabled`（或由宿主管理员在系统设置的「审计」面板打开），
  到期记录会被搬进 `OperationRecordArchives` 表而不是删除；归档表不参与日常查询，但数据仍在库里，容量规划要把它算进去。
  配置里的开关与保留天数是基线，界面上的设置优先，归档任务每轮读取；执行时刻与批大小只在配置里。
  归档逐个进入宿主库与每个独立库，某个库失败只记错误、最迟在下一个调度时段重做（按截止时间扫描，积压会一并搬走）；
  多副本部署经分布式锁每轮只跑一份，要求配置 Redis，否则每个副本各跑一遍。
- **多个服务共用一个 Redis 时，每个服务的 `Leistd:Lock:Redis:KeyPrefix` 必须互不相同**（默认取应用名，由代码给出；追加环境时由部署侧覆盖，同一服务的 API 与所有迁移、恢复作业必须同值）。
  周期任务的锁键是固定的作业名（如 `operation-records.archive`），前缀相同的两个服务会互相抢同一把锁——
  抢不到的那个当轮直接跳过自己的库，而两边日志各自都正常。
- 「只增不减」由应用契约保证。需要数据库层也保证时，把 Runtime Secret 对 `OperationRecords` 表的权限收敛到 INSERT／SELECT；
  代价是归档要从原表删除，**收敛权限与启用保留期不能同时成立**，二选一并在部署记录里写明。
- 操作记录表增长到按时间范围查询变慢时，再按时间分区（数据库层 DDL，由 `DbMigrator` 的 DDL 身份执行），不预先做。
<!--#else-->
- 安全行为写入 `Leistd.OperationRecords` 结构化日志。成功事件在真实事务提交后输出，失败事件立即输出；必须保留 Information 级别并配置生产日志采集与保留策略。提交后、输出前进程退出仍可能丢失记录，日志模式不提供数据库模式的同事务持久化保证。
- 多个服务共用 Redis 时，各服务的 `Leistd:Lock:Redis:KeyPrefix` 必须互不相同，避免周期任务相互抢锁；默认取应用名，覆盖时同一服务的 API 与所有迁移、恢复作业必须同值。
<!--#endif-->
<!--#if (LocalIdentity)-->
- 原始 IP 保存在三处，各有各的期限，合规评估按这三处分别写明：
  - **会话表**：会话过期后不再使用，由登录时的本人清理或每天的 `auth.sessions.cleanup` 作业删除（逐库执行，含停用租户的库），作业正常运行时最长比有效期多留约一天；
<!--#if (IncludeOperationRecords)-->
  - **操作记录**（会话撤销等记录的目标名里带 IP）：跟随操作记录的保留期，见上；
<!--#else-->
  - **安全行为日志**（会话撤销等记录的目标名里带 IP）：由日志采集系统的保留与脱敏策略管理；
<!--#endif-->
  - **用户的最近登录 IP**：是当前状态字段而非历史，随用户记录保留、不单设保留期。删除用户是软删除，**不会**清掉它；合规要求删除时须物理删除用户记录或显式清空该字段。
<!--#endif-->
- 模板不内置限流。对外提供登录的服务应在网关或 ASP.NET Core 限流中间件上按来源 IP 限流：
  应用内的失败登录计数按账号聚合，同一 IP 轮换账号的撞库不会触发它。
- API 启动时不执行 DDL。部署流水线先以 Migration Secret（DDL 身份）运行一次性 `DbMigrator`，任一目标失败即以非零码退出并阻断发布，成功后再发布 API；API 使用 Runtime Secret 且只持有 DML 权限。命令与预演见 [后端说明](../../backend/README.md#数据库迁移)。
- 部署前必须审查迁移，并明确备份、超时、失败恢复、回滚、健康检查与新旧版本并存时的兼容性。
- **破坏性 schema 变更按 Expand → Backfill/Switch → Contract 三个有序阶段推进**，不在一次发布里完成。约束的是阶段顺序与下面两道闸门，不是发布次数——Backfill 可能是一次独立运维任务，也可能分多个批次：
  - `DbMigrator` 先于新 API 执行，此刻旧 Pod 仍在运行，因此 **Expand 阶段的迁移必须同时兼容当前生产版本和新版本**（只加不减：新增可空列、新增表、新增索引，不改语义、不删不改名）。
  - 全部目标迁移完成后，再发布新代码并完成 Backfill 或读写切换。
  - **确认旧版本实例全部退出后**，才在之后的受控发布里执行 Contract（删除旧列/旧表/兼容代码）。过渡结构只允许存在于这段窗口，不留长期历史兼容层。
  - 发布前用当前生产版本的数据副本验证「当前生产版本 → 新版本」的升级路径与迁移重跑。
<!--#if (IncludeMultiTenancy)-->
- DedicatedDatabase 首次建库时，先以 `ConnectionStrings__MigrationTarget` 对目标连接运行每个服务的 DbMigrator，再在 Identity 创建租户，见 [后端说明](../../backend/README.md#数据库迁移)。
- DbMigrator 非零退出时发布在此停下，新版本 API 不上线；单个租户失败时其余独立库可能已经迁移且不回滚（边界见后端说明），修好之后重跑，成功后再继续发布。部分库已迁移、旧版本 API 仍在运行的这段时间，靠的正是上面 Expand 阶段"同时兼容新旧版本"的要求。
<!--#endif-->

只有项目已经确定真实环境和运维流程时，才在本目录新增或更新文档。先参考最新同类记录，没有可参考内容时与负责人确认最小必要信息，不创建服务器配置或部署报告模板。
