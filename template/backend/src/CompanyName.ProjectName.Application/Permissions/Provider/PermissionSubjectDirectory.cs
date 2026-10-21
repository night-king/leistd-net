using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Constants;
using Leistd.Ddd.Domain.Repositories;

namespace CompanyName.ProjectName.Application.Permissions.Provider;

/// <summary>权限管理用例的主体目录：按本项目的用户与角色确认主体存在并给出显示名。</summary>
/// <remarks>
/// 显示名进审计记录的目标名快照：权限授予替换是 Critical 级动作，目标若只是裸 GUID，最该被看懂的记录最难看懂。
/// Key 不是 GUID 即视为不存在（404），不让格式错误以 500 出去。
/// </remarks>
internal sealed class PermissionSubjectDirectory(
    IRepository<User, Guid> userRepository,
    IRepository<Role, Guid> roleRepository) : IPermissionSubjectDirectory
{
    public async Task<PermissionSubjectInfo?> FindAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(providerKey, out var id))
        {
            return null;
        }

        return providerName switch
        {
            PermissionGrantProviderNames.User => await userRepository.GetByIdAsync(id, cancellationToken) is { } user
                ? new PermissionSubjectInfo(user.DisplayName ?? user.Username)
                : null,
            PermissionGrantProviderNames.Role => await roleRepository.GetByIdAsync(id, cancellationToken) is { } role
                ? new PermissionSubjectInfo(role.DisplayName ?? role.Name)
                : null,
            _ => null
        };
    }
}
