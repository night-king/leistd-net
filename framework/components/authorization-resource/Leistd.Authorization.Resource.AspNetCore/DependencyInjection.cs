using Leistd.Security;
using Leistd.Authorization.Resource.Abstractions;
using Leistd.Authorization.Resource.AspNetCore.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.Authorization.Resource.AspNetCore;

/// <summary>资源实例授权的注册入口：判定接入官方授权管线。</summary>
public static class DependencyInjection
{
    /// <summary>注册资源实例授权：业务入口 <see cref="IResourceAuthorizationService"/>、资源 ACL 处理器与官方授权核心服务。</summary>
    /// <remarks>
    /// <para>领域规则写成官方的 <c>AuthorizationHandler&lt;OperationAuthorizationRequirement, TResource&gt;</c>，
    /// 按官方方式注册为 <c>IAuthorizationHandler</c>。任一处理器 <c>Fail()</c> 即拒绝（ACL 明确拒绝、资源状态不允许），
    /// 否则任一 <c>Succeed()</c> 即允许（规则、ACL 授予、超级管理员），全部无结论时拒绝。</para>
    /// <para>依赖宿主注册 <c>IPermissionSubjectProvider</c>；Web 宿主另需 <c>AddSecurity()</c> 让当前主体来自 HTTP 请求。资源 ACL 存储
    /// （EF 包的 <c>AddResourceAuthorizationEfCore</c>）可选，未注册时只有规则处理器与超级管理员参与判定。</para>
    /// <para>可重复调用：服务与资源 ACL 处理器只注册一次。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddResourceAuthorizationEfCore&lt;AppDbContext&gt;();
    /// builder.Services.AddResourceAuthorization();
    /// builder.Services.AddScoped&lt;IAuthorizationHandler, OrderOwnerHandler&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddResourceAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddResourceAuthorizationCore();
        // 业务入口以当前主体判定：登记主体访问器（Web 宿主再用 AddSecurity() 接入 HTTP 主体来源，顺序无关）
        services.AddAmbientContext();
        services.AddAuthorizationCore();
        services.TryAddScoped<IResourceAuthorizationService, ResourceAuthorizationService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, ResourceGrantAuthorizationHandler>());
        return services;
    }
}
