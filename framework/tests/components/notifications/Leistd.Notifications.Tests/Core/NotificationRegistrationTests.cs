using Leistd.Notifications.Dtos;
using Leistd.Notifications.Filters;
using Leistd.Notifications.Publishing;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Notifications.Tests.Core;

/// <summary><c>AddNotifications()</c> 的注册面：发布器与默认投递过滤器，宿主先登记的过滤器保留。</summary>
public sealed class NotificationRegistrationTests
{
    [Fact]
    public void Registration_exposes_the_publisher_and_the_deliver_all_filter()
    {
        var services = new ServiceCollection();

        services.AddNotifications();

        services.AssertSingle<INotificationPublisher>(ServiceLifetime.Transient);
        services.AssertImplementedBy<INotificationPublisher, NotificationPublisher>();
        services.AssertSingle<INotificationDeliveryFilter>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<INotificationDeliveryFilter, DeliverAllNotificationFilter>();
    }

    // 通知与实时两个包都会调到这里：不幂等时按 IEnumerable 解析发布器会重复发布
    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddNotifications());

    // 默认"一律投递"只是兜底：宿主先注册自己的过滤器时不能被它盖掉
    [Fact]
    public void A_delivery_filter_registered_by_the_host_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<INotificationDeliveryFilter, HostFilter>();

        services.AddNotifications();

        services.AssertSingle<INotificationDeliveryFilter>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<INotificationDeliveryFilter, HostFilter>();
    }

    private sealed class HostFilter : INotificationDeliveryFilter
    {
        public Task<bool> ShouldDeliverAsync(string userId, NotificationOutputDto notification, string channel, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
