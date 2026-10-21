namespace CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;

/// <summary>口令匹配及哈希参数升级的判定。</summary>
public enum PasswordVerificationStatus
{
    Failed,
    Succeeded,
    RehashNeeded
}
