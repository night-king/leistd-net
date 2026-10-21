namespace Leistd.MultiTenancy.Context;

/// <summary>读写当前租户上下文快照。</summary>
/// <remarks>
/// 三态语义：<c>Current == null</c> 表示从未显式设置（等同宿主视角）；
/// <c>Current.TenantId == null</c> 表示显式切换到宿主；非空表示显式租户。
/// </remarks>
public interface ICurrentTenantAccessor
{
    /// <summary>当前租户快照。</summary>
    BasicTenantInfo? Current { get; set; }
}
