namespace Leistd.Settings.Resolution;

/// <summary>按层级解析设置的当前生效值。</summary>
/// <remarks>
/// <para>回落顺序：用户级 → 租户级 → 代码默认值。宿主视角走租户级那一层（<c>TenantId</c> 为 <see langword="null"/> 的行）。</para>
/// <para>实现为 Scoped，按当前租户与当前用户缓存各层原始值：同一作用域里每个租户、每个用户只查一次库，切换租户读到目标租户的值；
/// 写入后缓存作废，其他请求写入的变更在下一请求可见。同一实例不支持并行调用。</para>
/// <para>进程级设置（<c>SettingScopes.Host</c>）不走回落链：宿主上下文下读宿主那一行，没有值才用代码默认值；
/// 租户上下文下不可达，单项读取抛 <see cref="Exceptions.HostScopeUnavailableException"/>，不返回代码默认值。</para>
/// </remarks>
public interface ISettingProvider
{
    /// <summary>读取设置的当前生效值。</summary>
    /// <param name="name">设置名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.UndefinedSettingException">名称未定义。</exception>
    /// <exception cref="Exceptions.HostScopeUnavailableException">
    /// 进程级设置在当前上下文不可达（非宿主上下文）。
    /// </exception>
    Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>读取指定用户在当前租户下的生效值：该用户的覆盖 → 租户级 → 代码默认值。</summary>
    /// <remarks>用于按他人偏好判断（如按收件人偏好决定是否投递）。进程级设置与 <see cref="GetOrNullAsync"/> 相同。</remarks>
    /// <param name="name">设置名称。</param>
    /// <param name="userId">目标用户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.UndefinedSettingException">名称未定义。</exception>
    /// <exception cref="Exceptions.HostScopeUnavailableException">进程级设置在当前上下文不可达。</exception>
    Task<string?> GetOrNullForUserAsync(string name, string userId, CancellationToken cancellationToken = default);

    /// <summary>读取设置并转换为 <typeparamref name="T"/>；值为空时返回 <c>default</c>。</summary>
    /// <typeparam name="T">目标类型，支持基元类型、枚举与带 <c>TypeConverter</c> 的类型。</typeparam>
    /// <param name="name">设置名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Exceptions.UndefinedSettingException">名称未定义。</exception>
    /// <exception cref="Exceptions.HostScopeUnavailableException">
    /// 进程级设置在当前上下文不可达；<c>default</c> 只表示"值为空"，不表示"读不到"。
    /// </exception>
    Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default);

    /// <summary>读取当前上下文可访问的全部设置的当前生效值。</summary>
    /// <remarks>租户上下文下不包含进程级设置，也不因此抛出。</remarks>
    /// <param name="visibleToClientsOnly">只返回标记为可下发客户端的设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
        bool visibleToClientsOnly = false,
        CancellationToken cancellationToken = default);
}
