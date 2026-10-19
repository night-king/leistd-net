using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.Settings.Resolution;

namespace CompanyName.ProjectName.UnitTests.Application;

/// <summary>展示时区的解析与换算。</summary>
/// <remarks>
/// 回落行为是这里的重点：设置缺失或值失效时必须退回服务器时区，而不是抛异常——
/// 一个坏掉的偏好设置不该让整张列表报 500。时区数据库会更名（如 Europe/Kiev → Europe/Kyiv），
/// 库里的历史值失效是迟早的事。
/// </remarks>
public class UserTimeZoneProviderTests
{
    private static UserTimeZoneProvider ProviderWith(string? timeZoneId)
        => new(new FixedSettingProvider(timeZoneId));

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
    public async Task A_named_user_is_resolved_by_their_own_setting_not_the_current_users()
    {
        var target = Guid.CreateVersion7();
        var provider = new UserTimeZoneProvider(new PerUserSettingProvider(
            current: "America/New_York",
            perUser: new Dictionary<string, string> { [target.ToString()] = "Asia/Tokyo" }));

        Assert.Equal("Asia/Tokyo", (await provider.GetForUserAsync(target)).Id);
        Assert.Equal(TimeZoneInfo.Utc.Id, (await provider.GetForUserAsync(Guid.CreateVersion7())).Id);
    }

    private sealed class PerUserSettingProvider(string current, IReadOnlyDictionary<string, string> perUser) : ISettingProvider
    {
        public Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(name == SettingConstant.Display.TimeZone ? current : null);

        public Task<string?> GetOrNullForUserAsync(string name, string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(name == SettingConstant.Display.TimeZone && perUser.TryGetValue(userId, out var id) ? id : null);

        public Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<T?>(default);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
            bool visibleToClientsOnly = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }

    private sealed class FixedSettingProvider(string? timeZoneId) : ISettingProvider
    {
        public Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(name == SettingConstant.Display.TimeZone ? timeZoneId : null);

        public Task<string?> GetOrNullForUserAsync(string name, string userId, CancellationToken cancellationToken = default)
            => GetOrNullAsync(name, cancellationToken);

        public Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<T?>(default);

        public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
            bool visibleToClientsOnly = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
    }
}
