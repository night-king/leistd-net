using System.Security.Claims;
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Preferences;
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.Security.Users;
#if (!RemoteTokenAuth)
using Leistd.Settings.Resolution;
#endif

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>展示时区的解析与换算。</summary>
/// <remarks>
/// 回落行为是这里的重点：设置缺失或值失效时必须退回服务器时区，而不是抛异常——
/// 一个坏掉的偏好设置不该让整张列表报 500。时区数据库会更名（如 Europe/Kiev → Europe/Kyiv），
/// 库里的历史值失效是迟早的事。
/// </remarks>
public class UserTimeZoneProviderTests
{
    private static readonly Guid CurrentUserId = Guid.CreateVersion7();

    // 当前用户的时区来自账户偏好：生效值由持有方按 个人 → 组织 → 部署基线 算好
    private static UserTimeZoneProvider ProviderWith(string? timeZoneId)
        => Create(new PreferenceStore(new Dictionary<Guid, string?> { [CurrentUserId] = timeZoneId }), CurrentUserId);

    private static UserTimeZoneProvider Create(IAccountPreferenceStore store, Guid? currentUserId)
#if (!RemoteTokenAuth)
        => new(store, new FixedCurrentUser(currentUserId), new FixedSettingProvider(null));
#else
        => new(store, new FixedCurrentUser(currentUserId));
#endif

    [Fact]
    public async Task A_configured_time_zone_is_used()
    {
        var timeZone = await ProviderWith("Asia/Tokyo").GetAsync();

        Assert.Equal(TimeSpan.FromHours(9), timeZone.GetUtcOffset(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task UTC_is_converted_into_the_configured_time_zone()
    {
        var utc = new DateTime(2026, 3, 1, 5, 30, 0, DateTimeKind.Utc);

        var userTime = await ProviderWith("Asia/Tokyo").ToUserTimeAsync(utc);

        Assert.Equal(new DateTime(2026, 3, 1, 14, 30, 0), userTime);
        // 换算后既不是 UTC 也未必是本机本地时间，Kind 只能是 Unspecified。
        Assert.Equal(DateTimeKind.Unspecified, userTime.Kind);
    }

    // 存储值经某些提供程序读出来 Kind 是 Unspecified；直接交给 ConvertTimeFromUtc 会抛异常。
    [Fact]
    public async Task An_unspecified_kind_input_is_read_as_UTC()
    {
        var stored = new DateTime(2026, 3, 1, 5, 30, 0, DateTimeKind.Unspecified);

        var userTime = await ProviderWith("Asia/Tokyo").ToUserTimeAsync(stored);

        Assert.Equal(new DateTime(2026, 3, 1, 14, 30, 0), userTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus")]
    public async Task A_missing_or_broken_setting_falls_back_to_UTC_not_the_server_time_zone(string? configured)
    {
        var timeZone = await ProviderWith(configured).GetAsync();

        Assert.Equal(TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    // 写入端与读取端必须共用同一套判定，否则会写进一个之后解析不出来的值。
    //
    // Windows 时区 ID 是这里的重点：.NET 认它，浏览器的 Intl.DateTimeFormat 抛错。
    // 只判断「能不能解析」就会放行一个后端 200、前端用不了的值，界面静默回落到本地时区，
    // 表现成「保存成功但不生效」——正是时区功能最容易悄悄失效的地方。
    [Theory]
    [InlineData("Asia/Shanghai", true)]
    [InlineData("America/New_York", true)]
    [InlineData("UTC", true)]           // UTC 自身带 IANA 标识
    [InlineData("China Standard Time", false)]
    [InlineData("Eastern Standard Time", false)]
    [InlineData("Mars/Olympus", false)]
    [InlineData("", false)]
    public void Only_IANA_identifiers_are_accepted(string id, bool expected)
    {
        Assert.Equal(expected, ProviderWith(null).TryResolve(id, out var timeZone));
        Assert.Equal(expected ? id : TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    // 库里存着 Windows ID 时（旧数据、脚本写入）读取端也不能用它：两端认的集合必须一致。
    [Fact]
    public async Task A_windows_identifier_in_storage_falls_back_to_UTC()
    {
        var timeZone = await ProviderWith("China Standard Time").GetAsync();

        Assert.Equal(TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    // 收件人不是当前用户（例如登录前的锁定提醒）：按目标用户读，不能落到当前用户身上
    [Fact]
    public async Task A_named_user_is_resolved_by_their_own_preference_not_the_current_users()
    {
        var target = Guid.CreateVersion7();
        var provider = Create(
            new PreferenceStore(new Dictionary<Guid, string?> { [CurrentUserId] = "America/New_York", [target] = "Asia/Tokyo" }),
            CurrentUserId);

        Assert.Equal("Asia/Tokyo", (await provider.GetForUserAsync(target)).Id);
        Assert.Equal(TimeZoneInfo.Utc.Id, (await provider.GetForUserAsync(Guid.CreateVersion7())).Id);
    }

    // 资源服务回源失败且没有旧值时读取器返回 null：同样回落 UTC，不报错
    [Fact]
    public async Task Unavailable_preferences_fall_back_to_UTC()
    {
        var provider = Create(new PreferenceStore(new Dictionary<Guid, string?>(), unavailable: true), CurrentUserId);

        Assert.Equal(TimeZoneInfo.Utc.Id, (await provider.GetAsync()).Id);
    }
#if (!RemoteTokenAuth)

    // 没有当前用户（如匿名请求）时按组织值或部署基线
    [Fact]
    public async Task Without_a_current_user_the_organization_value_is_used()
    {
        var provider = new UserTimeZoneProvider(
            new PreferenceStore(new Dictionary<Guid, string?>()),
            new FixedCurrentUser(null),
            new FixedSettingProvider("Asia/Tokyo"));

        Assert.Equal("Asia/Tokyo", (await provider.GetAsync()).Id);
    }
#else

    // 资源服务不持有偏好：没有当前用户时没有可读的组织值
    [Fact]
    public async Task Without_a_current_user_the_resource_service_uses_UTC()
    {
        var provider = Create(new PreferenceStore(new Dictionary<Guid, string?>()), null);

        Assert.Equal(TimeZoneInfo.Utc.Id, (await provider.GetAsync()).Id);
    }
#endif

    private sealed class PreferenceStore(IReadOnlyDictionary<Guid, string?> timeZones, bool unavailable = false) : IAccountPreferenceStore
    {
        public Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The time zone provider reads preferences by user id.");

        public Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(unavailable
                ? null
                : new AccountPreferencesOutputDto
                {
                    Preferences = new AccountPreferenceValuesDto { TimeZone = timeZones.GetValueOrDefault(userId) },
                    PreferenceOverrides = new AccountPreferenceValuesDto(),
                });

        public Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
            Guid userId,
            string field,
            string? value,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The time zone provider only reads preferences.");
    }

    private sealed class FixedCurrentUser(Guid? id) : ICurrentUser
    {
        public bool IsAuthenticated => Id is not null;
        public string? SubjectId => Id?.ToString();
        public Guid? Id { get; } = id;
        public Guid? TenantId => null;
        public string? Username => null;
        public string? Name => null;
        public string? Email => null;
        public Claim? FindClaim(string claimType) => null;
        public IReadOnlyList<Claim> FindClaims(string claimType) => [];
        public bool IsInRole(string role) => false;
    }
#if (!RemoteTokenAuth)

    private sealed class FixedSettingProvider(string? timeZoneId) : ISettingProvider
    {
        public Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(name == CompanyName.ProjectName.Application.Settings.Provider.SettingConstant.Display.TimeZone ? timeZoneId : null);

        public Task<string?> GetOrNullForUserAsync(string name, string userId, CancellationToken cancellationToken = default)
            => GetOrNullAsync(name, cancellationToken);

        public Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<T?>(default);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
            bool visibleToClientsOnly = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }
#endif
}
