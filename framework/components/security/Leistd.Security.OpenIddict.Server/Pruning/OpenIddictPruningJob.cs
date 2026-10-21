using Leistd.BackgroundJobs.Recurring;
using Microsoft.Extensions.Options;
using global::OpenIddict.Abstractions;

namespace Leistd.Security.OpenIddict.Server.Pruning;

/// <summary>通过原生管理器清理过期令牌和无效授权；宿主选择存储、排期和执行范围。</summary>
public sealed class OpenIddictPruningJob(IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    TimeProvider time, IOptions<OpenIddictPruningOptions> options) : IRecurringJob
{
    /// <summary>默认任务名，也是原生调度器集群锁和水位的键。</summary>
    public const string Name = "security.openiddict.prune";

    /// <inheritdoc />
    public async Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
    {
        var threshold = time.GetUtcNow() - options.Value.MinimumRetention;
        await tokens.PruneAsync(threshold, cancellationToken);
        await authorizations.PruneAsync(threshold, cancellationToken);
    }
}
