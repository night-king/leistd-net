using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.Auditing.Abstractions;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Domain.Users.DomainServices;

/// <summary>角色领域服务：角色名在租户内唯一，内置角色的定义与保障，以及删除前的校验。</summary>
public sealed class RoleDomainService(
    IRepository<Role, Guid> roleRepository,
    IRepository<User, Guid> userRepository,
    IDataFilter dataFilter,
    ILogger<RoleDomainService> logger)
{
    /// <summary>默认角色：新用户未指定角色时分配。</summary>
    public const string MemberRoleName = "Member";

    /// <summary>创建自定义角色（非内置），角色名已被占用时以 <see cref="RoleErrorCodes.NameAlreadyUsed"/> 拒绝。</summary>
    /// <remarks>
    /// 查重要看见被软删除的行：角色名的唯一索引没有排除 <c>IsDeleted</c>，删掉的角色仍然占着名字，
    /// 而仓储默认把这些行过滤掉。不关掉过滤，这里会答"可用"，随后落库撞唯一索引——
    /// 用户看到的是 500，而不是"角色名已被占用"。
    /// <para>保留租户过滤：唯一索引按租户分开，跨租户允许同名。</para>
    /// </remarks>
    public async Task<Role> CreateAsync(
        string name,
        string displayName,
        string? description,
        bool isDefault,
        int sort,
        CancellationToken cancellationToken = default)
    {
        bool taken;
        using (dataFilter.Disable<ISoftDelete>())
        {
            taken = await roleRepository.AnyAsync(r => r.Name == name, cancellationToken);
        }

        if (taken)
        {
            throw new BusinessException(RoleErrorCodes.NameAlreadyUsed, $"Role '{name}' already exists.")
                .WithData("Name", name);
        }

        var role = new Role(name, displayName, description, isStatic: false, isDefault: isDefault, sort: sort);
        await roleRepository.InsertAsync(role, cancellationToken);

        logger.LogInformation("Role created: {Name} (ID: {Id})", role.Name, role.Id);
        return role;
    }

    /// <summary>确保内置的管理员与默认成员角色存在，不存在就建。宿主初始化与租户初始化共用这一份定义。</summary>
    /// <param name="adminDescription">管理员角色的说明，宿主与租户的管理范围不同。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <exception cref="InvalidOperationException">同名内置角色已被删除：它仍占着角色名，不能另建一个。</exception>
    public async Task<(Role Admin, Role Member)> EnsureBuiltInRolesAsync(
        string adminDescription,
        CancellationToken cancellationToken = default)
    {
        var admin = await EnsureStaticRoleAsync(
            AdminConstant.RoleName, "Administrator", adminDescription, isDefault: false, sort: 1, cancellationToken);
        var member = await EnsureStaticRoleAsync(
            MemberRoleName, "Member", "Default role automatically assigned to new users", isDefault: true, sort: 100, cancellationToken);
        return (admin, member);
    }

    /// <summary>删除角色：内置角色不可删，仍分配给未删除用户的角色不可删。</summary>
    /// <remarks>
    /// 只数还在的用户：删除用户是软删除，成员关系随用户保留（恢复时一并回来），
    /// 数进去的话角色就被一个界面上看不到、也无法改派的人永久卡住。已删除用户名下的成员关系由调用方随后撤销。
    /// </remarks>
    /// <exception cref="BusinessException">
    /// 内置角色（<see cref="RoleErrorCodes.StaticRoleCannotBeDeleted"/>），或仍有用户持有（<see cref="RoleErrorCodes.RoleStillAssigned"/>）。
    /// </exception>
    public async Task DeleteAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!role.CanBeDeleted())
        {
            throw new BusinessException(RoleErrorCodes.StaticRoleCannotBeDeleted, $"Built-in role '{role.Name}' cannot be deleted.")
                .WithData("Name", role.Name);
        }

        var userCount = await userRepository.CountAsync(u => u.Roles.Any(ur => ur.RoleId == role.Id), cancellationToken);
        if (userCount > 0)
        {
            throw new BusinessException(RoleErrorCodes.RoleStillAssigned,
                    $"Role '{role.Name}' still has {userCount} assigned user(s). Reassign them before deleting.")
                .WithData("Name", role.Name)
                .WithData("UserCount", userCount);
        }

        await roleRepository.DeleteAsync(role, cancellationToken);
    }

    private async Task<Role> EnsureStaticRoleAsync(
        string name,
        string displayName,
        string description,
        bool isDefault,
        int sort,
        CancellationToken cancellationToken)
    {
        // 查重要看见被软删除的行，理由同 CreateAsync
        Role? existing;
        using (dataFilter.Disable<ISoftDelete>())
        {
            existing = await roleRepository.GetFirstAsync(r => r.Name == name, cancellationToken: cancellationToken);
        }

        if (existing is { IsDeleted: true })
        {
            throw new InvalidOperationException(
                $"Built-in role '{name}' has been deleted and still holds its name; restore it through your data recovery procedure first.");
        }

        if (existing is not null)
        {
            return existing;
        }

        var role = new Role(name, displayName, description, isStatic: true, isDefault: isDefault, sort: sort);
        await roleRepository.InsertAsync(role, cancellationToken);
        logger.LogInformation("Built-in role created: {Name} (ID: {Id})", role.Name, role.Id);
        return role;
    }
}
