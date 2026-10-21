using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Resolution;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

public class TenantResolverTests
{
    private sealed class FakeContributor(string name, string? value = null, bool handled = false)
        : ITenantResolveContributor
    {
        public bool Executed { get; private set; }
        public string Name => name;

        public Task ResolveAsync(TenantResolveContext context)
        {
            Executed = true;
            if (value is not null)
            {
                context.TenantIdOrName = value;
            }

            if (handled)
            {
                context.Handled = true;
            }

            return Task.CompletedTask;
        }
    }

    private static TenantResolver CreateResolver(params ITenantResolveContributor[] contributors)
    {
        var options = new TenantResolveOptions();
        foreach (var contributor in contributors)
        {
            options.Contributors.Add(contributor);
        }

        return new TenantResolver(
            new ServiceCollection().BuildServiceProvider(),
            MsOptions.Create(options));
    }

    [Fact]
    public async Task First_conclusive_contributor_wins_and_stops_the_chain()
    {
        var second = new FakeContributor("second", "t-second");
        var resolver = CreateResolver(new FakeContributor("first", "t-first"), second);

        var result = await resolver.ResolveAsync();

        Assert.Equal("t-first", result.TenantIdOrName);
        Assert.False(second.Executed);
        Assert.Equal("first", result.AppliedResolver);
    }

    [Fact]
    public async Task Handled_with_null_means_definitely_host_and_stops_the_chain()
    {
        // 已认证宿主用户的语义：有定论（是宿主），后续头/查询串不得再改写
        var header = new FakeContributor("header", "t-evil");
        var resolver = CreateResolver(new FakeContributor("claim", value: null, handled: true), header);

        var result = await resolver.ResolveAsync();

        Assert.Null(result.TenantIdOrName);
        Assert.False(header.Executed);
    }

    [Fact]
    public async Task Inconclusive_contributors_fall_through_to_next()
    {
        var resolver = CreateResolver(
            new FakeContributor("first"),
            new FakeContributor("second", "t2"));

        var result = await resolver.ResolveAsync();

        Assert.Equal("t2", result.TenantIdOrName);
        // 前一个没给出结论就继续，记的是给出结论的那一个
        Assert.Equal("second", result.AppliedResolver);
    }

    [Fact]
    public async Task Nothing_resolved_returns_null_meaning_host()
    {
        var resolver = CreateResolver(new FakeContributor("first"), new FakeContributor("second"));

        var result = await resolver.ResolveAsync();

        Assert.Null(result.TenantIdOrName);
        Assert.Null(result.AppliedResolver);
    }
}
