using Leistd.BackgroundJobs.Recurring;
using Leistd.EventBus.EventHandlers;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Stores;
using Leistd.Settings.Events;
using Leistd.Settings.Hosting;
using Leistd.Settings.Hosting.Runtime;
using Leistd.Settings.Tests.Core;
using Leistd.TestBase.Doubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.Lock.Memory;
using Leistd.UnitOfWork;

namespace Leistd.Settings.Tests.Hosting;

/// <summary>宿主级设置经配置源覆盖部署配置：消费方读 <c>IOptionsMonitor</c>，默认值是部署基线，整组不合规时沿用上一组。</summary>
public sealed class HostSettingsTests : IDisposable
{
    private readonly FakeSettingStore _store = new();
    private readonly IHost _host;

    public HostSettingsTests()
    {
        _host = Build(attach: true);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>默认值取部署配置并按定义归一：清除设置要回到部署基线，而不是回到一个写死的值。</summary>
    [Fact]
    public void Bound_settings_default_to_the_normalized_deployment_baseline()
    {
        var definitions = _host.Services.GetRequiredService<ISettingDefinitionManager>();

        Assert.Equal("Warning", definitions.GetOrNull("Demo.Level")!.DefaultValue);
        Assert.Equal("true", definitions.GetOrNull("Demo.Enabled")!.DefaultValue);
        Assert.Equal("90", definitions.GetOrNull("Demo.Days")!.DefaultValue);
        Assert.Equal("fallback", definitions.GetOrNull("Demo.Missing")!.DefaultValue);
        // 口令不该作为默认值下发到界面
        Assert.Null(definitions.GetOrNull("Demo.Password")!.DefaultValue);
    }

    [Fact]
    public async Task A_stored_value_overrides_the_bound_configuration_key()
    {
        _store.Host["Demo.Level"] = "Debug";

        await ApplyAsync();

        Assert.Equal("Debug", Options.Level);
        // 基线不受覆盖值影响：否则界面上的默认值会随当前值变化
        Assert.Equal("Warning", _host.Services.GetRequiredService<ISettingDefinitionManager>().GetOrNull("Demo.Level")!.DefaultValue);
    }

    [Fact]
    public async Task Clearing_the_setting_returns_to_the_deployment_configuration()
    {
        _store.Host["Demo.Level"] = "Debug";
        await ApplyAsync();
        _store.Host.Remove("Demo.Level");

        await ApplyAsync();

        Assert.Equal("warning", Options.Level);
    }

    /// <summary>新值让选项校验不过时整组不生效：消费方始终取到合规的值，而不是每次取值都抛异常。</summary>
    [Fact]
    public async Task A_set_that_fails_options_validation_keeps_the_previous_values()
    {
        _store.Host["Demo.Level"] = "Debug";
        await ApplyAsync();

        _store.Host["Demo.Level"] = "Information";
        _store.Host["Demo.Days"] = "10";
        await Assert.ThrowsAsync<InvalidOperationException>(ApplyAsync);

        Assert.Equal(("Debug", 90), (Options.Level, Options.Days));
    }

    /// <summary>
    /// 周期刷新没把值推进去时要让调度器记失败，而不是记成功；被拒的一组未改正前每轮都失败，
    /// 否则只有第一轮留下痕迹，之后看起来一切正常。
    /// </summary>
    [Fact]
    public async Task Refresh_fails_while_the_stored_set_is_rejected()
    {
        _store.Host["Demo.Days"] = "10";

        var first = await Assert.ThrowsAsync<InvalidOperationException>(RefreshAsync);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(RefreshAsync);

        Assert.Contains("keeping the previous values", first.Message);
        Assert.Contains("Days must be at least 30.", second.Message);
        Assert.Equal(90, Options.Days);
    }

    [Fact]
    public async Task Refresh_fails_when_the_host_scope_is_unreachable()
    {
        _store.CanAccessHostScope = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(RefreshAsync);

        Assert.Contains("host scope is not reachable", error.Message);
    }

    // 写入已提交，应用失败不能变成写入方看到的异常；下一轮周期刷新再试
    [Fact]
    public async Task A_committed_host_write_that_fails_to_apply_does_not_throw()
    {
        _store.Host["Demo.Days"] = "10";

        await HandleAsync(new SettingChangedEvent("Demo.Days", SettingScopes.Host, null));

        Assert.Equal(90, Options.Days);
    }

    [Fact]
    public async Task A_committed_host_write_is_applied_to_this_process()
    {
        _store.Host["Demo.Level"] = "Debug";

        await HandleAsync(new SettingChangedEvent("Demo.Level", SettingScopes.Host, null));

        Assert.Equal("Debug", Options.Level);
    }

    [Fact]
    public async Task Writes_to_unbound_or_non_host_settings_are_ignored()
    {
        _store.Host["Demo.Level"] = "Debug";

        await HandleAsync(new SettingChangedEvent("Demo.Unbound", SettingScopes.Host, null));
        await HandleAsync(new SettingChangedEvent("Demo.Level", SettingScopes.Tenant, null));

        Assert.Equal("warning", Options.Level);
    }

    // 租户上下文读不到宿主行，"读不到"不能当成"都没设"推进配置
    [Fact]
    public async Task Nothing_is_applied_when_the_host_scope_is_unreachable()
    {
        _store.Host["Demo.Level"] = "Debug";
        await ApplyAsync();
        _store.CanAccessHostScope = false;
        _store.Host.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(ApplyAsync);

        Assert.Equal("Debug", Options.Level);
    }

    // 最早的请求与日志要用设置里的值：首次应用必须在宿主开始接收请求之前完成，而不是在后台慢慢跑
    [Fact]
    public async Task Stored_values_are_applied_before_the_host_has_started()
    {
        _store.Host["Demo.Level"] = "Debug";

        await _host.StartAsync();
        try
        {
            Assert.Equal("Debug", Options.Level);
        }
        finally
        {
            await _host.StopAsync();
        }
    }

    [Fact]
    public void Refresh_runs_on_every_instance()
    {
        var job = Assert.Single(_host.Services.GetServices<RecurringJobDefinition>());

        Assert.Equal((HostSettingRefreshJob.Name, RecurringJobScope.EveryInstance), (job.Name, job.Scope));
    }

    [Fact]
    public async Task Starting_without_attaching_the_configuration_source_fails()
    {
        using var host = Build(attach: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("UseHostSettings()", error.Message);
    }

    [Fact]
    public void Binding_a_setting_that_is_not_process_wide_fails()
    {
        using var host = Build(attach: true, extraBinding: "Demo.Layered");

        var error = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<ISettingDefinitionManager>().GetAll());

        Assert.Contains("not host-scoped", error.Message);
    }

    private DemoOptions Options => _host.Services.GetRequiredService<IOptionsMonitor<DemoOptions>>().CurrentValue;

    private async Task ApplyAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HostSettingApplier>().ApplyAsync();
    }

    private async Task RefreshAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<HostSettingRefreshJob>(scope.ServiceProvider);
        await job.ExecuteAsync(new RecurringJobContext(HostSettingRefreshJob.Name, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private async Task HandleAsync(SettingChangedEvent @event)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventHandler<SettingChangedEvent>>().HandleAsync(@event);
    }

    private IHost Build(bool attach, string? extraBinding = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:Level"] = "warning",
            ["Demo:Enabled"] = "True",
            ["Demo:Days"] = "90",
            ["Demo:Password"] = "from-config",
        });

        builder.Services
            .AddSettingsCore()
            .AddSingleton<ISettingStore>(_store)
            .AddMemoryLocalLock()
            .AddSingleton<IUnitOfWorkManager>(new RecordingUnitOfWorkManager())
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<ISettingDefinitionProvider, Definitions>()
            .AddHostSettings(bindings =>
            {
                bindings
                    .BindOption<DemoOptions>("Demo.Level", "Demo", nameof(DemoOptions.Level))
                    .BindOption<DemoOptions>("Demo.Enabled", "Demo", nameof(DemoOptions.Enabled))
                    .BindOption<DemoOptions>("Demo.Days", "Demo", nameof(DemoOptions.Days))
                    .BindOption<DemoOptions>("Demo.Password", "Demo", nameof(DemoOptions.Password))
                    .Bind("Demo.Missing", ["Demo:Missing"], fallback: "fallback");
                if (extraBinding is not null)
                {
                    bindings.Bind(extraBinding, ["Demo:Layered"]);
                }
            });
        builder.Services.AddOptions<DemoOptions>()
            .BindConfiguration("Demo")
            .Validate(options => options.Days >= 30, "Days must be at least 30.");

        var host = builder.Build();
        return attach ? host.UseHostSettings() : host;
    }

    private sealed class DemoOptions
    {
        public string Level { get; set; } = "Information";

        public bool Enabled { get; set; }

        public int Days { get; set; } = 365;

        public string? Password { get; set; }
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Demo.Level", scopes: SettingScopes.Host).WithAllowedValues("Debug", "Information", "Warning");
            context.Add("Demo.Enabled", scopes: SettingScopes.Host).AsBoolean();
            context.Add("Demo.Days", scopes: SettingScopes.Host).AsInteger(30, 3650);
            context.Add("Demo.Password", scopes: SettingScopes.Host).IsEncrypted = true;
            context.Add("Demo.Missing", scopes: SettingScopes.Host);
            context.Add("Demo.Unbound", scopes: SettingScopes.Host);
            context.Add("Demo.Layered", scopes: SettingScopes.Tenant);
        }
    }
}
