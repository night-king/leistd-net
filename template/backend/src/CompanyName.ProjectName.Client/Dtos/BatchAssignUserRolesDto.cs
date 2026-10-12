namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>批量追加角色的请求（对应 <c>POST /api/v1/users/batch/assign-roles</c>）；已有角色保留。</summary>
/// <param name="Ids">目标用户 Id，1 至 100 个；重复的只处理一次</param>
/// <param name="RoleIds">要追加的角色 Id，1 至 20 个</param>
public sealed record BatchAssignUserRolesDto(IReadOnlyList<Guid> Ids, IReadOnlyList<Guid> RoleIds);
