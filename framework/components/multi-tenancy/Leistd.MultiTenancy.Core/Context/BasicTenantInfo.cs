namespace Leistd.MultiTenancy.Context;

/// <summary>表示当前租户上下文的不可变快照。</summary>
/// <param name="TenantId">租户 Id，<see langword="null"/> 表示宿主。</param>
/// <param name="Name">租户名称，仅用于展示与日志。</param>
public sealed record BasicTenantInfo(Guid? TenantId, string? Name = null);
