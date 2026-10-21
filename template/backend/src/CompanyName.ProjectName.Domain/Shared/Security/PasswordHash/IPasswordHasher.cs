namespace CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;

/// <summary>领域密码哈希端口。</summary>
public interface IPasswordHasher
{
    /// <summary>生成新的密码哈希。</summary>
    string HashPassword(string password);

    /// <summary>返回匹配结果及是否需要升级哈希参数。</summary>
    PasswordVerificationStatus VerifyPassword(string hashedPassword, string providedPassword);
}
