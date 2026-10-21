using System.Security.Claims;

namespace Leistd.AmbientContext;

/// <summary>为非 HTTP 入口一次性建立环境上下文（主体，以及已注册的其它维度）。</summary>
/// <remarks>
/// <para>用于 SignalR Hub 方法调用、后台作业、消息消费者这类拿到完整身份才进入的入口。
/// 这些入口不经 ASP.NET Core 中间件，因此中间件建立的主体、租户与链路标识在其中都不成立。</para>
/// <para>HTTP 请求不使用本接口，理由见 <see cref="IAmbientContextContributor"/>。</para>
/// </remarks>
/// <example>
/// <code>
/// using (ambientContext.Begin(hubCallerContext.User!))
/// {
///     // 此作用域内 ICurrentUser、ICurrentTenant、ICorrelationIdProvider 均成立
/// }
/// </code>
/// </example>
public interface IAmbientContext
{
    /// <summary>建立环境上下文；释放返回值即还原到进入前的状态。</summary>
    /// <param name="principal">本次调用的主体。</param>
    /// <param name="correlationId">指定链路标识；省略时由追踪维度决定。</param>
    IDisposable Begin(ClaimsPrincipal principal, string? correlationId = null);

    /// <summary>捕获当前执行流的环境上下文（主体与各维度），供稍后在别的执行流里还原。</summary>
    /// <remarks>
    /// 用于后台队列：入队时捕获，执行时 <see cref="Restore"/>。只捕获登记过的维度，不捕获工作单元、<c>HttpContext</c> 这类随请求释放的对象。
    /// </remarks>
    AmbientContextSnapshot Capture();

    /// <summary>还原 <see cref="Capture"/> 得到的上下文；释放返回值即回到还原前的状态。</summary>
    /// <param name="snapshot">捕获的上下文。</param>
    IDisposable Restore(AmbientContextSnapshot snapshot);
}

/// <summary>某一时刻的环境上下文快照，由 <see cref="IAmbientContext.Capture"/> 产生、<see cref="IAmbientContext.Restore"/> 消费。</summary>
/// <param name="Principal">捕获时的主体；匿名或未建立时为 <see langword="null"/>。</param>
/// <param name="States">各维度捕获的值，按维度的实现类型索引；内容只由对应维度解读。</param>
public sealed record AmbientContextSnapshot(
    ClaimsPrincipal? Principal,
    IReadOnlyDictionary<Type, object?> States);
