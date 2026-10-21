using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.Exceptions;

/// <summary>表示租户名称已被占用。</summary>
public class DuplicateTenantNameException : BusinessException
{
    /// <summary>构造异常。</summary>
    /// <param name="normalizedName">冲突的归一化名称。</param>
    public DuplicateTenantNameException(string normalizedName)
        : base(MultiTenancyErrorCodes.DuplicateName, $"Tenant name already exists: {normalizedName}")
    {
        NormalizedName = normalizedName;
        WithData("Name", normalizedName);
    }

    /// <summary>冲突的归一化名称。</summary>
    public string NormalizedName { get; }
}
