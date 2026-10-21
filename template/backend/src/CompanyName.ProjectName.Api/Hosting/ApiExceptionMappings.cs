using CompanyName.ProjectName.Api.Hosting.ExceptionMappings;
using Leistd.ExceptionHandling.Options;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>登记本项目业务模块的非默认 HTTP 状态。</summary>
/// <remarks>组件默认映射由其注册入口登记；宿主可用 <see cref="GlobalExceptionOptions.MapCode"/> 覆盖。</remarks>
internal static class ApiExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
#if (LocalIdentity)
        AuthExceptionMappings.Configure(options);
#if (IncludeMultiTenancy)
        TenantExceptionMappings.Configure(options);
#endif
#endif
#if (OpenIddictServer)
        OpenApplicationExceptionMappings.Configure(options);
#endif
        UserExceptionMappings.Configure(options);
        RoleExceptionMappings.Configure(options);
        AppSettingExceptionMappings.Configure(options);
    }

    internal static void Map(GlobalExceptionOptions options, int statusCode, params string[] codes)
    {
        foreach (var code in codes)
            options.MapCode(code, statusCode);
    }
}
