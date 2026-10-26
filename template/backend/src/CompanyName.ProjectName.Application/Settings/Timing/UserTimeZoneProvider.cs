using CompanyName.ProjectName.Application.Settings.Preferences;
#if (!RemoteTokenAuth)
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Settings.Resolution;
#endif
using Leistd.Security.Users;

namespace CompanyName.ProjectName.Application.Settings.Timing;

/// <summary>按账户偏好的时区解析当前用户或收件人的展示时区。</summary>
/// <remarks>
/// 时区值只接受 IANA 名。.NET 会连 Windows 时区 ID（<c>China Standard Time</c> 之类）一起认下来，
/// 但浏览器的 <c>Intl.DateTimeFormat</c> 对它抛 <c>RangeError</c>——只按「能不能解析」放行，
/// 就会存进一个后端认、前端用不了的值，界面静默回落到浏览器时区，表现为「保存成功但不生效」。
/// 因此这里额外要求 <see cref="TimeZoneInfo.HasIanaId"/>，让两端认的是同一个集合。
/// </remarks>
/// <param name="preferenceStore">账户偏好；生效值按 个人 → 组织 → 部署基线 回落，资源服务读 Identity。</param>
/// <param name="currentUser">当前用户。</param>
#if (!RemoteTokenAuth)
/// <param name="settingProvider">没有当前用户时读组织值或部署基线。</param>
public class UserTimeZoneProvider(
    IAccountPreferenceStore preferenceStore,
    ICurrentUser currentUser,
    ISettingProvider settingProvider) : IUserTimeZoneProvider
#else
public class UserTimeZoneProvider(
    IAccountPreferenceStore preferenceStore,
    ICurrentUser currentUser) : IUserTimeZoneProvider
#endif
{
    /// <inheritdoc />
    public async Task<TimeZoneInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is { } userId)
        {
            return await GetForUserAsync(userId, cancellationToken);
        }

#if (!RemoteTokenAuth)
        var id = await settingProvider.GetOrNullAsync(SettingConstant.Display.TimeZone, cancellationToken);

        return TryResolve(id, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
#else
        // 资源服务不持有偏好，没有用户时也就没有可读的组织值
        return TimeZoneInfo.Utc;
#endif
    }

    /// <inheritdoc />
    public async Task<TimeZoneInfo> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // 暂时读不到偏好（资源服务回源失败且没有旧值）时同样回落 UTC
        var preferences = await preferenceStore.GetForUserAsync(userId, cancellationToken);

        return TryResolve(preferences?.Preferences.TimeZone, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
    }

    /// <inheritdoc />
    public async Task<DateTime> ToUserTimeAsync(DateTime utc, CancellationToken cancellationToken = default)
    {
        var timeZone = await GetAsync(cancellationToken);

        // 入参按 UTC 解释：调用方传的是存储值，而存储一律是 UTC。某些提供程序读出来
        // Kind 为 Unspecified，直接交给 ConvertTimeFromUtc 会抛异常。
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);
    }

    /// <inheritdoc />
    /// <remarks>读取与写入共用这一处判定：写入端放行的值，读取端必须解析得出来，前端也必须认。</remarks>
    public bool TryResolve(string? timeZoneId, out TimeZoneInfo timeZone) => TryResolveId(timeZoneId, out timeZone);

    /// <summary><see cref="TryResolve"/> 的纯值判定，供不能依赖设置读取的场合（如部署基线的启动校验）使用。</summary>
    /// <param name="timeZoneId">IANA 时区名。</param>
    /// <param name="timeZone">解析出的时区；不可用时为 UTC。</param>
    /// <returns>可用时为 <see langword="true"/>。</returns>
    public static bool TryResolveId(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;

        if (string.IsNullOrWhiteSpace(timeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            return false;
        }

        // Windows 时区 ID 解析得出来但浏览器不认，挡在这里。UTC 自身带 IANA 标识，照常通过。
        if (!found.HasIanaId)
        {
            return false;
        }

        timeZone = found;
        return true;
    }
}
