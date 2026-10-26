#if (RemoteTokenAuth)
using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Domain.Users.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.ObjectMapping.Abstractions;
using Leistd.Security.Users;

namespace CompanyName.ProjectName.Application.Settings.Preferences;

/// <summary>资源服务不持有账户偏好：按当前租户向 Identity 读取，写入以本人委托转交 Identity。</summary>
/// <param name="identity">Identity 账户偏好端口（缓存、回源与 Token Exchange 在 Infrastructure）。</param>
/// <param name="currentUser">当前用户。</param>
/// <param name="currentTenant">当前租户。</param>
/// <param name="objectMapper">端口给出的表示按名称约定映射成输出 DTO。</param>
internal sealed class RemoteAccountPreferenceStore(
    IIdentityAccountPreferences identity,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IObjectMapper objectMapper) : IAccountPreferenceStore
{
    public async Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is not { } userId)
        {
            return null;
        }

        var snapshot = await identity.ReadAsync(currentTenant.Id, userId, AccountPreferenceReadMode.CurrentUser, cancellationToken);
        return snapshot is null ? null : ToOutput(snapshot);
    }

    public async Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var snapshot = await identity.ReadAsync(currentTenant.Id, userId, AccountPreferenceReadMode.Server, cancellationToken);
        return snapshot is null ? null : ToOutput(snapshot);
    }

    public async Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default)
        => ToOutput(await identity.WriteForCurrentUserAsync(currentTenant.Id, userId, field, value, cancellationToken));

    private AccountPreferencesOutputDto ToOutput(AccountPreferenceSnapshot snapshot)
        => objectMapper.Map<AccountPreferenceSnapshot, AccountPreferencesOutputDto>(snapshot);
}
#endif
