using System.Data.Common;
using System.Text.RegularExpressions;
using Leistd.Data.Connections;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 连接名的归一化与校验。名字来自 DbContext 的 [ConnectionStringName] 与管理员输入，
// 统一小写后存储与查询，避免大小写差异表现为“登记过却解析不到”。
internal static class TenantConnectionNames
{
    // 归一化之后才匹配，所以模式里只有小写
    private static readonly Regex Pattern =
        new(TenantConnectionConfiguration.NamePattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>默认连接名归一化后的值。</summary>
    public static readonly string Default = Normalize(ConnectionStringNames.Default);

    // 代码里写死的名字（[ConnectionStringName]）不合法是编程错误
    public static string Normalize(string name)
        => TryNormalize(name) ?? throw new ArgumentException(
            $"Connection name '{name}' is invalid. After lowercasing it must match {TenantConnectionConfiguration.NamePattern}.",
            nameof(name));

    // 管理员填的、URL 里带来的名字不合法是调用方能改对的输入错误：400 而不是 500
    public static string NormalizeInput(string? name)
        => TryNormalize(name) ?? throw new BusinessException(MultiTenancyErrorCodes.ConnectionNameInvalid,
                $"Connection name '{name}' is invalid. After lowercasing it must match {TenantConnectionConfiguration.NamePattern}.")
            .WithData("Name", name)
            .WithData("Pattern", TenantConnectionConfiguration.NamePattern);

    private static string? TryNormalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = name.Trim().ToLowerInvariant();
        return Pattern.IsMatch(normalized) ? normalized : null;
    }
}

// 连接串的写入前校验：空、超长与非“键=值;”语法都是输入错误（400）。
// 语法用 BCL 的 DbConnectionStringBuilder 判断，不引用数据库驱动，也不验证可达性。
// 异常消息只描述规则，不回显连接串（含数据库口令）。
internal static class TenantConnectionStrings
{
    public static string NormalizeInput(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Invalid(
                "A tenant connection requires a non-empty connection string. To stop routing this name, remove the registration instead.");
        }

        var normalized = connectionString.Trim();
        if (normalized.Length > TenantConnectionConfiguration.MaxConnectionStringLength)
        {
            throw Invalid($"The connection string exceeds {TenantConnectionConfiguration.MaxConnectionStringLength} characters.");
        }

        try
        {
            _ = new DbConnectionStringBuilder { ConnectionString = normalized };
        }
        catch (ArgumentException)
        {
            throw Invalid(
                "The connection string is malformed; it must be semicolon-separated key=value pairs. The value is not echoed back because it carries credentials.");
        }

        return normalized;
    }

    private static BusinessException Invalid(string message)
        => new BusinessException(MultiTenancyErrorCodes.ConnectionStringInvalid, message);
}
