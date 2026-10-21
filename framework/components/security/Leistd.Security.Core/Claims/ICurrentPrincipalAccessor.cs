using System.Security.Claims;

namespace Leistd.Security.Claims;

/// <summary>提供当前认证主体并支持临时切换。</summary>
public interface ICurrentPrincipalAccessor
{
    /// <summary>当前认证主体；没有时为 <see langword="null"/>。</summary>
    ClaimsPrincipal? Principal { get; }

    /// <summary>临时切换认证主体，并在释放返回句柄时恢复父主体。</summary>
    /// <remarks>
    /// 仅切换主体，不更新租户或链路标识。后台任务、消息消费者和 Hub 调用应通过
    /// <c>Leistd.AmbientContext.IAmbientContext.Begin(principal)</c> 建立已注册的上下文维度。
    /// </remarks>
    /// <example>
    /// <code>
    /// // 测试中只替换身份读取面，不切换租户或链路。
    /// using (principalAccessor.Change(testPrincipal))
    /// {
    ///     var principal = principalAccessor.Principal;
    /// }
    /// </code>
    /// </example>
    IDisposable Change(ClaimsPrincipal principal);
}
