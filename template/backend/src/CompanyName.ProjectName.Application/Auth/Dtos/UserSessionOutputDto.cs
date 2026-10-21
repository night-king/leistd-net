#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>一个登录中的会话（登录设备）。</summary>
public record UserSessionOutputDto
{
    /// <summary>会话 Id</summary>
    public Guid Id { get; init; }

    /// <summary>登录时间</summary>
    public DateTime CreationTime { get; init; }

    /// <summary>最近活跃时间（按分钟节流，不是精确到每个请求）</summary>
    public DateTime LastSeenTime { get; init; }

    /// <summary>最近一次请求的 IP</summary>
    public string? IpAddress { get; init; }

    /// <summary>登录时的 User-Agent 原文；由界面归纳成"浏览器 · 系统"</summary>
    public string? UserAgent { get; init; }

    /// <summary>模拟登录建立的会话：发起人名称；普通登录为 null</summary>
    public string? ImpersonatorName { get; init; }

    /// <summary>是否就是发出本次请求的会话</summary>
    public bool IsCurrent { get; init; }
}
#endif
