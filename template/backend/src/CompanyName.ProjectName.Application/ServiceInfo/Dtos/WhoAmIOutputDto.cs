namespace CompanyName.ProjectName.Application.ServiceInfo.Dtos;

/// <summary>当前调用身份。</summary>
/// <param name="UserId">当前用户 Id（服务间调用时来自已验证的交换令牌）</param>
/// <param name="Username">当前用户名</param>
/// <param name="ClientId">令牌中的调用方客户端 Id</param>
public sealed record WhoAmIOutputDto(Guid? UserId, string? Username, string? ClientId);
