namespace Leistd.MultiTenancy.Stores;

/// <summary>归一化租户名称以支持大小写不敏感查询。</summary>
public interface ITenantNormalizer
{
    /// <summary>归一化租户名称；<see langword="null"/> 原样返回。</summary>
    string? NormalizeName(string? name);
}

/// <summary>使用 Unicode 规范化和不变区域性大写转换名称。</summary>
public class UpperInvariantTenantNormalizer : ITenantNormalizer
{
    /// <inheritdoc />
    public string? NormalizeName(string? name) => name?.Normalize().ToUpperInvariant();
}
