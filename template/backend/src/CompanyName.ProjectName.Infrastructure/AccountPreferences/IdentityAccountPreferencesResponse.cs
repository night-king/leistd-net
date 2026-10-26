#if (RemoteTokenAuth)
using CompanyName.ProjectName.Domain.Users.Abstractions;

namespace CompanyName.ProjectName.Infrastructure.AccountPreferences;

/// <summary>Identity 偏好接口的响应体（<c>/me/preferences</c> 与机器读取端点同形）；null 值不出现，按缺省读成 null。</summary>
internal sealed record IdentityAccountPreferencesResponse
{
    public AccountPreferenceValues? Preferences { get; init; }

    public AccountPreferenceValues? PreferenceOverrides { get; init; }

    /// <summary>两部分都要有：缺了哪一份都不是完整表示。</summary>
    public AccountPreferenceSnapshot? ToSnapshot()
        => Preferences is null || PreferenceOverrides is null
            ? null
            : new AccountPreferenceSnapshot { Preferences = Preferences, PreferenceOverrides = PreferenceOverrides };
}
#endif
