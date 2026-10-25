#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;
#endif
using CompanyName.ProjectName.Application.Users.Avatars;
using CompanyName.ProjectName.Application.Users.Dtos;
using CompanyName.ProjectName.Domain.Users.Entities;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.ValueObjects;
#endif
using Mapster;

namespace CompanyName.ProjectName.Application.Users.Mappings;

/// <summary>用户映射配置。</summary>
public class UserMappings : IRegister
{
#if (LocalIdentity)
    /// <summary>MapContext 参数名：调用方已解析好的角色名称。</summary>
    /// <remarks>
    /// 写路径（注册、首次外部登录）在同一事务边界内刚分配完角色，成员关系尚未落库，
    /// 按名称回查查不到。传本键即可绕开回查——调用方本来就知道它刚分配了哪些角色。
    /// </remarks>
    public const string RoleNamesKey = "RoleNames";

    /// <summary>MapContext 参数名：当前时刻，用来判定锁定是否仍在生效。</summary>
    /// <remarks>过期的临时锁定在库里仍是 <c>IsLocked</c>（下次登录失败或成功时才清），只看字段会误报"已锁定"。</remarks>
    public const string NowKey = "Now";
#endif

    /// <summary>MapContext 参数名：角色实体，按用户当前持有的角色 Id（<see cref="User.GetRoleIds"/>）取用。</summary>
    public const string RolesKey = "Roles";

    public void Register(TypeAdapterConfig config)
    {
#if (LocalIdentity)
        config.NewConfig<User, UserOutputDto>()
            .Map(dest => dest.Roles, src => ResolveRoles(src))
            .Map(dest => dest.Avatar, src => AvatarUrls.For(src.Id, src.Avatar))
            .Map(dest => dest.IsEmailVerified, src => src.EmailConfirmed)
            .Map(dest => dest.IsTwoFactorEnabled, src => src.TwoFactorEnabled)
            .Map(dest => dest.HasPasskeys, src => src.PasskeyCount > 0)
            .Map(dest => dest.HasPassword, src => src.HasLocalPassword)
            .Ignore(dest => dest.TwoFactorSetupRequired);
#endif

        config.NewConfig<User, UserManagementOutputDto>()
            .Map(dest => dest.Avatar, src => AvatarUrls.For(src.Id, src.Avatar))
#if (LocalIdentity)
            .Map(dest => dest.IsEmailVerified, src => src.EmailConfirmed)
            .Map(dest => dest.LastLoginTime, src => src.LastLogin == null ? (DateTime?)null : src.LastLogin.Time)
            .Map(dest => dest.IsLockedOut, src => ResolveIsLockedOut(src))
            .Map(dest => dest.LockoutEnd, src => ResolveIsLockedOut(src) ? src.Lockout.End : null)
            .Map(dest => dest.IsTwoFactorEnabled, src => src.HasSecondFactor)
#endif
            // 角色实体交给 Mapster 按同一份配置映射成 RoleBriefOutputDto（RoleMappings 登记的规则在这里生效）
            .Map(dest => dest.Roles, src => ResolveRoleEntities(src));
    }

#if (LocalIdentity)
    // 优先用调用方直接给出的角色名；没有时按角色实体取
    private static string[] ResolveRoles(User source)
    {
        if (MapContext.Current?.Parameters.TryGetValue(RoleNamesKey, out var roleNamesObj) == true &&
            roleNamesObj is IEnumerable<string> roleNames)
        {
            return [.. roleNames];
        }

        return [.. ResolveRoleEntities(source).Select(role => role.Name)];
    }

    // 调用方没给时刻时按库里的字段报：宁可多报一个已过期的锁定，也不要漏报一个生效中的
    private static bool ResolveIsLockedOut(User source)
    {
        if (MapContext.Current?.Parameters.TryGetValue(NowKey, out var nowObj) == true && nowObj is DateTime now)
        {
            return source.GetAccessStatus(now) == UserAccessStatus.LockedOut;
        }

        return source.Lockout.IsLocked;
    }
#endif

    private static List<Role> ResolveRoleEntities(User source)
    {
        if (MapContext.Current?.Parameters.TryGetValue(RolesKey, out var rolesObj) == true &&
            rolesObj is List<Role> roles)
        {
            return [.. source.GetRoleIds().Join(roles, roleId => roleId, role => role.Id, (_, role) => role)];
        }

        return [];
    }
}
