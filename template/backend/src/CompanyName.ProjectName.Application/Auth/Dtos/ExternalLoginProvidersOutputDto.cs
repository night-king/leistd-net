namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>部署已配置的外部登录提供商。</summary>
public sealed record ExternalLoginProvidersOutputDto
{
    /// <summary>提供商标识（<c>github</c>、<c>google</c>），按名称排序。</summary>
    public required IReadOnlyList<string> Providers { get; init; }
}
