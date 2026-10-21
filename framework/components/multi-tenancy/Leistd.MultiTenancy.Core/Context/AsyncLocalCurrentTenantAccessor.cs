namespace Leistd.MultiTenancy.Context;

/// <summary>使用 <see cref="AsyncLocal{T}"/> 保存当前租户快照。</summary>
/// <remarks>
/// 状态挂在 ExecutionContext 上：跨 <c>await</c>、跨 <c>Task.Run</c>、跨事件处理器的新 DI Scope 均自然流动。
/// 以静态单例注册，保证无 DI 场景（如拦截器内部）也能取到同一实例。
/// </remarks>
public sealed class AsyncLocalCurrentTenantAccessor : ICurrentTenantAccessor
{
    /// <summary>全局实例。</summary>
    public static AsyncLocalCurrentTenantAccessor Instance { get; } = new();

    private readonly AsyncLocal<BasicTenantInfo?> _currentScope = new();

    private AsyncLocalCurrentTenantAccessor()
    {
    }

    /// <inheritdoc />
    public BasicTenantInfo? Current
    {
        get => _currentScope.Value;
        set => _currentScope.Value = value;
    }
}
