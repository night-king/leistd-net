#if (LocalIdentity && IncludeNotifications)
using CompanyName.ProjectName.Api.Notifications;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
#if (IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Preferences;
#endif
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.Notifications.Dtos;
using Leistd.Notifications.Publishing;
#if (IncludeLocalization)
using Microsoft.Extensions.Localization;
#endif
using Microsoft.Extensions.Logging.Abstractions;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>
/// 锁定提醒的截止时刻按<b>收件人</b>的展示时区写出并注明时区：锁定发生在登录前，当前用户为空，
/// 必须按目标用户解析，不能用当前用户的时区，也不能把当地时刻标成 UTC。
/// </summary>
public sealed class NotificationSecurityAlertPublisherTests
{
    private static readonly Guid Recipient = Guid.CreateVersion7();
    private static readonly DateTime LockedUntilUtc = new(2026, 3, 1, 5, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_unlock_time_is_written_in_the_recipients_time_zone_and_names_it()
    {
        var content = await PublishLockoutAsync(new Dictionary<Guid, string> { [Recipient] = "Asia/Tokyo" });

        Assert.Contains("2026-03-01 14:30 (Asia/Tokyo)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("America/New_York", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_time_zone_the_unlock_time_is_UTC_and_says_so()
    {
        var content = await PublishLockoutAsync(new Dictionary<Guid, string>());

        Assert.Contains("2026-03-01 05:30 (UTC)", content, StringComparison.Ordinal);
    }

    /// <summary>提醒是尽力而为的：下游自己的超时或取消不能让已经提交的改密、找回报失败；调用方取消时照常传出。</summary>
    [Fact]
    public async Task A_downstream_cancellation_is_swallowed_but_a_caller_cancellation_is_not()
    {
        var alerts = CreatePublisher(new CancelingPublisher());

        await alerts.PublishAsync(Recipient, new SecurityAlert(SecurityAlertKind.PasswordChanged));

        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => alerts.PublishAsync(Recipient, new SecurityAlert(SecurityAlertKind.PasswordChanged), caller.Token));
    }

    private static NotificationSecurityAlertPublisher CreatePublisher(INotificationPublisher publisher) => new(
        publisher,
        new PerUserTimeZones(new Dictionary<Guid, string>()),
#if (IncludeLocalization)
        new NoPreferences(),
        new TemplateLocalizerFactory(),
#endif
        NullLogger<NotificationSecurityAlertPublisher>.Instance);

    private sealed class CancelingPublisher : INotificationPublisher
    {
        public Task PublishToUserAsync(string userId, NotificationInputDto notification, CancellationToken ct = default)
            => ct.IsCancellationRequested ? Task.FromCanceled(ct) : throw new TaskCanceledException("Downstream timed out.");
    }

    private static async Task<string> PublishLockoutAsync(IReadOnlyDictionary<Guid, string> timeZones)
    {
        var publisher = new CapturingPublisher();
        var alerts = new NotificationSecurityAlertPublisher(
            publisher,
            new PerUserTimeZones(timeZones),
#if (IncludeLocalization)
            new NoPreferences(),
            new TemplateLocalizerFactory(),
#endif
            NullLogger<NotificationSecurityAlertPublisher>.Instance);

        await alerts.PublishAsync(Recipient, new SecurityAlert(SecurityAlertKind.LockedOut, Until: LockedUntilUtc));

        var (userId, notification) = Assert.Single(publisher.Published);
        Assert.Equal(Recipient.ToString(), userId);
        return notification.Content!;
    }

    private sealed class CapturingPublisher : INotificationPublisher
    {
        public List<(string UserId, NotificationInputDto Notification)> Published { get; } = [];

        public Task PublishToUserAsync(string userId, NotificationInputDto notification, CancellationToken ct = default)
        {
            Published.Add((userId, notification));
            return Task.CompletedTask;
        }
    }

    // 当前用户是另一个时区：用错入口就会拿到纽约时间
    private sealed class PerUserTimeZones(IReadOnlyDictionary<Guid, string> timeZones) : IUserTimeZoneProvider
    {
        public Task<TimeZoneInfo> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        public Task<TimeZoneInfo> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(timeZones.TryGetValue(userId, out var id) ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Utc);

        public Task<DateTime> ToUserTimeAsync(DateTime utc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool TryResolve(string? timeZoneId, out TimeZoneInfo timeZone) => UserTimeZoneProvider.TryResolveId(timeZoneId, out timeZone);
    }
#if (IncludeLocalization)

    private sealed class NoPreferences : IAccountPreferenceStore
    {
        public Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<AccountPreferencesOutputDto?>(null);

        public Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountPreferencesOutputDto?>(null);

        public Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
            Guid userId,
            string field,
            string? value,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    // 只关心占位符怎么填：正文模板里只留 {Until}
    private sealed class TemplateLocalizerFactory : IStringLocalizerFactory
    {
        public IStringLocalizer Create(Type resourceSource) => new TemplateLocalizer();

        public IStringLocalizer Create(string baseName, string location) => new TemplateLocalizer();
    }

    private sealed class TemplateLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name]
            => new(name, name.EndsWith(":Content", StringComparison.Ordinal) ? "Unlocks at {Until}." : name);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
#endif
}
#endif
