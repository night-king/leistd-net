using System.Reflection;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Security.OpenIddict.Server.Pruning;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using global::OpenIddict.Abstractions;
using Xunit;

namespace Leistd.Security.OpenIddict.Server.Tests;

public sealed class OpenIddictPruningJobTests
{
    [Fact]
    public async Task Pruning_passes_the_utc_threshold_and_cancellation_to_tokens_before_authorizations()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var calls = new List<string>();
        using var cancellation = new CancellationTokenSource();
        void Record(string name, DateTimeOffset threshold, CancellationToken token)
        {
            Assert.Equal(time.GetUtcNow().AddMinutes(-20), threshold);
            Assert.Equal(cancellation.Token, token);
            calls.Add(name);
        }
        var job = new OpenIddictPruningJob(
            Manager<IOpenIddictTokenManager>((threshold, token) => Record("tokens", threshold, token)),
            Manager<IOpenIddictAuthorizationManager>((threshold, token) => Record("authorizations", threshold, token)),
            time, Options.Create(new OpenIddictPruningOptions { MinimumRetention = TimeSpan.FromMinutes(20) }));
        await job.ExecuteAsync(new RecurringJobContext(OpenIddictPruningJob.Name, time.GetUtcNow()), cancellation.Token);
        Assert.Equal(["tokens", "authorizations"], calls);
    }

    [Fact]
    public async Task Cancellation_stops_pruning_before_the_authorization_phase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var authorizationsCalled = false;
        var job = new OpenIddictPruningJob(
            Manager<IOpenIddictTokenManager>((_, token) => token.ThrowIfCancellationRequested()),
            Manager<IOpenIddictAuthorizationManager>((_, _) => authorizationsCalled = true),
            TimeProvider.System, Options.Create(new OpenIddictPruningOptions()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.ExecuteAsync(
            new RecurringJobContext(OpenIddictPruningJob.Name, DateTimeOffset.UtcNow), cancellation.Token));
        Assert.False(authorizationsCalled);
    }

    private static T Manager<T>(Action<DateTimeOffset, CancellationToken> prune) where T : class
    {
        var manager = DispatchProxy.Create<T, PruningManager>();
        ((PruningManager)(object)manager).Prune = prune;
        return manager;
    }

    public class PruningManager : DispatchProxy
    {
        public Action<DateTimeOffset, CancellationToken> Prune { get; set; } = null!;
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "PruneAsync" || args is null) throw new InvalidOperationException("Unexpected manager call.");
            Prune((DateTimeOffset)args[0]!, (CancellationToken)args[1]!);
            return new ValueTask<long>(0);
        }
    }
}
