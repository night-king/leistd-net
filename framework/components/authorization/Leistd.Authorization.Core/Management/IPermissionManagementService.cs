using Leistd.Authorization.Dtos;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Subjects;

namespace Leistd.Authorization.Management;

/// <summary>权限管理用例：当前用户的有效权限、定义树、主体授予的读取与整体替换。</summary>
/// <remarks>
/// <para>全部按当前侧别过滤（<see cref="IPermissionDefinitionManager.IsAvailableOn"/>），与权限检查器同一判据。</para>
/// <para>不做权限检查——谁能读定义、谁能改谁的授予由端点的授权策略决定。
/// 主体是否存在与显示名经 <see cref="IPermissionSubjectDirectory"/> 由宿主回答。</para>
/// </remarks>
public interface IPermissionManagementService
{
    /// <summary>获取当前用户的有效权限；超级管理员返回全部可用权限。</summary>
    /// <remarks>
    /// 当前身份不在本权限主体空间里时返回空集合而不抛 401（调用方已认证，401 会让客户端反复重新登录）。
    /// </remarks>
    Task<CurrentPermissionsOutputDto> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>获取权限定义树，剔除停用项、当前侧别不可用的项与因此变空的分组。</summary>
    Task<IReadOnlyList<PermissionDefinitionGroupOutputDto>> GetDefinitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>获取某个主体的直接授予状态与并发版本。</summary>
    /// <param name="providerName">授予对象类型。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ExceptionHandling.BusinessException">主体不存在。</exception>
    Task<PermissionGrantsOutputDto> GetGrantsAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>带版本整体替换某个主体的授予，成功后发布 <see cref="Events.PermissionGrantsReplacedEvent"/>。</summary>
    /// <param name="providerName">授予对象类型。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="input">目标权限集合与期望版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ExceptionHandling.BusinessException">主体不存在。</exception>
    /// <exception cref="Exceptions.PermissionGrantConcurrencyException">版本不一致。</exception>
    /// <exception cref="Exceptions.UndefinedPermissionException">包含未定义或已禁用的权限。</exception>
    Task<PermissionGrantsOutputDto> ReplaceGrantsAsync(
        string providerName,
        string providerKey,
        ReplacePermissionGrantsInputDto input,
        CancellationToken cancellationToken = default);
}
