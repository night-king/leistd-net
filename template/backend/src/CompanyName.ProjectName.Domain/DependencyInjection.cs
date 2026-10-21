using CompanyName.ProjectName.Domain.Users.DomainServices;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.DomainServices;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace CompanyName.ProjectName.Domain;

/// <summary>Domain 层依赖注入配置。</summary>
public static class DependencyInjection
{
    /// <summary>注册 Domain 层服务。</summary>
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        // 用户管理领域服务。TryAdd：组合根拆分后重复调用是常态，
        // 重复注册会让同一实现出现多条，按 IEnumerable 解析时重复执行
        services.TryAddTransient<UserDomainService>();
        services.TryAddTransient<RoleDomainService>();
#if (LocalIdentity)
        services.TryAddTransient<TwoFactorDomainService>();
        services.TryAddTransient<UserSessionDomainService>();
#endif

#if (ExternalLogin)
        // 外部认证领域服务
        services.TryAddTransient<ExternalAuthDomainService>();
#endif

        return services;
    }
}
