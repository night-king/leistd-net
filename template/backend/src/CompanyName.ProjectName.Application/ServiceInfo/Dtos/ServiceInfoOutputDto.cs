namespace CompanyName.ProjectName.Application.ServiceInfo.Dtos;

/// <summary>服务基础信息。</summary>
/// <param name="Service">服务名（程序集名）</param>
/// <param name="Version">程序集版本</param>
/// <param name="ServerTime">服务器当前时间（UTC）</param>
public sealed record ServiceInfoOutputDto(string Service, string Version, DateTimeOffset ServerTime);
