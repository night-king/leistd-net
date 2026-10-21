#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Users.Options;

/// <summary>首次启动时创建的超级管理员配置。</summary>
/// <remarks>仅创建初始管理员时要求有效密码；已有管理员时不要求。口令由部署配置或 user-secrets 注入。</remarks>
public class DefaultAdminOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "DefaultAdmin";

    /// <summary>用户名。</summary>
    public string Username { get; set; } = "admin";

    /// <summary>密码。无默认值，创建管理员时必须由配置提供。</summary>
    public string? Password { get; set; }

    /// <summary>邮箱。</summary>
    public string Email { get; set; } = "admin@companyname-projectname.com";

    /// <summary>显示名称。</summary>
    public string? DisplayName { get; set; }
}
#endif
