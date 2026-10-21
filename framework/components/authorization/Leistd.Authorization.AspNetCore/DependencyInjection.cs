using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.AspNetCore.Permissions;
using Leistd.Authorization.Checking;
using Leistd.Authorization.Definitions;

namespace Leistd.Authorization.AspNetCore;

/// <summary>权限授权接入 ASP.NET Core 策略管道的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册基于权限定义的动态授权策略：使 <c>[Authorize(Policy = "权限名")]</c> 生效。</summary>
    /// <remarks>
    /// 依赖调用方已注册 <see cref="IPermissionChecker"/> 与
    /// <see cref="IPermissionDefinitionManager"/>，并已调用 <c>AddAuthorization()</c>。
    /// 可重复调用：策略提供器与授权处理器只登记一次。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddPermissionAuthorization();
    ///
    /// // 策略名即权限名；| 表示"任一满足"
    /// [Authorize(Policy = "Orders.Update|Orders.Manage")]
    /// public Task&lt;IActionResult&gt; UpdateAsync(long id) =&gt; /* ... */;
    /// </code>
    /// </example>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddPermissionAuthorizationCore();
        // 有意替换官方 DefaultAuthorizationPolicyProvider：权限名策略须动态生成；显式注册的策略仍由内部的官方实现提供。
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAuthorizationHandler, PermissionAuthorizationHandler>());
        return services;
    }
}

