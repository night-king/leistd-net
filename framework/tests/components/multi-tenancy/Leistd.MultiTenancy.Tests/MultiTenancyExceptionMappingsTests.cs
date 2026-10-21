using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Leistd.ExceptionHandling.Options;
using Leistd.MultiTenancy.ExceptionMappings;
using Leistd.MultiTenancy.Errors;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leistd.MultiTenancy.Tests;

public sealed class MultiTenancyExceptionMappingsTests
{
    [Fact]
    public void Component_registers_its_non_default_http_statuses()
    {
        var options = new GlobalExceptionOptions();
        MultiTenancyExceptionMappings.Configure(options);

        Assert.Equal(StatusCodes.Status403Forbidden, options.CodeStatusMappings[MultiTenancyErrorCodes.NotActive]);
        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[MultiTenancyErrorCodes.NotFound]);
        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[MultiTenancyErrorCodes.ConcurrencyConflict]);
        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[MultiTenancyErrorCodes.DuplicateName]);
        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[MultiTenancyErrorCodes.ConnectionChangeRequiresInactiveTenant]);
        Assert.Equal(StatusCodes.Status409Conflict, options.CodeStatusMappings[MultiTenancyErrorCodes.ConnectionVersionConflict]);
    }

    // 映射由组件注册时自己登记：若要宿主在自己的 ExceptionMappings 里手写一行，
    // 漏了不会有编译或启动错误，只会静默回落成 400。
    [Fact]
    public void Component_registration_applies_the_defaults_without_host_wiring()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancyCore();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[MultiTenancyErrorCodes.NotFound]);
    }
}
