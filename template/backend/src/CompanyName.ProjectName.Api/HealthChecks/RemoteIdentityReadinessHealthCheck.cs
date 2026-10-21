#if (RemoteTokenAuth)
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CompanyName.ProjectName.Api.HealthChecks;

/// <summary>资源服务的 readiness：确认远端 Identity 可达之后才接流量。</summary>
/// <remarks>
/// <para>资源服务只有取得远端 OIDC 元数据与签名密钥后才可接收流量；启动期确认由
/// <c>RemoteIdentityReadinessInitializer</c> 完成后调用 <see cref="MarkReady"/>。</para>
/// <para>成功后锁存：Identity 短暂故障不会摘除已具备缓存的实例。确认失败时实例保持 not ready 并持续重试，
/// liveness 仍只反映进程状态，避免外部依赖故障触发实例重启。</para>
/// <para>标志与检查放在同一个类里，与官方文档"分离 readiness 与 liveness"的示例同一写法。</para>
/// </remarks>
internal sealed class RemoteIdentityReadinessHealthCheck : IHealthCheck
{
    private volatile bool _isReady;

    /// <summary>启动依赖是否已确认通过（锁存，一经置位不再回落）</summary>
    public bool IsReady => _isReady;

    /// <summary>标记启动依赖确认通过</summary>
    public void MarkReady() => _isReady = true;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_isReady
            ? HealthCheckResult.Healthy("Remote identity metadata was confirmed at startup.")
            : HealthCheckResult.Unhealthy(
                "Remote identity metadata has not been confirmed yet; refusing traffic."));
}
#endif
