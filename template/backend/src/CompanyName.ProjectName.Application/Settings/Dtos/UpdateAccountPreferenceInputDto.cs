namespace CompanyName.ProjectName.Application.Settings.Dtos;

/// <summary>改本人的一项账户偏好。</summary>
/// <remarks>不由模型绑定产生：接口收的是 JSON 合并补丁，Api 按协议核对形状与字段名（<see cref="Constants.AccountPreferenceFields.SettingNames"/>）后构造本 DTO。</remarks>
public sealed record UpdateAccountPreferenceInputDto
{
    /// <summary>字段名（<c>language</c>、<c>timeZone</c>、<c>theme</c>）。</summary>
    public required string Field { get; init; }

    /// <summary>新值；<see langword="null"/> 清除本人的覆盖，继承组织或部署基线。</summary>
    public string? Value { get; init; }
}
