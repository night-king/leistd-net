using Leistd.ExceptionHandling;
using Leistd.Authorization.Resource.Errors;

namespace Leistd.Authorization.Resource.Exceptions;

/// <summary>资源授权主体或效果无效，由调用方修正输入。</summary>
/// <param name="resourceName">资源类型名。</param>
/// <param name="resourceKey">资源实例 Key。</param>
/// <param name="reason">具体原因，进入异常消息。</param>
public sealed class InvalidResourceGrantException(string resourceName, string resourceKey, string reason)
    : BusinessException(
        ResourceAuthorizationErrorCodes.InvalidGrant,
        $"Resource grant on '{resourceName}/{resourceKey}' is invalid: {reason}")
{
    /// <summary>资源类型名。</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>资源实例 Key。</summary>
    public string ResourceKey { get; } = resourceKey;

    /// <summary>具体原因。</summary>
    public string Reason { get; } = reason;
}
