namespace Leistd.DependencyInjection.Abstractions;

/// <summary>向服务注册回调公开当前描述符的类型信息。</summary>
public interface IOnServiceRegisteredContext
{
    /// <summary>注册的服务类型。</summary>
    Type ServiceType { get; }

    /// <summary>实现类型；工厂委托注册时为 <see langword="null"/>。</summary>
    /// <remarks>回调对工厂委托注册同样会被调用；判定依赖实现类型的回调须自行处理 <see langword="null"/>。</remarks>
    Type? ImplementationType { get; }

    /// <summary>键控注册的服务键；非键控注册时为 <see langword="null"/>。</summary>
    /// <remarks>只按 <see cref="ServiceType"/> 判定的回调会同时命中键控与非键控注册；织入器保留键控形态。</remarks>
    object? ServiceKey { get; }

    /// <summary>扩展数据，供上层集成组件挂载自定义信息（如拦截器列表）。</summary>
    IDictionary<string, object?> Items { get; }
}
