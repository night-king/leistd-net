using Leistd.Settings.Dtos;
using Leistd.Settings.Definitions;
using Leistd.Settings.Resolution;

namespace Leistd.Settings.Management;

/// <summary>设置页的读写用例：按层级读取覆盖值，写当前用户的偏好或当前租户（宿主上下文下为宿主）的值。</summary>
/// <remarks>
/// <para>只处理标记为 <see cref="ISettingDefinition.IsVisibleToClients"/> 的设置。</para>
/// <para>不做权限检查——谁能写租户值由端点的授权策略决定。层级越权（租户上下文写进程级设置）在这里拒绝。</para>
/// <para>业务代码消费设置值时注入 <see cref="ISettingProvider"/>，不经本用例。</para>
/// </remarks>
public interface ISettingManagementService
{
    /// <summary>获取当前上下文可见设置在各层级上的覆盖值。</summary>
    /// <remarks>租户上下文不含进程级设置。机密设置不下发任何值，只报是否已设置。</remarks>
    Task<IReadOnlyList<SettingOutputDto>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>写入当前用户自己的偏好；值为 <see langword="null"/> 时恢复为上一层。</summary>
    /// <param name="input">要写入的设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ExceptionHandling.BusinessException">当前身份不是用户，或设置不存在、不对客户端开放。</exception>
    Task SetForCurrentUserAsync(SetSettingInputDto input, CancellationToken cancellationToken = default);

    /// <summary>写入当前租户的默认值；进程级设置写入宿主那一份，只允许在宿主上下文。</summary>
    /// <param name="input">要写入的设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ExceptionHandling.BusinessException">在租户上下文写进程级设置，或设置不存在、不对客户端开放。</exception>
    Task SetForCurrentTenantAsync(SetSettingInputDto input, CancellationToken cancellationToken = default);
}
