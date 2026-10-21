namespace Leistd.Data.Connections;

/// <summary>提供约束当前工作单元数据归属的环境标识。</summary>
/// <remarks>
/// 工作单元用它判断环境是否在存活期内发生了切换——<b>只看物理连接目标不够</b>：
/// 共享库形态下两个不同的逻辑归属会解析到同一个连接串。
/// 取值必须是<b>纯内存读取</b>，不做 I/O——每次获取 DbContext 都会读它。
/// 键的语义对工作单元层不透明；未注册实现时为 <see langword="null"/>，此时只按物理连接目标约束。
/// </remarks>
public interface IConnectionAffinityProvider
{
    /// <summary>获取当前环境的归属标识；无归属时为 <see langword="null"/>。</summary>
    string? AffinityKey { get; }
}
