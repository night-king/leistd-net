namespace Leistd.Authorization.Resource.Abstractions;

/// <summary>常用资源操作名称；业务可使用任意字符串操作。</summary>
public static class ResourceOperations
{
    /// <summary>查看单个资源实例。</summary>
    public const string Read = "Read";

    /// <summary>修改资源实例。</summary>
    public const string Update = "Update";

    /// <summary>删除资源实例。</summary>
    public const string Delete = "Delete";

    /// <summary>把资源实例分享给其他主体。</summary>
    public const string Share = "Share";
}

/// <summary>可被资源实例授权保护的资源，调用方据此省去手工传资源名与 Key。</summary>
/// <remarks>资源名建议使用稳定的复数业务名（如 <c>Orders</c>），资源 Key 使用主键的字符串形式。</remarks>
public interface IAuthorizableResource
{
    /// <summary>资源类型名称，同一类资源在 ACL 中共享该名称。</summary>
    string ResourceName { get; }

    /// <summary>资源实例 Key，在同一资源类型内唯一。</summary>
    string ResourceKey { get; }
}

/// <summary>资源实例授权的业务入口：判定当前主体能否对一个已加载的资源实例执行指定操作。</summary>
/// <remarks>
/// <para>实现以当前主体调用官方授权管线（<c>Leistd.Authorization.Resource.AspNetCore</c>）：领域规则写成官方的
/// <c>AuthorizationHandler&lt;OperationAuthorizationRequirement, TResource&gt;</c>，资源 ACL 与超级管理员由组件的处理器判定。
/// 任一来源拒绝即拒绝，否则任一来源允许即允许，全部无结论时拒绝；没有经过认证的当前主体一律拒绝。</para>
/// <para>只判定已定位实例能否操作，不负责哪些实例可见（见 <c>Leistd.Authorization.DataScope.Core</c>）。
/// 官方授权管线不接收取消令牌，入口也不带。</para>
/// </remarks>
public interface IResourceAuthorizationService
{
    /// <summary>判定当前主体能否对指定资源实例执行指定操作；资源没有实现 <see cref="IAuthorizableResource"/> 时显式给出资源名与 Key。</summary>
    Task<bool> IsGrantedAsync<TResource>(
        TResource resource,
        string resourceName,
        string resourceKey,
        string operation);

    /// <summary>判定当前主体能否对实现了 <see cref="IAuthorizableResource"/> 的资源执行指定操作。</summary>
    Task<bool> IsGrantedAsync<TResource>(TResource resource, string operation)
        where TResource : IAuthorizableResource;
}
