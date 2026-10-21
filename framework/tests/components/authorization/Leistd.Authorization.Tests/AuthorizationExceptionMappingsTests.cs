using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.ExceptionMappings;
using Leistd.Authorization.Errors;
using Leistd.ExceptionHandling.Options;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leistd.Authorization.Tests;

public sealed class AuthorizationExceptionMappingsTests
{
    [Fact]
    public void Component_registers_only_non_default_statuses_and_host_can_override()
    {
        var options = new GlobalExceptionOptions();
        AuthorizationExceptionMappings.Configure(options);

        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[PermissionErrorCodes.SubjectNotFound]);
        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[PermissionErrorCodes.ConcurrencyConflict]);
        Assert.DoesNotContain(PermissionErrorCodes.UndefinedPermission, options.CodeStatusMappings.Keys);

        options.MapCode(PermissionErrorCodes.SubjectNotFound, StatusCodes.Status403Forbidden);
        Assert.Equal(StatusCodes.Status403Forbidden, options.CodeStatusMappings[PermissionErrorCodes.SubjectNotFound]);

        var hostFirst = new GlobalExceptionOptions();
        hostFirst.MapCode(PermissionErrorCodes.SubjectNotFound, StatusCodes.Status403Forbidden);
        AuthorizationExceptionMappings.Configure(hostFirst);
        Assert.Equal(StatusCodes.Status403Forbidden,
            hostFirst.CodeStatusMappings[PermissionErrorCodes.SubjectNotFound]);
    }

    // 映射由组件注册时自己登记：若要宿主在自己的 ExceptionMappings 里手写一行，
    // 漏了不会有编译或启动错误，只会静默回落成 400。
    [Fact]
    public void Component_registration_applies_the_defaults_without_host_wiring()
    {
        var services = new ServiceCollection();
        services.AddPermissionAuthorizationCore();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[PermissionErrorCodes.SubjectNotFound]);
    }
}
