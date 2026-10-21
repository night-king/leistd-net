namespace Leistd.OperationRecords.AspNetCore.Attributes;

/// <summary>声明写端点对应的业务动作码，让授权阶段的拒绝也能落一条失败记录。</summary>
/// <remarks>
/// 授权阶段的拒绝到不了应用服务；本特性把动作码带到端点元数据上，供 <c>HttpContext.RecordDeniedOperationAsync()</c>
/// 在拒绝时读取。动作码显式声明、不从路由推断，且须与成功路径写下的动作码逐字一致。
/// </remarks>
/// <param name="action">业务动作码，与应用服务成功路径使用的值逐字一致。</param>
/// <param name="targetRouteKeys">
/// 目标标识所在的路由参数名，按顺序以 <c>/</c> 连接；创建类端点尚无目标标识时省略，记为 <c>-</c>。
/// </param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OperationRecordActionAttribute(string action, params string[] targetRouteKeys) : Attribute
{
    /// <summary>业务动作码。</summary>
    public string Action { get; } = action;

    /// <summary>目标标识的路由参数名，按顺序以 <c>/</c> 连接；为空表示该端点没有目标标识。</summary>
    /// <remarks>
    /// <para>多段目标（如 <c>{tenantId}/{name}</c>）须全部列出，写法与成功路径一致。</para>
    /// <para>绑定到 <see cref="Guid"/> 参数或带 <c>guid</c> 约束的段归一为小写 <c>D</c> 格式，其余段保留路由原文。</para>
    /// </remarks>
    public IReadOnlyList<string> TargetRouteKeys { get; } = targetRouteKeys;

    /// <summary>加在路由值前面的前缀，让被拒记录的目标标识与成功路径写法一致（如权限授予记为 <c>Role/{roleId}</c>）。</summary>
    public string TargetIdPrefix { get; set; } = string.Empty;
}
