using Leistd.ExceptionHandling;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Dtos;
using Leistd.TestBase.Doubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.Lock.Memory;
using Leistd.UnitOfWork;

namespace Leistd.Settings.Tests.Core;

/// <summary>设置页用例：分层读取原始覆盖值、只处理对客户端开放的设置、层级越权就地拒绝。</summary>
public class SettingManagementTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Each_level_is_reported_separately_instead_of_the_resolved_value()
    {
        var store = new FakeSettingStore();
        store.Tenant["Display.TimeZone"] = "Asia/Shanghai";
        store.User["Display.TimeZone"] = "Asia/Tokyo";

        var timeZone = Single(await Build(store).GetAsync(), "Display.TimeZone");

        Assert.Equal(("Asia/Tokyo", "Asia/Shanghai", "UTC"), (timeZone.UserValue, timeZone.TenantValue, timeZone.DefaultValue));
        Assert.True(timeZone.AllowsTenantScope && timeZone.AllowsUserScope && !timeZone.AllowsHostScope);
    }

    [Fact]
    public async Task Settings_not_visible_to_clients_are_neither_listed_nor_writable()
    {
        var service = Build(new FakeSettingStore());

        Assert.DoesNotContain(await service.GetAsync(), s => s.Name == "Export.MaxRowsPerFile");
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.SetForCurrentTenantAsync(new SetSettingInputDto("Export.MaxRowsPerFile", "10")));
        Assert.Equal(SettingErrorCodes.NotAvailable, error.Code);
    }

    [Fact]
    public async Task The_host_reads_process_wide_settings_from_the_host_row()
    {
        var store = new FakeSettingStore();
        store.Host["Logging.MinimumLevel"] = "Debug";

        var level = Single(await Build(store).GetAsync(), "Logging.MinimumLevel");

        Assert.Equal("Debug", level.TenantValue);
        Assert.True(level.AllowsHostScope && !level.AllowsTenantScope && !level.AllowsUserScope);
        Assert.Equal(["Debug", "Information"], level.AllowedValues!);
    }

    // 下发一个只能看、改了还会被拒的项，比看不到更让人困惑
    [Fact]
    public async Task A_tenant_reader_does_not_see_process_wide_settings_and_cannot_write_them()
    {
        var service = Build(new FakeSettingStore { CanAccessHostScope = false });

        Assert.DoesNotContain(await service.GetAsync(), s => s.Name == "Logging.MinimumLevel");
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.SetForCurrentTenantAsync(new SetSettingInputDto("Logging.MinimumLevel", "Debug")));
        Assert.Equal(SettingErrorCodes.HostOnly, error.Code);
    }

    [Fact]
    public async Task A_process_wide_setting_is_written_to_the_host_scope()
    {
        var store = new FakeSettingStore();

        await Build(store).SetForCurrentTenantAsync(new SetSettingInputDto("Logging.MinimumLevel", "Debug"));

        Assert.Equal(SettingScopes.Host, Assert.Single(store.Writes).Scope);
    }

    [Fact]
    public async Task A_personal_write_needs_a_user_identity()
    {
        var service = Build(new FakeSettingStore(), new FakeCurrentUser());

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.SetForCurrentUserAsync(new SetSettingInputDto("Display.TimeZone", "Asia/Tokyo")));

        Assert.Equal(SettingErrorCodes.IdentityCannotOperate, error.Code);
    }

    [Fact]
    public async Task A_personal_write_lands_on_the_current_user()
    {
        var store = new FakeSettingStore();

        await Build(store).SetForCurrentUserAsync(new SetSettingInputDto("Display.TimeZone", "Asia/Tokyo"));

        Assert.Equal((SettingScopes.User, UserId.ToString()), (store.Writes[0].Scope, store.Writes[0].UserId));
    }

    [Fact]
    public async Task Secret_values_are_never_returned_only_whether_one_is_set()
    {
        var store = new FakeSettingStore();
        store.Host["Email.SmtpPassword"] = "cipher";

        var password = Single(await Build(store).GetAsync(), "Email.SmtpPassword");

        Assert.Equal((null, null, null), (password.UserValue, password.TenantValue, password.DefaultValue));
        Assert.True(password.IsSecret && password.HasSecretValue);
    }

    [Fact]
    public async Task Value_metadata_is_reported_for_controls()
    {
        var settings = await Build(new FakeSettingStore()).GetAsync();

        Assert.True(Single(settings, "Security.RequireTwoFactor").IsBoolean);
        var duration = Single(settings, "Security.LockoutDurationMinutes");
        Assert.Equal((1, 1440), (duration.Minimum, duration.Maximum));
    }

    [Fact]
    public async Task Names_are_translated_by_convention_and_fall_back_to_the_definition()
    {
        var service = Build(new FakeSettingStore(), localized: new Dictionary<string, string>
        {
            ["Setting:Display.TimeZone"] = "时区",
            ["SettingGroup:Display"] = "显示",
        });

        var settings = await service.GetAsync();

        var timeZone = Single(settings, "Display.TimeZone");
        Assert.Equal(("时区", "Display", "显示"), (timeZone.DisplayName, timeZone.Group, timeZone.GroupDisplayName));
        // 没有词条：用定义里的文案；没写分组：归入默认分组
        var twoFactor = Single(settings, "Security.RequireTwoFactor");
        Assert.Equal(("Require 2FA", "Other", "Other"), (twoFactor.DisplayName, twoFactor.Group, twoFactor.GroupDisplayName));
    }

    // 提示显示在字段旁、也会进 toast：给用户看的必须是界面上的字段名，而不是技术键
    [Fact]
    public async Task Validation_errors_name_the_setting_as_the_user_sees_it()
    {
        var service = Build(new FakeSettingStore(), localized: new Dictionary<string, string>
        {
            ["Setting:Security.LockoutDurationMinutes"] = "锁定时长（分钟）",
        });

        var error = await Assert.ThrowsAsync<BusinessException>(() => service.SetForCurrentTenantAsync(
            new SetSettingInputDto("Security.LockoutDurationMinutes", "0")));

        Assert.Equal(SettingErrorCodes.ValueOutOfRange, error.Code);
        Assert.Equal("锁定时长（分钟）", error.LocalizationData["Name"]);
    }

    // 未启用本地化时，兜底文案与占位参数都用定义上的显示名
    [Fact]
    public async Task Validation_errors_fall_back_to_the_definition_display_name()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => Build(new FakeSettingStore())
            .SetForCurrentTenantAsync(new SetSettingInputDto("Security.LockoutDurationMinutes", "abc")));

        Assert.Equal(SettingErrorCodes.IntegerRequired, error.Code);
        Assert.Equal("Lockout duration (minutes)", error.LocalizationData["Name"]);
        Assert.Contains("Lockout duration (minutes)", error.Message);
        Assert.DoesNotContain("Security.LockoutDurationMinutes", error.Message);
    }

    [Fact]
    public async Task Host_only_rejection_uses_the_display_name()
    {
        var service = Build(new FakeSettingStore { CanAccessHostScope = false }, localized: new Dictionary<string, string>
        {
            ["Setting:Logging.MinimumLevel"] = "最小日志级别",
        });

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.SetForCurrentTenantAsync(new SetSettingInputDto("Logging.MinimumLevel", "Debug")));

        Assert.Equal(SettingErrorCodes.HostOnly, error.Code);
        Assert.Equal("最小日志级别", error.LocalizationData["Name"]);
    }

    // 替别人判断（按收件人偏好投递）时读的是目标用户的覆盖值，不是当前请求者的
    [Fact]
    public async Task A_given_users_value_is_resolved_through_the_tenant_default()
    {
        var store = new PerUserStore();
        store.Tenant["Display.TimeZone"] = "Asia/Shanghai";
        store.Users["recipient"] = new Dictionary<string, string> { ["Display.TimeZone"] = "Asia/Tokyo" };
        store.Users[UserId.ToString()] = new Dictionary<string, string> { ["Display.TimeZone"] = "Europe/Paris" };
        var provider = Services(store).GetRequiredService<ISettingProvider>();

        Assert.Equal("Asia/Tokyo", await provider.GetOrNullForUserAsync("Display.TimeZone", "recipient"));
        Assert.Equal("Asia/Shanghai", await provider.GetOrNullForUserAsync("Display.TimeZone", "someone-else"));
        Assert.Equal("Europe/Paris", await provider.GetOrNullAsync("Display.TimeZone"));
    }

    private static SettingOutputDto Single(IReadOnlyList<SettingOutputDto> settings, string name)
        => Assert.Single(settings, s => s.Name == name);

    private static ISettingManagementService Build(
        ISettingStore store,
        FakeCurrentUser? currentUser = null,
        Dictionary<string, string>? localized = null)
        => Services(store, currentUser, localized).GetRequiredService<ISettingManagementService>();

    private static IServiceProvider Services(
        ISettingStore store,
        FakeCurrentUser? currentUser = null,
        Dictionary<string, string>? localized = null)
    {
        var services = new ServiceCollection()
            .AddSettingsCore(options => options.LocalizationResource = localized is null ? null : typeof(SettingManagementTests))
            .AddSingleton<ISettingDefinitionProvider, Definitions>()
            .AddSingleton(store)
            .AddSingleton<ICurrentUser>(currentUser ?? new FakeCurrentUser(UserId))
            // 默认写入入口的前提：锁与工作单元
            .AddLogging()
            .AddMemoryLocalLock()
            .AddSingleton<IUnitOfWorkManager>(new RecordingUnitOfWorkManager());
        if (localized is not null)
        {
            services.AddSingleton<IStringLocalizerFactory>(new DictionaryLocalizerFactory(localized));
        }

        return services.BuildServiceProvider().CreateScope().ServiceProvider;
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Display.TimeZone", "UTC", SettingScopes.All, "Time zone", "Display").IsVisibleToClients = true;
            context.Add("Export.MaxRowsPerFile", "50000");
            context.Add("Logging.MinimumLevel", "Information", SettingScopes.Host)
                .WithAllowedValues("Debug", "Information")
                .IsVisibleToClients = true;
            context.Add("Email.SmtpPassword", scopes: SettingScopes.Host).IsVisibleToClients = true;
            context.GetOrNull("Email.SmtpPassword")!.IsEncrypted = true;
            context.Add("Security.RequireTwoFactor", "false", displayName: "Require 2FA").AsBoolean().IsVisibleToClients = true;
            context.Add("Security.LockoutDurationMinutes", "15", displayName: "Lockout duration (minutes)")
                .AsInteger(1, 1440)
                .IsVisibleToClients = true;
        }
    }

    private sealed class PerUserStore : ISettingStore
    {
        public Dictionary<string, string> Tenant { get; } = [];

        public Dictionary<string, Dictionary<string, string>> Users { get; } = [];

        public bool CanAccessHostScope => false;

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(SettingScopes scope, string? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(scope switch
            {
                SettingScopes.Tenant => Tenant,
                SettingScopes.User when userId is not null && Users.TryGetValue(userId, out var values) => values,
                _ => new Dictionary<string, string>(),
            });

        public Task RemoveAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetAsync(string name, string? value, SettingScopes scope, string? userId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
