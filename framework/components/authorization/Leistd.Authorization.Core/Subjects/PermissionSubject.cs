namespace Leistd.Authorization.Subjects;

/// <summary>权限检查主体。</summary>
/// <param name="UserId">当前用户 ID。</param>
/// <param name="RoleIds">当前用户所属角色 ID 集合。</param>
/// <param name="IsSuperAdmin">是否是可绕过权限授予检查的超级管理员。</param>
public sealed record PermissionSubject(
    string UserId,
    IReadOnlyCollection<string> RoleIds,
    bool IsSuperAdmin);
