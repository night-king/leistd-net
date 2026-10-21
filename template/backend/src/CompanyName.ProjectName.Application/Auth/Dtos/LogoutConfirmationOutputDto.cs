namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>依赖方发起退出、需要用户确认时，确认页所需的信息。</summary>
public record LogoutConfirmationOutputDto
{
    /// <summary>
    /// 确认凭据仍然有效且属于当前会话。为 <see langword="false"/> 时（过期、已换用户或重新登录）其余字段为空，
    /// 确认页应提示重新从应用发起退出。
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>发起退出的应用名称；退出请求未标明客户端时为空。</summary>
    public string? ApplicationName { get; init; }

    /// <summary>确认表单须携带的官方防伪令牌字段名。</summary>
    public string? AntiforgeryFieldName { get; init; }

    /// <summary>确认表单须携带的官方防伪令牌（配套的 Cookie 随本响应下发）。</summary>
    public string? AntiforgeryToken { get; init; }
}
