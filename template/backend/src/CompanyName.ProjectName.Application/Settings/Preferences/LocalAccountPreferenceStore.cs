#if (!RemoteTokenAuth)
using CompanyName.ProjectName.Application.Settings.Constants;
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;

namespace CompanyName.ProjectName.Application.Settings.Preferences;

/// <summary>偏好的持有方读写本服务的设置：生效值按 个人 → 组织 → 部署基线 回落，个人覆盖取用户层原值。</summary>
/// <param name="settingProvider">设置读取器；同一请求内按租户与用户记忆化，写入后由设置组件作废。</param>
/// <param name="settingStore">读取用户层原值；与读取器同一工作单元。</param>
/// <param name="settingManager">写入入口：沿用定义校验、业务校验器、锁、变更事件与操作记录，每次写入在独立事务里提交。</param>
/// <param name="currentUser">当前用户。</param>
internal sealed class LocalAccountPreferenceStore(
    ISettingProvider settingProvider,
    ISettingStore settingStore,
    ISettingManager settingManager,
    ICurrentUser currentUser) : IAccountPreferenceStore
{
    public Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default)
        => currentUser.Id is { } id ? GetForUserAsync(id, cancellationToken) : Task.FromResult<AccountPreferencesOutputDto?>(null);

    public async Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = userId.ToString();
        var overrides = await settingStore.GetAllAsync(SettingScopes.User, user, cancellationToken);
        return new AccountPreferencesOutputDto
        {
            Preferences = new AccountPreferenceValuesDto
            {
#if (IncludeLocalization)
                Language = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.Language, user, cancellationToken),
#endif
                TimeZone = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.TimeZone, user, cancellationToken),
                Theme = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.Theme, user, cancellationToken),
            },
            PreferenceOverrides = new AccountPreferenceValuesDto
            {
#if (IncludeLocalization)
                Language = overrides.GetValueOrDefault(SettingConstant.Display.Language),
#endif
                TimeZone = overrides.GetValueOrDefault(SettingConstant.Display.TimeZone),
                Theme = overrides.GetValueOrDefault(SettingConstant.Display.Theme),
            },
        };
    }

    public async Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default)
    {
        await settingManager.SetAsync(AccountPreferenceFields.SettingNames[field], value, SettingScopes.User, userId.ToString(), cancellationToken);

        // 提交后重读：清除覆盖时生效值回落到组织或部署基线，只有持有方算得出来
        return await GetForUserAsync(userId, cancellationToken)
               ?? throw new InvalidOperationException("Account preferences could not be read back after the write.");
    }
}
#endif
