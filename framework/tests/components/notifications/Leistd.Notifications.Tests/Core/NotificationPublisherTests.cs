using Leistd.Data.Paging;
using Leistd.Notifications.Channels;
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Stores;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Filters;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leistd.Notifications.Tests.Core;

public class NotificationPublisherTests
{
    [Fact]
    public async Task Publishing_to_a_user_writes_history_and_pushes()
    {
        var (publisher, store, channel) = Build();

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Single(store.Saved);
        Assert.Equal("user-1", store.Saved[0].UserId);
        Assert.Single(channel.ToUser);
    }

    // 通知只面向人：每条都落历史。瞬态推送是 realtime 组件的职责，不在本契约内。
    [Fact]
    public async Task Every_notification_is_persisted_for_its_recipient()
    {
        var (publisher, store, _) = Build();

        await publisher.PublishToUserAsync("user-1", New("一"));
        await publisher.PublishToUserAsync("user-2", New("二"));

        Assert.Equal(["user-1", "user-2"], store.Saved.Select(x => x.UserId));
    }

    // 存储是必需依赖：漏装持久化时在解析发布器处失败，而不是静默变成"只推不落"。
    [Fact]
    public void Resolving_the_publisher_without_a_store_fails()
    {
        var sp = new ServiceCollection()
            .AddLogging()   // 必须先装：否则抛出的可能是"缺 ILogger"，这条用例就证明不了缺存储
            .AddSingleton<IClock>(new FakeClock())
            .AddNotifications()
            .BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<INotificationPublisher>());

        Assert.Contains(nameof(INotificationStore), exception.Message);
    }

    // 先落库再推送：推送失败只是这一次没送到，历史还在。
    [Fact]
    public async Task History_is_written_before_the_push()
    {
        var order = new List<string>();
        var publisher = new NotificationPublisher(
            new FakeClock(),
            [new CallbackChannel(() => order.Add("push"))],
            new CallbackStore(() => order.Add("save")),
            DeliverAll.Instance,
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Equal(["save", "push"], order);
    }

    // 创建时刻只能来自时钟：发布输入里没有这个字段，调用方传不进 Local/Unspecified 的时间，
    // 「通知列表排序基准与其它时间线对不上」这类问题因此不再可能发生，而不是靠归一化去救。
    [Fact]
    public async Task Creation_time_always_comes_from_the_clock()
    {
        var clock = new FakeClock();
        var (publisher, store, _) = Build(clock);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Equal(clock.Now, store.Saved[0].Notification.CreationTime);
        Assert.Equal(DateTimeKind.Utc, store.Saved[0].Notification.CreationTime.Kind);
    }

    // 同一份内容扇出给多个用户是多条独立记录。入参若是带 Id 的输出 DTO，调用方复用一个对象
    // 逐人发布时两条记录带同一个主键，第二次落库直接冲突——身份必须在收件人边界产生。
    [Fact]
    public async Task The_same_content_becomes_an_independent_record_per_recipient()
    {
        var (publisher, store, _) = Build();
        var content = New("全员公告");

        await publisher.PublishToUserAsync("user-1", content);
        await publisher.PublishToUserAsync("user-2", content);

        var ids = store.Saved.Select(x => x.Notification.Id).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.DoesNotContain(ids, string.IsNullOrWhiteSpace);
    }

    // 客户端拿实时推送里的 ID 去标记已读，必须命中自己那条记录：两侧的 ID 只能是同一个。
    [Fact]
    public async Task The_pushed_notification_carries_the_same_id_as_the_stored_one()
    {
        var (publisher, store, channel) = Build();

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Equal(store.Saved[0].Notification.Id, channel.ToUser[0].Notification.Id);
    }

    // 未读状态由发布器定：新发布的通知一定是未读，不看调用方。
    [Fact]
    public async Task A_freshly_published_notification_is_unread()
    {
        var (publisher, store, _) = Build();

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.False(store.Saved[0].Notification.IsRead);
    }

    // 跨渠道隔离归发布器：一个宿主自定义渠道抛错，后面的渠道仍要收到，历史也已经落库。
    // 吞异常由发布方统一负责：若要求"每个实现自己吞异常"，这条保证就取决于每个实现者是否记得。
    [Fact]
    public async Task A_failing_channel_does_not_stop_the_remaining_channels()
    {
        var store = new RecordingStore();
        var second = new RecordingChannel();
        var publisher = new NotificationPublisher(
            new FakeClock(),
            [new CallbackChannel(() => throw new InvalidOperationException("channel down")), second],
            store,
            DeliverAll.Instance,
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Single(store.Saved);
        Assert.Single(second.ToUser);
    }

    // 取消是调用方主动放弃这次操作，要如实传播——不能被隔离逻辑伪装成"某个渠道失败了"。
    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_treated_as_a_channel_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var second = new RecordingChannel();
        // 渠道接住真实 token 再 ThrowIfCancellationRequested：无条件抛 OCE 的写法证明不了
        // "调用方的 token 真的传到了渠道"，那条链断掉时用例照样绿。
        CancellationToken received = default;
        var first = new TokenCapturingChannel(ct =>
        {
            received = ct;
            ct.ThrowIfCancellationRequested();
        });
        var publisher = new NotificationPublisher(
            new FakeClock(), [first, second], new RecordingStore(),
            DeliverAll.Instance, NullLogger<NotificationPublisher>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishToUserAsync("user-1", New("标题"), cts.Token));

        Assert.Equal(cts.Token, received);
        Assert.Empty(second.ToUser);
    }

    // 边界：渠道自身超时抛 OCE，而调用方 token 并未取消——那仍是一次渠道故障，
    // 应按普通失败隔离并继续。发布器的 `when (ct.IsCancellationRequested)` 过滤器就是干这个的。
    [Fact]
    public async Task A_channel_timeout_is_isolated_when_the_caller_did_not_cancel()
    {
        var second = new RecordingChannel();
        var publisher = new NotificationPublisher(
            new FakeClock(),
            [new CallbackChannel(() => throw new OperationCanceledException()), second],
            new RecordingStore(),
            DeliverAll.Instance,
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Single(second.ToUser);
    }

    // 站内关掉：历史不落，站内渠道不推；别的渠道照常按自己的偏好投
    [Fact]
    public async Task When_in_app_is_filtered_out_nothing_is_saved_or_pushed_but_other_channels_still_deliver()
    {
        var store = new RecordingStore();
        var inApp = new RecordingChannel();
        var email = new RecordingChannel("Email");
        var publisher = new NotificationPublisher(
            new FakeClock(), [inApp, email], store,
            new FilterStub((_, channel) => channel != INotificationChannel.InAppName),
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Empty(store.Saved);
        Assert.Empty(inApp.ToUser);
        Assert.Single(email.ToUser);
    }

    [Fact]
    public async Task A_filtered_out_channel_is_skipped_while_in_app_is_kept()
    {
        var store = new RecordingStore();
        var email = new RecordingChannel("Email");
        var publisher = new NotificationPublisher(
            new FakeClock(), [email], store,
            new FilterStub((_, channel) => channel != "Email"),
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题"));

        Assert.Single(store.Saved);
        Assert.Empty(email.ToUser);
    }

    // 过滤器按类别决定：它得拿到通知本身，而不只是收件人
    [Fact]
    public async Task The_filter_sees_the_notification_type_and_the_channel_name()
    {
        var seen = new List<(string Type, string Channel)>();
        var publisher = new NotificationPublisher(
            new FakeClock(), [new RecordingChannel("Email")], new RecordingStore(),
            new FilterStub((notification, channel) =>
            {
                seen.Add((notification.Type, channel));
                return true;
            }),
            NullLogger<NotificationPublisher>.Instance);

        await publisher.PublishToUserAsync("user-1", New("标题") with { Type = "Security" });

        Assert.Equal([("Security", INotificationChannel.InAppName), ("Security", "Email")], seen);
    }

    private static NotificationInputDto New(string title) => new() { Title = title };

    private static (NotificationPublisher Publisher, RecordingStore Store, RecordingChannel Channel) Build(
        FakeClock? clock = null)
    {
        var store = new RecordingStore();
        var channel = new RecordingChannel();
        return (
            new NotificationPublisher(
                clock ?? new FakeClock(), [channel], store, DeliverAll.Instance, NullLogger<NotificationPublisher>.Instance),
            store,
            channel);
    }

    private sealed class RecordingStore : INotificationStore
    {
        public List<(NotificationOutputDto Notification, string UserId)> Saved { get; } = [];

        public Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default)
        {
            Saved.Add((notification, userId));
            return Task.CompletedTask;
        }

        public Task<PagedResult<NotificationOutputDto>> GetByUserAsync(
            string userId, PageRequest page, bool unreadOnly = false, CancellationToken ct = default)
            => Task.FromResult(PagedResult<NotificationOutputDto>.Empty);

        public Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> DeleteAllAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);

        public Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkAllAsReadAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class CallbackStore(Action onSave) : INotificationStore
    {
        public Task SaveAsync(NotificationOutputDto notification, string userId, CancellationToken ct = default)
        {
            onSave();
            return Task.CompletedTask;
        }

        public Task<PagedResult<NotificationOutputDto>> GetByUserAsync(
            string userId, PageRequest page, bool unreadOnly = false, CancellationToken ct = default)
            => Task.FromResult(PagedResult<NotificationOutputDto>.Empty);

        public Task<bool> DeleteAsync(string notificationId, string userId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> DeleteAllAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);

        public Task MarkAsReadAsync(string notificationId, string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkAllAsReadAsync(string userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> GetUnreadCountAsync(string userId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class CallbackChannel(Action onSend) : INotificationChannel
    {
        public string Name => INotificationChannel.InAppName;

        public Task DeliverAsync(string userId, NotificationOutputDto notification, CancellationToken ct = default)
        {
            onSend();
            return Task.CompletedTask;
        }
    }

    /// <summary>把收到的 <see cref="CancellationToken"/> 交给外部检查。</summary>
    private sealed class TokenCapturingChannel(Action<CancellationToken> onDeliver) : INotificationChannel
    {
        public string Name => INotificationChannel.InAppName;

        public Task DeliverAsync(string userId, NotificationOutputDto notification, CancellationToken ct = default)
        {
            onDeliver(ct);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChannel(string name = INotificationChannel.InAppName) : INotificationChannel
    {
        public string Name => name;

        // 连通知一起记：推送里的 ID 必须与落库的那条一致，只记 userId 验不到这件事。
        public List<(string UserId, NotificationOutputDto Notification)> ToUser { get; } = [];

        public Task DeliverAsync(string userId, NotificationOutputDto notification, CancellationToken ct = default)
        {
            ToUser.Add((userId, notification));
            return Task.CompletedTask;
        }
    }

    private sealed class DeliverAll : INotificationDeliveryFilter
    {
        public static readonly DeliverAll Instance = new();

        public Task<bool> ShouldDeliverAsync(string userId, NotificationOutputDto notification, string channel, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FilterStub(Func<NotificationOutputDto, string, bool> allow) : INotificationDeliveryFilter
    {
        public Task<bool> ShouldDeliverAsync(string userId, NotificationOutputDto notification, string channel, CancellationToken ct = default)
            => Task.FromResult(allow(notification, channel));
    }
}
