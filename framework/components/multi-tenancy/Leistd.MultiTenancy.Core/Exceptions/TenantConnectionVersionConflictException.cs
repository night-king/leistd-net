using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.Exceptions;

/// <summary>表示租户连接配置版本不匹配。</summary>
/// <remarks>
/// 写入必须携带预期版本；冲突时由调用方重新读取并决定如何合并。
/// </remarks>
public class TenantConnectionVersionConflictException : BusinessException
{
    /// <summary>构造异常。</summary>
    /// <param name="tenantId">目标租户。</param>
    /// <param name="expectedVersion">调用方预期的版本；<see langword="null"/> 表示预期该配置尚不存在。</param>
    /// <param name="actualVersion">实际版本；<see langword="null"/> 表示配置实际不存在。</param>
    /// <param name="innerException">预检失败时为 <see langword="null"/>；由数据库并发令牌拦下时为 <c>DbUpdateConcurrencyException</c>。</param>
    public TenantConnectionVersionConflictException(
        Guid tenantId,
        long? expectedVersion,
        long? actualVersion,
        Exception? innerException = null)
        : base(MultiTenancyErrorCodes.ConnectionVersionConflict,
            $"Tenant '{tenantId}' connection configuration version mismatch: " +
            $"expected {Describe(expectedVersion)} but found {Describe(actualVersion)}. " +
            "Re-read the configuration and retry.",
            innerException)
    {
        TenantId = tenantId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>目标租户标识。</summary>
    public Guid TenantId { get; }

    /// <summary>预期版本；<see langword="null"/> 表示预期配置不存在。</summary>
    public long? ExpectedVersion { get; }

    /// <summary>实际版本；<see langword="null"/> 表示配置不存在。</summary>
    public long? ActualVersion { get; }

    private static string Describe(long? version) =>
        version is { } value ? $"version {value}" : "no configuration";
}
