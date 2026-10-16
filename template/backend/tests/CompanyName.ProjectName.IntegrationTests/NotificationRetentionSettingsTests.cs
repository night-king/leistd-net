#if (IncludeNotifications)
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.ExceptionHandling;
using Leistd.Notifications.EntityFrameworkCore.Options;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Stores;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 通知保留期由宿主级设置覆盖配置（默认开启、已读 90 天、未读 365 天）：写入后本进程立即应用；
/// 未读不得短于已读，分开写入、清除回落时都不能留下违序的一组。
/// </summary>
public sealed class NotificationRetentionSettingsTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Retention_follows_host_settings_and_never_lets_unread_expire_first()
    {
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<NotificationRetentionOptions>>();
        Assert.Equal((true, 90, 365), Snapshot(monitor));

        try
        {
            await SetHostAsync(SettingConstant.NotificationRetention.UnreadDays, "500");
            await SetHostAsync(SettingConstant.NotificationRetention.ReadDays, "400");
            await SetHostAsync(SettingConstant.NotificationRetention.Enabled, "false");
            Assert.Equal((false, 400, 500), Snapshot(monitor));

            await AssertOrderRejectedAsync(() => SetHostAsync(SettingConstant.NotificationRetention.ReadDays, "600"));

            // 清除未读会回落到部署基线 365 天，比已读的 400 天短：清除同样被拒，库里与生效值都不变
            await AssertOrderRejectedAsync(() => SetHostAsync(SettingConstant.NotificationRetention.UnreadDays, null));
            Assert.Equal((false, 400, 500), Snapshot(monitor));
        }
        finally
        {
            // 先清已读（回落 90 ≤ 500）再清未读（回落 365 ≥ 90），两步都合规
            await SetHostAsync(SettingConstant.NotificationRetention.ReadDays, null);
            await SetHostAsync(SettingConstant.NotificationRetention.UnreadDays, null);
            await SetHostAsync(SettingConstant.NotificationRetention.Enabled, null);
        }

        Assert.Equal((true, 90, 365), Snapshot(monitor));
    }

    // 另一项按设置链判定而不是 Options 的当前值：其它实例尚未刷新时，库里的值可能已经和本进程生效的不同
    [Fact]
    public async Task The_order_check_reads_the_stored_value_rather_than_the_applied_options()
    {
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<NotificationRetentionOptions>>();
        await WriteRawHostValueAsync(SettingConstant.NotificationRetention.UnreadDays, "50");
        try
        {
            Assert.Equal(365, monitor.CurrentValue.UnreadRetentionDays);

            await AssertOrderRejectedAsync(() => SetHostAsync(SettingConstant.NotificationRetention.ReadDays, "60"));
        }
        finally
        {
            await WriteRawHostValueAsync(SettingConstant.NotificationRetention.UnreadDays, null);
        }
    }

    private static (bool Enabled, int Read, int Unread) Snapshot(IOptionsMonitor<NotificationRetentionOptions> monitor)
        => (monitor.CurrentValue.Enabled, monitor.CurrentValue.ReadRetentionDays, monitor.CurrentValue.UnreadRetentionDays);

    private static async Task AssertOrderRejectedAsync(Func<Task> write)
    {
        var error = await Assert.ThrowsAsync<BusinessException>(write);
        Assert.Equal(AppSettingErrorCodes.NotificationRetentionOrder, error.Code);
    }

    private async Task SetHostAsync(string name, string? value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        await services.GetRequiredService<ISettingManager>().SetAsync(name, value, SettingScopes.Host);
        await unitOfWork.CompleteAsync();
    }

    // 只写存储、不发变更事件：本进程不重新应用，模拟"别的实例写了、这里还没刷新"
    private async Task WriteRawHostValueAsync(string name, string? value)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        await services.GetRequiredService<ISettingStore>().SetAsync(name, value, SettingScopes.Host, userId: null);
        await unitOfWork.CompleteAsync();
    }
}
#endif
