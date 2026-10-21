using Leistd.ExceptionHandling;
using Leistd.Authorization.Resource.Errors;

namespace Leistd.Authorization.Resource.Exceptions;

/// <summary>资源 ACL 版本冲突；调用方应重新加载后再保存。</summary>
public sealed class ResourceGrantConcurrencyException(
    string resourceName,
    string resourceKey,
    long expectedVersion,
    long actualVersion)
    : BusinessException(
        ResourceAuthorizationErrorCodes.ConcurrencyConflict,
        $"Resource ACL of '{resourceName}/{resourceKey}' was modified by someone else " +
        $"(expected version {expectedVersion}, actual {actualVersion}).")
{
    /// <summary>发生冲突的资源类型名。</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>发生冲突的资源实例 Key。</summary>
    public string ResourceKey { get; } = resourceKey;

    /// <summary>调用方回传的期望版本。</summary>
    public long ExpectedVersion { get; } = expectedVersion;

    /// <summary>存储中的真实版本，取自冲突发生后的重新读取。</summary>
    public long ActualVersion { get; } = actualVersion;
}
