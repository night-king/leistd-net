using CompanyName.ProjectName.Api.Auth.Authorization;
#if (LocalIdentity && IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Tenants.Dtos;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.TenantConnections.Constants;
#endif
using Leistd.MultiTenancy.AspNetCore.Endpoints;
#endif
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Authorization.AspNetCore.Endpoints;
using Leistd.Authorization.Constants;
#if (IncludeNotifications)
using Leistd.Notifications.AspNetCore.Endpoints;
#endif
using Leistd.OperationRecords.AspNetCore.Attributes;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.AspNetCore.Endpoints;
#endif
using Leistd.Settings.AspNetCore.Endpoints;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>组件自带的 HTTP 端点：本项目只给前缀、授权策略与个别端点的附加元数据。</summary>
/// <remarks>
/// <para>业务动作（发信测试、模拟登录）由控制器提供，与组件端点共用前缀，路由不重叠。</para>
/// <para>个别端点要追加元数据时按组件公开的端点名定位（<see cref="WithMetadataOn{TBuilder}"/>），
/// 不改组件、不复制端点。</para>
/// </remarks>
public static class ComponentEndpoints
{
    /// <summary>映射本项目用到的组件端点。</summary>
    /// <param name="app">应用。</param>
    public static WebApplication MapComponentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGroup("settings")
            .MapSettings(options =>
            {
                options.AccessPolicy = ApiPolicies.CurrentUser;
                options.TenantWritePolicy = PermissionConstant.Settings.Default;
                foreach (var (group, permission) in SettingGroupPermissions.All)
                {
                    options.GroupPolicies[group] = permission;
                }
            })
#if (LocalIdentity)
            // 界面启动就要读：受限会话（要求两步验证而本人未启用）也得能渲染出两步验证设置页
            .WithMetadataOn(SettingEndpoints.GetName, new AllowDuringTwoFactorSetupAttribute())
#endif
            ;

        api.MapGroup("permissions")
            .MapPermissionManagement(options =>
            {
                options.CurrentPolicy = ApiPolicies.CurrentUser;
                // 权限树只为授予而读，能配置角色权限的人才需要它；不另设「查看权限目录」权限
                options.DefinitionsPolicy = PermissionConstant.Roles.ManagePermissions;
                options.GrantPolicies[PermissionGrantProviderNames.Role] = PermissionConstant.Roles.ManagePermissions;
            })
#if (LocalIdentity)
            .WithMetadataOn(PermissionManagementEndpoints.GetCurrentName, new AllowDuringTwoFactorSetupAttribute())
#endif
            // 授权阶段被拒也要留痕；目标标识拼成 "Role/{roleId}"，与成功路径写下的逐字一致，按目标检索才查得全
            .WithMetadataOn(
                PermissionManagementEndpoints.ReplaceGrantsName(PermissionGrantProviderNames.Role),
                new OperationRecordActionAttribute(OperationRecordActions.PermissionGrantsReplaced, "providerKey")
                {
                    TargetIdPrefix = PermissionGrantProviderNames.Role + "/"
                });

#if (IncludeOperationRecords)
        // 导出与查看分开授权：整批带离系统的影响面与在线翻页不是一个量级；导出本身也留痕
        api.MapGroup("operation-records").MapOperationRecords(options =>
        {
            options.ReadPolicy = PermissionConstant.OperationRecords.Default;
            options.ExportPolicy = PermissionConstant.OperationRecords.Export;
            options.ExportAction = OperationRecordActions.OperationRecordsExported;
        });
#endif
#if (IncludeNotifications)

        api.MapGroup("notifications").MapNotifications(options => options.AccessPolicy = ApiPolicies.CurrentUser);
#endif
#if (LocalIdentity && IncludeMultiTenancy)

        // 租户管理是宿主侧能力：权限声明为 Host 侧别，租户上下文内任何主体都无法通过检查
        api.MapGroup("tenants").MapTenantManagement<CreateTenantWithAdminInputDto>(options =>
        {
            options.ReadPolicy = PermissionConstant.Tenants.Default;
            options.CreatePolicy = PermissionConstant.Tenants.Create;
            options.UpdatePolicy = PermissionConstant.Tenants.Update;
            options.DeletePolicy = PermissionConstant.Tenants.Delete;
        });

        api.MapGroup("tenant-connections").MapTenantConnections(options =>
        {
            options.ManagePolicy = PermissionConstant.Tenants.Update;
#if (OpenIddictServer)
            // 机器端点只在签发令牌的形态下映射：不签发令牌时不可能存在机器主体，永远无人可用的内部端点只是攻击面
            options.RuntimeReadPolicy = TenantConnectionPolicies.RuntimeRead;
            options.MigrationReadPolicy = TenantConnectionPolicies.MigrationRead;
#endif
        });
#endif

        return app;
    }

    // 按组件公开的端点名给个别端点追加元数据；Finally 在端点自己的约定之后执行，端点名此时已经在元数据里
    private static TBuilder WithMetadataOn<TBuilder>(this TBuilder builder, string endpointName, params object[] metadata)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<IEndpointNameMetadata>().Any(name => name.EndpointName == endpointName))
            {
                foreach (var item in metadata)
                {
                    endpoint.Metadata.Add(item);
                }
            }
        });
        return builder;
    }
}
