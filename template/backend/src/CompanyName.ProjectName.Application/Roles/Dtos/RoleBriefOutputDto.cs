namespace CompanyName.ProjectName.Application.Roles.Dtos;

/// <summary>角色简要信息，用于用户列表与下拉选择。</summary>
public record RoleBriefOutputDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
}
