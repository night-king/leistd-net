using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Leistd.AspNetCore.SignalR.Services;
using Leistd.Security.Claims;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Leistd.AspNetCore.SignalR.Tests;

public class ClaimsSignalRUserIdProviderTests
{
    [Fact]
    public void Uses_configured_claim_order_and_skips_empty_values()
    {
        var provider = new ClaimsSignalRUserIdProvider(MsOptions.Create(new ClaimTypeOptions
        {
            UserIds = ["tenant_user", "sub"]
        }));

        var connection = CreateConnection(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_user", ""),
            new Claim("sub", "subject-123")
        ], "Test")));

        Assert.Equal("subject-123", provider.GetUserId(connection));
    }

    [Fact]
    public void Falls_back_to_sub_then_name_identifier_by_default()
    {
        var provider = new ClaimsSignalRUserIdProvider(MsOptions.Create(new ClaimTypeOptions()));

        var connection = CreateConnection(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "nameid-9")], "Test")));

        Assert.Equal("nameid-9", provider.GetUserId(connection));
    }

    // AddSignalR() 内部也 TryAdd 一个 DefaultUserIdProvider（只认 ClaimTypes.NameIdentifier）。
    // 无论它先注册还是后注册，最终都必须被换掉，否则 ClaimTypeOptions.UserIds 永不生效。
    [Fact]
    public void The_framework_provider_wins_over_the_SignalR_default()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalRAmbientContext();

        Assert.IsType<ClaimsSignalRUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    // 但不能连宿主自己的实现一起盖掉：IUserIdProvider 是 SignalR 的公开扩展点。
    [Fact]
    public void A_host_provider_registered_first_is_not_overridden()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IUserIdProvider, HostUserIdProvider>();

        services.AddSignalRAmbientContext();

        Assert.IsType<HostUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    [Fact]
    public void A_host_provider_registered_afterwards_wins()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalRAmbientContext();

        services.AddSingleton<IUserIdProvider, HostUserIdProvider>();

        Assert.IsType<HostUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    // 宿主常先自己调 AddSignalR(o => ...) 配心跳，那时 DefaultUserIdProvider 已经占位。
    // 这是模板的真实顺序，也是这条规则最容易被踩空的地方。
    [Fact]
    public void The_framework_provider_wins_even_when_the_host_calls_AddSignalR_first()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalR(options => options.EnableDetailedErrors = true);

        services.AddSignalRAmbientContext();

        Assert.IsType<ClaimsSignalRUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    // 两个 Hub 组件都会调基座的注册入口，夹在中间的宿主实现不能被第二次调用盖掉。
    [Fact]
    public void A_host_provider_survives_a_second_hub_component_registration()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalRAmbientContext();
        services.AddSingleton<IUserIdProvider, HostUserIdProvider>();

        services.AddSignalRAmbientContext();

        Assert.IsType<HostUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    // keyed 注册不参与非 keyed 的解析，因此不能让它挡住对默认实现的替换。
    [Fact]
    public void A_keyed_provider_does_not_block_replacing_the_default()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalR();
        services.AddKeyedSingleton<IUserIdProvider, HostUserIdProvider>("tenant-a");

        services.AddSignalRAmbientContext();

        Assert.IsType<ClaimsSignalRUserIdProvider>(
            services.BuildServiceProvider().GetRequiredService<IUserIdProvider>());
    }

    private sealed class HostUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) => "host-supplied";
    }

    private static HubConnectionContext CreateConnection(ClaimsPrincipal user)
    {
        var connection = new DefaultConnectionContext
        {
            ConnectionId = "claim-connection",
            User = user
        };

        return new HubConnectionContext(
            connection,
            new HubConnectionContextOptions(),
            NullLoggerFactory.Instance);
    }
}
