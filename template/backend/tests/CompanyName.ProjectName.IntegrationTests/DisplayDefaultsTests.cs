using CompanyName.ProjectName.Application.Settings.Options;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy && LocalIdentity)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
#if (IncludeLocalization)
using Leistd.Settings.Resolution;
#endif
using Leistd.UnitOfWork;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 界面语言与时区：个人 → 组织 → 部署基线 → 回落（服务端时区为 UTC）。部署基线来自配置节 <c>DisplayDefaults</c>，
/// 空为跟随设备，非空却不合法时启动即失败——否则默认值能生效、管理员却存不进同一个值。
/// </summary>
public sealed class DisplayDefaultsTests
{
    [Fact]
    public async Task The_time_zone_follows_the_user_then_the_organization_then_the_deployment_baseline()
    {
        using var factory = new ProjectWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("DisplayDefaults:TimeZone", "Asia/Tokyo"));
        var name = SettingConstant.Display.TimeZone;
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();

        Assert.Equal("Asia/Tokyo", await ResolveTimeZoneAsync(host, alice));
        try
        {
            await SetAsync(host, name, "Europe/Paris", SettingScopes.Tenant);
            await SetAsync(host, name, "America/New_York", SettingScopes.User, alice);

            // 按目标用户解析：同一租户环境里两个人各得各的
            Assert.Equal("America/New_York", await ResolveTimeZoneAsync(host, alice));
            Assert.Equal("Europe/Paris", await ResolveTimeZoneAsync(host, bob));

            await SetAsync(host, name, null, SettingScopes.User, alice);
            Assert.Equal("Europe/Paris", await ResolveTimeZoneAsync(host, alice));
        }
        finally
        {
            await SetAsync(host, name, null, SettingScopes.User, alice);
            await SetAsync(host, name, null, SettingScopes.Tenant);
        }

        Assert.Equal("Asia/Tokyo", await ResolveTimeZoneAsync(host, alice));
    }

    // 零配置（以及只写了空白）照常启动，没有任何一层有值时服务端回落 UTC
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Without_a_baseline_the_server_falls_back_to_UTC(string? configured)
    {
        using var factory = new ProjectWebApplicationFactory();
        await using var host = configured is null
            ? factory.WithWebHostBuilder(_ => { })
            : factory.WithWebHostBuilder(builder => builder.UseSetting("DisplayDefaults:TimeZone", configured));

        Assert.Equal(TimeZoneInfo.Utc.Id, await ResolveTimeZoneAsync(host, Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData("DisplayDefaults:TimeZone", "Mars/Olympus")]
    [InlineData("DisplayDefaults:TimeZone", "China Standard Time")]
#if (IncludeLocalization)
    [InlineData("DisplayDefaults:Language", "fr")]
#endif
    public void An_invalid_baseline_fails_at_startup(string key, string value)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));

        var exception = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        var failure = Assert.IsType<OptionsValidationException>(exception);
        Assert.Equal(typeof(DisplayDefaultsOptions), failure.OptionsType);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }
#if (IncludeLocalization)

    [Fact]
    public async Task The_language_baseline_is_the_default_until_the_organization_sets_one()
    {
        using var factory = new ProjectWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("DisplayDefaults:Language", "zh-CN"));
        var name = SettingConstant.Display.Language;
        var user = Guid.CreateVersion7();

        Assert.Equal("zh-CN", await ReadForUserAsync(host, name, user));
        try
        {
            await SetAsync(host, name, "en", SettingScopes.Tenant);
            Assert.Equal("en", await ReadForUserAsync(host, name, user));
        }
        finally
        {
            await SetAsync(host, name, null, SettingScopes.Tenant);
        }
    }
#endif
#if (IncludeMultiTenancy)

    // 宿主在系统设置里写的是宿主这个组织自己的值，不是全部署的默认：其他租户读不到，全部署默认走部署基线
    [Fact]
    public async Task The_host_organization_value_does_not_reach_tenants()
    {
        using var factory = new ProjectWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(_ => { });
        var name = SettingConstant.Display.TimeZone;
        var user = Guid.CreateVersion7();

#if (LocalIdentity)
        // 本地身份的租户要在注册表里存在，连接串才解析得出来；不走开通全流程，共用宿主库
        Guid tenantId;
        await using (var serviceScope = host.Services.CreateAsyncScope())
        {
            var tenants = serviceScope.ServiceProvider.GetRequiredService<ITenantManager>();
            tenantId = (await tenants.CreateAsync($"disp{Guid.CreateVersion7():N}"[..16], null, isActive: true)).Id;
        }
#else
        // 资源服务的租户来自令牌声明，没有注册表
        var tenantId = Guid.CreateVersion7();
#endif

        try
        {
            await SetAsync(host, name, "Europe/Paris", SettingScopes.Tenant);

            Assert.Equal("Europe/Paris", await ResolveTimeZoneAsync(host, user));
            Assert.Equal(TimeZoneInfo.Utc.Id, await ResolveTimeZoneAsync(host, user, tenantId));
        }
        finally
        {
            await SetAsync(host, name, null, SettingScopes.Tenant);
        }
    }
#endif

    private static async Task SetAsync(
        WebApplicationFactory<Program> host,
        string name,
        string? value,
        SettingScopes scope,
        Guid? userId = null)
    {
        await using var serviceScope = host.Services.CreateAsyncScope();
        var services = serviceScope.ServiceProvider;
        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        await services.GetRequiredService<ISettingManager>().SetAsync(name, value, scope, userId?.ToString());
        await unitOfWork.CompleteAsync();
    }

    private static async Task<string> ResolveTimeZoneAsync(WebApplicationFactory<Program> host, Guid userId, Guid? tenantId = null)
    {
        await using var serviceScope = host.Services.CreateAsyncScope();
        var services = serviceScope.ServiceProvider;
        // 单租户形态可能不注册租户上下文，那时只有宿主这一个组织
        using var tenant = services.GetService<ICurrentTenant>()?.Change(tenantId);
        var timeZone = await services.GetRequiredService<IUserTimeZoneProvider>().GetForUserAsync(userId);
        return timeZone.Id;
    }
#if (IncludeLocalization)

    private static async Task<string?> ReadForUserAsync(WebApplicationFactory<Program> host, string name, Guid userId)
    {
        await using var serviceScope = host.Services.CreateAsyncScope();
        return await serviceScope.ServiceProvider.GetRequiredService<ISettingProvider>()
            .GetOrNullForUserAsync(name, userId.ToString());
    }
#endif
}
