# 部署说明

项目根 `Dockerfile` 和 `deploy/` 是当前容器部署配置的事实来源。部署前先核对实际镜像、环境变量、数据库迁移、健康检查和回滚能力，不使用文档中的假定值替代配置。

## 文档索引

| 主题 | 文档 |
| --- | --- |
| 配置键的取值来源、部署环境的必填项、生产边界 | [配置参考](configuration.md) |
<!--#if (OpenIddictServer)-->
| 令牌签名与加密证书的轮换和泄露处置 | [令牌证书轮换](../operations/runbooks/token-certificate-rotation.md) |
<!--#endif-->
| 换 PostgreSQL、Redis 主版本 | [大版本升级](../operations/runbooks/postgres-redis-major-upgrade.md) |
| 发布后把新权限授给已有角色 | [升级后新增的权限](../operations/runbooks/grant-new-permissions.md) |
<!--#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))-->
| 管理员把自己锁在门外 | [管理员恢复](../operations/runbooks/admin-recovery.md) |
<!--#endif-->
<!--#if (LocalIdentity)-->
| 验证器与恢复码都丢失、在线无人能重置 | [两步验证的部署级重置](../operations/runbooks/two-factor-reset.md) |
<!--#endif-->

上表列出模板自带的运维手册；项目自己的运维操作同样写在 `docs/operations/runbooks/`，一个操作一份。

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
  实时推送随 Redis 一起启用 SignalR 官方背板（`backend/src/CompanyName.ProjectName.Api/Hosting/SignalRBackplaneExtensions.cs`），不需要另配开关：
  - **会话粘滞**：协商与后续请求必须落在同一个 API 副本。亲和要配在实际选副本的那一层：自建 Nginx 在 upstream 配 `ip_hash` 或 cookie 亲和；经 Ingress 时按所用控制器配置 cookie 亲和并覆盖 Hub 路径。Service 的 `sessionAffinity: ClientIP` 只在流量确实经过该 Service 且源地址稳定时有效，不能代替 Ingress 上的亲和。
  - **前缀**：背板通道前缀取锁前缀 `Leistd:Lock:Redis:KeyPrefix`（默认按项目名）。Redis 的发布订阅不按数据库编号隔离，共用一个 Redis 的不同环境要覆盖成不同的值，同一环境的所有副本与 Worker 用同一个值。
  - **Redis 不可用**：期间的推送丢失、不补发，恢复后自动重连；连接串保留 `abortConnect=false`。推送只用于提示刷新，客户端重连、回到前台与下次请求会收敛到最新状态。
  - 另起的 Worker 进程要推送时，按框架实时通信与通知文档的“非 Hub 进程发布”接线：同一 Redis、同一前缀，不映射 Hub。
<!--#endif-->
- **云平台（容器服务、应用服务）**：配置写应用设置，机密放托管密钥库并以托管身份读取（如 Key Vault 引用）。这类接入与平台绑定，确定平台后再加，不预置在模板里。

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
OIDC 端点（授权码流程、令牌）在生产配置下拒绝 HTTP 请求，验证开放应用或资源服务联调须在前面加一层终结 TLS 的反向代理，并按[配置参考](configuration.md#部署环境的必填项)配置转发头。
<!--#endif-->
只验证镜像能启动、迁移与健康端点时不需要 HTTPS。

命令和文件存在性应以当前项目为准。

## 环境晋级

各环境部署同一份镜像，按 digest 晋级，只换配置与机密。部署记录写 API 和 DbMigrator 的 digest，不用 `latest` 或可被重新推送的标签充当发布身份；模板 Compose 中的 `:latest` 是示例值，实际发布由项目流水线替换。

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

## 确认已部署版本

<!--#if (Ci == "github")-->
默认分支的 push 通过 `verify`（含适用的依赖审计）后，`.github/workflows/ci.yml` 的 `publish-images` 作业构建并推送 `ghcr.io/<owner>/<repo>-api` 与 `ghcr.io/<owner>/<repo>-migrator`（名称取小写），标签为提交 SHA，作业摘要列出两个镜像的 digest。PR、merge queue、定时审计与手动运行不发布。

前提：作业用 `GITHUB_TOKEN` 登录 GHCR，工作流已为该作业声明 `packages: write`；同名包已由其他仓库创建时，要在包设置里给本仓库写权限。
<!--#elseif (Ci == "gitlab")-->
默认分支的 push 流水线在 `verify` 阶段的作业（含适用的依赖审计）全部成功后，由 `publish` 阶段的 `publish-images` 作业构建并推送 `$CI_REGISTRY_IMAGE/api` 与 `$CI_REGISTRY_IMAGE/migrator`，标签为提交 SHA；两个镜像的 digest 写在作业日志里，并作为 dotenv 报告（`API_IMAGE_DIGEST`、`MIGRATOR_IMAGE_DIGEST`）留给后续作业。合并请求、定时与手动运行不发布。

前提：项目已启用 Container Registry，作业用内置的 `CI_REGISTRY_*` 变量登录；Runner 要求与 `verify` 相同（见 `.gitlab-ci.yml` 文件头）。
<!--#endif-->
<!--#if (Ci == "github" || Ci == "gitlab")-->
流水线传入 `APP_REVISION`（提交 SHA）；项目根有 `VERSION` 文件时以其内容作 `APP_VERSION`，否则用 Dockerfile 的默认值。本地手工构建时自行传入：
<!--#else-->
镜像由项目流水线构建并传入发布身份：
<!--#endif-->

```bash
docker build --build-arg APP_VERSION=1.4.0 --build-arg APP_REVISION="$(git rev-parse HEAD)" --target api -t <仓库>/companyname-projectname-api:1.4.0 .
```

部署后按两处核对，不按镜像标签推断：

- 运行中的 API 返回自己的版本：`GET /api/v1/service-info` 的 `version`（程序集版本，`APP_VERSION` 补齐为四段）。
- 镜像记录源码提交：`docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' <容器或镜像>`。为空说明镜像不是由流水线按上面的方式构建的。

只有项目已经确定真实环境和运维流程时，才在本目录新增或更新文档。先参考最新同类记录，没有可参考内容时与负责人确认最小必要信息，不创建服务器配置或部署报告模板。
