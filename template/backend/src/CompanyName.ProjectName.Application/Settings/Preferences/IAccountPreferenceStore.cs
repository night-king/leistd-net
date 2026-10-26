using CompanyName.ProjectName.Application.Settings.Dtos;

namespace CompanyName.ProjectName.Application.Settings.Preferences;

/// <summary>账户偏好的读写：服务端按用户取语言、时区与外观都经这里，不直接读设置。</summary>
/// <remarks>
/// 偏好的持有方（Identity、Standalone）读写本服务的用户层设置；资源服务向 Identity 回源并缓存，写入以本人委托转交，本服务不存这三项。
/// 读取返回 <see langword="null"/> 表示暂时读不到（只有资源服务在没有旧表示时遇到上游故障才会出现），
/// 调用方按各自现有的「取不到偏好」处理，不要拿本地默认值冒充。
/// </remarks>
public interface IAccountPreferenceStore
{
    /// <summary>当前用户的完整表示，供 <c>/me</c> 使用；没有当前用户时为 <see langword="null"/>。</summary>
    /// <remarks>资源服务每次都向 Identity 做条件重验证，不受缓存新鲜期限制：页签切回时要看到别处刚改的值。</remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccountPreferencesOutputDto?> GetForCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>当前租户环境里指定用户的完整表示，供服务端产出文本（收件人的语言与时区）使用。</summary>
    /// <remarks>资源服务在新鲜期内直接用缓存，同一用户的并发读取合并为一次回源。</remarks>
    /// <param name="userId">用户 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccountPreferencesOutputDto?> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>写入当前用户本人的一项偏好，返回写入提交后的完整表示。</summary>
    /// <param name="userId">当前用户；只能写本人。</param>
    /// <param name="field">字段名，取自 <see cref="Constants.AccountPreferenceFields.SettingNames"/>。</param>
    /// <param name="value">新值；<see langword="null"/> 清除本人的覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccountPreferencesOutputDto> SetForCurrentUserAsync(
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default);
}
