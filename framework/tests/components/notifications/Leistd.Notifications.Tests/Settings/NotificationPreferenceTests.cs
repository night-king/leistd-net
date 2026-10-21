using Leistd.Notifications.Channels;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Filters;
using Leistd.Notifications.Settings;
using Leistd.Notifications.Settings.Options;
using Leistd.Settings;
using Leistd.Settings.Definitions;
using Leistd.Settings.Stores;
using Leistd.Security.Users;
using Leistd.TestBase.Doubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Notifications.Tests.Settings;

/// <summary>通知偏好：按收件人自己的生效值（本人覆盖 → 租户默认 → 代码默认）决定投递，没有定义的组合与必达组合一律投递。</summary>
/// <remarks>读的是收件人的设置而不是请求者的：发布方往往是别人或后台任务，读错人就是按别人的偏好给你发通知。</remarks>
public sealed class NotificationPreferenceTests
{
    private static INotificationDeliveryFilter Build(
        Dictionary<string, string> recipientValues,
        Dictionary<string, string>? tenantValues = null)
    {
        var services = new ServiceCollection()
            .AddSettingsCore()
            .AddSingleton<ISettingDefinitionProvider, PreferenceDefinitions>()
            // 请求者是另一个用户：读错人就会按请求者的偏好给收件人投递
            .AddSingleton<ICurrentUser>(new FakeCurrentUser(Guid.NewGuid()))
            .AddSingleton<ISettingStore>(new UserValuesStore("recipient", recipientValues, tenantValues ?? []))
            .AddNotifications()
            .AddNotificationPreferences(o => o.MandatoryDeliveries.Add(new NotificationDelivery("Security", INotificationChannel.InAppName)));

        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<INotificationDeliveryFilter>();
    }

    private static NotificationOutputDto Notification(string type) => new() { Id = "n1", Type = type, CreationTime = DateTime.UtcNow };

    [Theory]
    [InlineData("System", "Email", "false", false)]
    [InlineData("System", "Email", "true", true)]
    [InlineData("System", "Email", null, false)]
    [InlineData("System", "InApp", null, true)]
    [InlineData("Security", "InApp", "false", true)]
    [InlineData("Billing", "Email", null, true)]
    public async Task Delivery_follows_the_recipients_own_preference(string type, string channel, string? value, bool expected)
    {
        var values = new Dictionary<string, string>();
        if (value is not null)
        {
            values[$"Notifications.{type}.{channel}"] = value;
        }

        Assert.Equal(expected, await Build(values).ShouldDeliverAsync("recipient", Notification(type), channel));
    }

    [Fact]
    public async Task A_tenant_default_applies_when_the_recipient_has_not_chosen()
    {
        var filter = Build([], new Dictionary<string, string> { ["Notifications.System.Email"] = "true" });

        Assert.True(await filter.ShouldDeliverAsync("recipient", Notification("System"), "Email"));
    }

    // 系统邮件默认关、系统站内默认开、安全站内虽然也定义了但属于必达组合
    private sealed class PreferenceDefinitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Notifications.System.Email", "false", SettingScopes.All);
            context.Add("Notifications.System.InApp", "true", SettingScopes.User);
            context.Add("Notifications.Security.InApp", "true", SettingScopes.User);
        }
    }

    private sealed class UserValuesStore(
        string userId,
        Dictionary<string, string> values,
        Dictionary<string, string> tenantValues) : ISettingStore
    {
        public bool CanAccessHostScope => false;

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(SettingScopes scope, string? requestedUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(scope switch
            {
                SettingScopes.User when requestedUserId == userId => values,
                SettingScopes.Tenant => tenantValues,
                _ => new Dictionary<string, string>(),
            });

        public Task RemoveAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetAsync(string name, string? value, SettingScopes scope, string? requestedUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
