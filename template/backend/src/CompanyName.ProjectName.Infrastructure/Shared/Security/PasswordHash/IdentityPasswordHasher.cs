using CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;
using Microsoft.AspNetCore.Identity;

namespace CompanyName.ProjectName.Infrastructure.Shared.Security.PasswordHash;

/// <summary>使用 Identity 原生格式生成和验证密码哈希。</summary>
public sealed class IdentityPasswordHasher(IPasswordHasher<object> hasher) : IPasswordHasher
{
    private static readonly object HashingContext = new();

    public string HashPassword(string password) => hasher.HashPassword(HashingContext, password);

    public PasswordVerificationStatus VerifyPassword(string hashedPassword, string providedPassword)
    {
        try
        {
            return hasher.VerifyHashedPassword(HashingContext, hashedPassword, providedPassword) switch
            {
                PasswordVerificationResult.Success => PasswordVerificationStatus.Succeeded,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationStatus.RehashNeeded,
                _ => PasswordVerificationStatus.Failed
            };
        }
        catch (FormatException)
        {
            return PasswordVerificationStatus.Failed;
        }
    }
}
