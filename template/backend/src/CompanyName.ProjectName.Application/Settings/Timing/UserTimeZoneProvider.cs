using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Settings.Resolution;

namespace CompanyName.ProjectName.Application.Settings.Timing;

/// <summary>按 <c>Display.TimeZone</c> 设置解析当前用户的展示时区。</summary>
/// <remarks>
/// 时区值只接受 IANA 名。.NET 会连 Windows 时区 ID（<c>China Standard Time</c> 之类）一起认下来，
/// 但浏览器的 <c>Intl.DateTimeFormat</c> 对它抛 <c>RangeError</c>——只按「能不能解析」放行，
/// 就会存进一个后端认、前端用不了的值，界面静默回落到浏览器时区，表现为「保存成功但不生效」。
/// 因此这里额外要求 <see cref="TimeZoneInfo.HasIanaId"/>，让两端认的是同一个集合。
/// </remarks>
/// <param name="settingProvider">设置读取器，按 用户 → 租户 → 默认值（部署基线）回落。</param>
public class UserTimeZoneProvider(ISettingProvider settingProvider) : IUserTimeZoneProvider
{
    /// <inheritdoc />
    public async Task<TimeZoneInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var id = await settingProvider.GetOrNullAsync(SettingConstant.Display.TimeZone, cancellationToken);

        return TryResolve(id, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
    }

    /// <inheritdoc />
    public async Task<TimeZoneInfo> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.TimeZone, userId.ToString(), cancellationToken);

        return TryResolve(id, out var timeZone) ? timeZone : TimeZoneInfo.Utc;
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
