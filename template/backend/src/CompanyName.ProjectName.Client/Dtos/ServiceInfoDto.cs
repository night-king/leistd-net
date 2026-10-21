namespace CompanyName.ProjectName.Client.Dtos;

/// <summary>服务基础信息（对应 <c>GET /api/v1/service-info</c>）。</summary>
/// <param name="Service">服务名</param>
/// <param name="Version">服务版本</param>
/// <param name="ServerTime">服务器当前时间（UTC）</param>
public sealed record ServiceInfoDto(string Service, string Version, DateTimeOffset ServerTime);
