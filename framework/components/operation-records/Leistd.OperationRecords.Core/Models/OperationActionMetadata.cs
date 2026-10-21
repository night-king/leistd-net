namespace Leistd.OperationRecords.Models;

/// <summary>动作本身的严重度，与结果无关（结果见 <see cref="OperationRecordOutcome"/>）。</summary>
public enum OperationSeverity
{
    /// <summary>常规变更。</summary>
    Info,

    /// <summary>值得留意，但不必告警。</summary>
    Notice,

    /// <summary>改变了"谁能做什么"或"谁是谁"，需要能直接接告警。</summary>
    /// <remarks>如权限授予变更、角色替换、关闭 MFA、开始模拟登录。</remarks>
    Critical
}

/// <summary>一条操作记录能被谁看见。</summary>
/// <remarks>
/// <para>可见性是安全边界：多租户下租户管理员直接阅读这张表，层级判错即越权。</para>
/// <para>技术说明与链路标识只对宿主开放。</para>
/// </remarks>
public enum OperationVisibility
{
    /// <summary>该租户的管理员与宿主都能看见。</summary>
    Tenant,

    /// <summary>仅宿主可见。</summary>
    Host,

    /// <summary>仅操作人本人可见，宿主与租户管理员另按上面两档判定。</summary>
    /// <remarks>自助改密、绑定 MFA 这类"只关乎本人"的动作用它。</remarks>
    Actor
}
