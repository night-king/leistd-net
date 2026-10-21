namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>用户批量操作的请求（对应 <c>POST /api/v1/users/batch/{动作}</c>）。</summary>
/// <param name="Ids">目标用户 Id，1 至 100 个；重复的只处理一次</param>
public sealed record BatchUsersDto(IReadOnlyList<Guid> Ids);
