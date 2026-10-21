using CompanyName.ProjectName.Application.Roles.Dtos;
using CompanyName.ProjectName.Domain.Users.Entities;
using Mapster;

namespace CompanyName.ProjectName.Application.Roles.Mappings;

/// <summary>角色映射配置。</summary>
/// <remarks>
/// 用户数与权限数不在角色实体上，需要由调用方额外查询后经 <c>MapContext</c> 传入——
/// 与 <c>UserMappings</c> 解析角色名的做法保持同一形态，不在应用服务里手工 new DTO。
/// </remarks>
public class RoleMappings : IRegister
{
    /// <summary>MapContext 参数名：角色 Id → 关联用户数。</summary>
    public const string UserCountsKey = "RoleUserCounts";

    /// <summary>MapContext 参数名：角色 Id → 授予数。</summary>
    public const string PermissionCountsKey = "RolePermissionCounts";

    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Role, RoleBriefOutputDto>();

        config.NewConfig<Role, RoleOutputDto>()
            .Map(dest => dest.UserCount, src => ResolveCount(UserCountsKey, src.Id))
            .Map(dest => dest.PermissionCount, src => ResolveCount(PermissionCountsKey, src.Id));
    }

    private static int ResolveCount(string parameterKey, Guid roleId)
    {
        if (MapContext.Current?.Parameters.TryGetValue(parameterKey, out var value) == true &&
            value is IReadOnlyDictionary<Guid, int> counts &&
            counts.TryGetValue(roleId, out var count))
        {
            return count;
        }

        return 0;
    }
}
