using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;

namespace Leistd.Settings.Stores;

/// <summary>设置值的持久化契约，只负责按层级读写原始字符串。</summary>
/// <remarks>
/// 不做定义校验与层级回落——那是 <see cref="ISettingManager"/> 与 <see cref="ISettingProvider"/> 的职责。
/// 租户隔离由实现所在的数据过滤器承担，本契约不带租户参数。
/// <para>默认写入入口在独立的事务型工作单元里调用本存储的读与写：实现须经当前工作单元读写并参与它的事务
/// （EF 实现经 <c>IDbContextProvider&lt;TDbContext&gt;</c> 取上下文），读取须反映数据库的当前值，不复用已跟踪的实体或自带缓存。
/// 不满足时，写入失败不会整体回滚，按租户串行的跨设置校验也可能读到旧值。</para>
/// </remarks>
public interface ISettingStore
{
    /// <summary>当前上下文能否读写宿主层（<see cref="SettingScopes.Host"/>）。</summary>
    /// <remarks>不可达时读到的空值不代表“没设过”，调用方必须先判断这一项。</remarks>
    bool CanAccessHostScope { get; }

    /// <summary>读取当前租户下某一层级的全部设置值。</summary>
    /// <param name="scope">要读取的层级。</param>
    /// <param name="userId">用户级时的用户标识；租户级传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>移除当前租户下的全部设置值，含各用户在该租户内的偏好。</summary>
    /// <remarks>供永久废弃租户时清理。按当前租户隐式限定：在宿主上下文调用会清掉宿主的设置。</remarks>
    Task RemoveAllAsync(CancellationToken cancellationToken = default);

    /// <summary>写入设置值；<paramref name="value"/> 为 <see langword="null"/> 时删除该层级的值。</summary>
    /// <remarks>
    /// <para>
    /// 同一层级同一名称的并发写入收敛到存储实际写入的先后：后落库者的值保留，而不是按请求到达顺序。
    /// 删除是“清空本键”：值本就不存在时成功；删除期间被并发重建的行也一并删掉。
    /// </para>
    /// <para>
    /// 不承诺任意竞争下都能成功：实现无法收敛的冲突原样抛出，取消照常抛出。
    /// </para>
    /// </remarks>
    /// <param name="name">设置名称。</param>
    /// <param name="value">设置值；<see langword="null"/> 表示清除，使其回落到下一层。</param>
    /// <param name="scope">写入的层级。</param>
    /// <param name="userId">用户级时的用户标识；租户级传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default);
}
