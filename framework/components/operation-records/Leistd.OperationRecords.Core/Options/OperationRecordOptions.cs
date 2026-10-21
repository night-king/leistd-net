using Leistd.Security.Claims;

namespace Leistd.OperationRecords.Options;

/// <summary>操作记录配置，经 <c>AddOperationRecords(options =&gt; ...)</c> 设置，不绑定配置节。</summary>
/// <remarks>
/// 模拟登录的 claim 类型默认取 <see cref="CustomClaimTypes"/>，须与宿主签发主体时一致。
/// 操作人标识按 <c>ClaimTypeOptions.UserIds</c> 读取（<c>ICurrentUser.SubjectId</c>）。
/// </remarks>
public class OperationRecordOptions
{
    /// <summary>模拟登录时真实操作人标识的 claim 类型。</summary>
    public string ImpersonatorIdClaimType { get; set; } = CustomClaimTypes.ImpersonatorUserId;

    /// <summary>模拟登录时真实操作人显示名的 claim 类型。</summary>
    public string ImpersonatorNameClaimType { get; set; } = CustomClaimTypes.ImpersonatorUserName;
}
