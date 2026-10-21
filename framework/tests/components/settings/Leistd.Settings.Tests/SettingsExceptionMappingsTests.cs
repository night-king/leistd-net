using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Leistd.ExceptionHandling.Options;
using Leistd.Settings.ExceptionMappings;
using Leistd.Settings.Errors;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leistd.Settings.Tests;

public sealed class SettingsExceptionMappingsTests
{
    [Fact]
    public void Component_registers_its_non_default_http_statuses()
    {
        var options = new GlobalExceptionOptions();
        SettingsExceptionMappings.Configure(options);

        Assert.Equal(StatusCodes.Status403Forbidden, options.CodeStatusMappings[SettingErrorCodes.HostOnly]);
        Assert.Equal(StatusCodes.Status403Forbidden, options.CodeStatusMappings[SettingErrorCodes.IdentityCannotOperate]);
        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[SettingErrorCodes.NotAvailable]);
        Assert.DoesNotContain(SettingErrorCodes.ValueOutOfRange, options.CodeStatusMappings.Keys);
    }

    // 映射由组件注册时自己登记：若要宿主在自己的 ExceptionMappings 里手写一行，
    // 漏了不会有编译或启动错误，只会静默回落成 400。
    [Fact]
    public void Component_registration_applies_the_defaults_without_host_wiring()
    {
        var services = new ServiceCollection();
        services.AddSettingsCore();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

        Assert.Equal(StatusCodes.Status404NotFound, options.CodeStatusMappings[SettingErrorCodes.NotAvailable]);
    }
}
