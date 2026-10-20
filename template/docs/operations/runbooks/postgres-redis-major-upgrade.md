# PostgreSQL 与 Redis 大版本升级

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
