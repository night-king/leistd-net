# 升级后新增的权限

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
- 没有人还能配置权限时，用[管理员恢复](admin-recovery.md)的命令：它会把 Admin 角色缺少的当前可用权限一并补齐。
<!--#endif-->
