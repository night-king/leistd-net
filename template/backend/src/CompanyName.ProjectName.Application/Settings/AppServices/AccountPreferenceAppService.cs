using CompanyName.ProjectName.Application.Settings.Dtos;
using CompanyName.ProjectName.Application.Settings.Preferences;
using CompanyName.ProjectName.Application.Settings.Validators;
#if (OpenIddictServer)
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
#endif
using Leistd.Ddd.Application.AppServices;
using Leistd.Security.Users;
#if (OpenIddictServer)
using Leistd.UnitOfWork;
#endif

namespace CompanyName.ProjectName.Application.Settings.AppServices;

/// <inheritdoc cref="IAccountPreferenceAppService"/>
/// <param name="preferenceStore">偏好的读写（持有方读写设置，资源服务转交 Identity）。</param>
/// <param name="valueValidator">写入前判定取值。</param>
/// <param name="currentUser">当前用户；写入只针对本人。</param>
#if (OpenIddictServer)
/// <param name="currentTenant">机器读取时切换到路径里的租户；设置存储与连接路由都按它取。</param>
#if (IncludeMultiTenancy)
/// <param name="tenantStore">校验目标租户存在且启用。</param>
#endif
/// <param name="unitOfWorkManager">目标租户的独立工作单元，不复用外层（宿主）已建立的上下文。</param>
/// <param name="userRepository">确认用户在目标租户里存在。</param>
#endif
public class AccountPreferenceAppService(
    IAccountPreferenceStore preferenceStore,
    AccountPreferenceValueValidator valueValidator,
#if (OpenIddictServer)
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
#endif
    IUnitOfWorkManager unitOfWorkManager,
    IUserRepository userRepository) : BaseAppService, IAccountPreferenceAppService
#else
    ICurrentUser currentUser) : BaseAppService, IAccountPreferenceAppService
#endif
{
#if (OpenIddictServer)
    private const string Host = "host";

#endif
    /// <inheritdoc />
    public async Task<AccountPreferencesOutputDto> UpdateForCurrentUserAsync(
        UpdateAccountPreferenceInputDto input,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id ?? throw new InvalidOperationException("Writing account preferences requires a signed-in user.");
        valueValidator.Validate(input);

        return await preferenceStore.SetForCurrentUserAsync(userId, input.Field, input.Value, cancellationToken);
    }
#if (OpenIddictServer)

    /// <inheritdoc />
    public async Task<AccountPreferencesOutputDto> GetForTenantUserAsync(
        string tenant,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        Guid? tenantId;
        string? tenantName = null;
        if (string.Equals(tenant, Host, StringComparison.Ordinal))
        {
            tenantId = null;
        }
#if (IncludeMultiTenancy)
        else if (Guid.TryParse(tenant, out var id)
                 && await tenantStore.FindAsync(id, cancellationToken) is { IsActive: true } record)
        {
            tenantId = id;
            tenantName = record.Name;
        }
#endif
        else
        {
            throw NotFound(userId);
        }

        // 切换与工作单元建在这里而不是辅助方法里：AsyncLocal 在被 await 的方法里改，回不到调用方
        using (currentTenant.Change(tenantId, tenantName))
        using (unitOfWorkManager.Begin(requiresNew: true))
        {
            if (await userRepository.GetByIdAsync(userId, cancellationToken) is null)
            {
                throw NotFound(userId);
            }

            return await preferenceStore.GetForUserAsync(userId, cancellationToken)
                   ?? throw new InvalidOperationException("Account preferences of an existing user could not be read.");
        }
    }

    // 租户无效与用户不存在同一个答复：调用方只需知道"这里没有这个人"，不借此探测租户状态
    private static BusinessException NotFound(Guid userId)
        => new BusinessException(UserErrorCodes.NotFound, "The user was not found.").WithData("UserId", userId);
#endif
}
