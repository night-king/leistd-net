using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Leistd.Authorization.Resource.AspNetCore.Operations;

/// <summary>对一个资源实例执行某个操作的授权要求：官方 <see cref="OperationAuthorizationRequirement"/> 加上资源的 ACL 定位。</summary>
/// <remarks>
/// 业务规则写成官方的 <c>AuthorizationHandler&lt;OperationAuthorizationRequirement, TResource&gt;</c> 即可匹配本要求，
/// 按 <see cref="OperationAuthorizationRequirement.Name"/> 区分操作；资源名与资源 Key 供 ACL 处理器查询授予。
/// </remarks>
public sealed class ResourceOperationRequirement : OperationAuthorizationRequirement
{
    /// <summary>创建要求。</summary>
    /// <param name="resourceName">资源类型名称，同一类资源在 ACL 中共享。</param>
    /// <param name="resourceKey">资源实例 Key。</param>
    /// <param name="operation">操作名，常用值见 <c>ResourceOperations</c>。</param>
    public ResourceOperationRequirement(string resourceName, string resourceKey, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        ResourceName = resourceName;
        ResourceKey = resourceKey;
        Name = operation;
    }

    /// <summary>资源类型名称。</summary>
    public string ResourceName { get; }

    /// <summary>资源实例 Key。</summary>
    public string ResourceKey { get; }
}
