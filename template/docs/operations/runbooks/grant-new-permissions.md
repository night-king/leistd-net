# 升级后新增的权限

`DbMigrator --apply` 每次施加迁移之后，给每个 Admin 角色补上所在侧别当前可用、却还没有的权限（如新版本的仪表盘 `App.Dashboard`）。按[部署说明](../../deploy/README.md)先迁移后发布，新权限随发布到位，不需要手工授予。命令与语义见[后端说明](../../../backend/README.md#数据库迁移)。
<!--#if (IncludeMultiTenancy)-->

宿主 Admin 补宿主侧，各租户的 Admin 补租户侧，共享库与独立库都覆盖。
<!--#endif-->

- **Admin 只增不减**：从 Admin 撤掉的权限下次迁移会补回。要给某些人收窄权限，建自定义角色并把他们移出 Admin。
- **其他角色不补**：自定义角色要不要拿到新权限由管理员决定，在角色权限里勾选。
- 发布前用不带 `--apply` 的预演查看将补授的权限；有待执行的迁移时，预演不列补授，施加后的输出会列出实际补授的内容。
- 迁移失败时不补齐，进程以非零码退出；修好后重跑 `--apply` 即可补上。
<!--#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))-->
- 没有人还能配置权限时（管理员被停用、删除或移出 Admin），用[管理员恢复](admin-recovery.md)的命令。
<!--#endif-->
