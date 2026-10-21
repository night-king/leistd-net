using Leistd.Notifications.Filters;
using Leistd.Notifications.Settings;
using Leistd.Notifications.Settings.Filters;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Notifications.Tests.Settings;

/// <summary>通知偏好的注册面：与通知组件的注册顺序无关，总是成为唯一的投递过滤器。</summary>
/// <remarks>
/// 过滤器并存时发布器只取最后一条：偏好要么静默不生效，要么取决于宿主把哪行写在后面。
/// </remarks>
public sealed class NotificationPreferenceRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Preferences_become_the_only_delivery_filter_regardless_of_order(bool preferencesFirst)
    {
        var services = new ServiceCollection();
        if (preferencesFirst)
        {
            services.AddNotificationPreferences();
        }

        services.AddNotifications();
        if (!preferencesFirst)
        {
            services.AddNotificationPreferences();
        }

        // 读的是作用域内的用户设置，必须 Scoped
        services.AssertSingle<INotificationDeliveryFilter>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<INotificationDeliveryFilter, SettingsNotificationDeliveryFilter>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddNotificationPreferences());

    // 有意覆盖宿主此前登记的过滤器：并存时生效的是哪一条取决于注册顺序。重复调用仍只剩偏好过滤器一条
    [Fact]
    public void Repeated_registration_replaces_a_host_filter_and_keeps_a_single_preference_filter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationDeliveryFilter, DeliverAllNotificationFilter>();

        services.AddNotificationPreferences().AddNotificationPreferences();

        services.AssertSingle<INotificationDeliveryFilter>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<INotificationDeliveryFilter, SettingsNotificationDeliveryFilter>();
    }
}
