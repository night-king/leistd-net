using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.Resource.ExceptionMappings;
using Leistd.Authorization.Resource.Errors;
using Leistd.ExceptionHandling.Options;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leistd.Authorization.Resource.Tests;

public sealed class ResourceAuthorizationExceptionMappingsTests
{
    [Fact]
    public void Resource_defaults_are_transport_scoped_and_host_overridable()
    {
        var options = new GlobalExceptionOptions();
        ResourceAuthorizationExceptionMappings.Configure(options);

        Assert.Equal(StatusCodes.Status409Conflict,
            options.CodeStatusMappings[ResourceAuthorizationErrorCodes.ConcurrencyConflict]);
        Assert.DoesNotContain(ResourceAuthorizationErrorCodes.InvalidGrant, options.CodeStatusMappings.Keys);

        options.MapCode(ResourceAuthorizationErrorCodes.ConcurrencyConflict, StatusCodes.Status412PreconditionFailed);
        Assert.Equal(StatusCodes.Status412PreconditionFailed,
            options.CodeStatusMappings[ResourceAuthorizationErrorCodes.ConcurrencyConflict]);

        var hostFirst = new GlobalExceptionOptions();
        hostFirst.MapCode(ResourceAuthorizationErrorCodes.ConcurrencyConflict, StatusCodes.Status412PreconditionFailed);
        ResourceAuthorizationExceptionMappings.Configure(hostFirst);
        Assert.Equal(StatusCodes.Status412PreconditionFailed,
            hostFirst.CodeStatusMappings[ResourceAuthorizationErrorCodes.ConcurrencyConflict]);
    }

    // 映射由组件注册时自己登记：若要宿主在自己的 ExceptionMappings 里手写一行，
    // 漏了不会有编译或启动错误，只会静默回落成 400。
    [Fact]
    public void Component_registration_applies_the_defaults_without_host_wiring()
    {
        var services = new ServiceCollection();
        services.AddResourceAuthorizationCore();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[ResourceAuthorizationErrorCodes.ConcurrencyConflict]);
    }
}
