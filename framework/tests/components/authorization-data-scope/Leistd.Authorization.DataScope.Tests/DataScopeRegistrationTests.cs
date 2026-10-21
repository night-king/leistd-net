using System.Linq.Expressions;
using Leistd.Authorization.DataScope.Abstractions;
using Leistd.Authorization.DataScope.Services;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Authorization.DataScope.Tests;

/// <summary>数据范围的注册面：应用器可替换且只有一份，范围 Provider 按实现累加、同一实现不重复。</summary>
/// <remarks>
/// 同一 Provider 登记两份时并集结果不变，但每次查询都多构造一遍谓词；
/// 反过来按 TryAdd 去重会让同一实体的第二种范围静默丢失，可见性因此变窄却不报错。
/// </remarks>
public sealed class DataScopeRegistrationTests
{
    [Fact]
    public void Registration_adds_a_scoped_applier()
    {
        var services = new ServiceCollection();

        services.AddDataScopeCore();

        services.AssertSingle<IDataScopeApplier>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IDataScopeApplier, DefaultDataScopeApplier>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services
            .AddDataScopeCore()
            .AddDataScopeProvider<Order, OwnOrdersProvider>());

    [Fact]
    public void Host_registered_applier_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDataScopeApplier>(_ => throw new NotSupportedException());

        services.AddDataScopeCore();

        Assert.NotNull(services.AssertSingle<IDataScopeApplier>(ServiceLifetime.Scoped).ImplementationFactory);
    }

    [Fact]
    public void Different_providers_for_the_same_entity_coexist_and_repeats_collapse()
    {
        var services = new ServiceCollection();

        services.AddDataScopeProvider<Order, OwnOrdersProvider>();
        services.AddDataScopeProvider<Order, DepartmentOrdersProvider>();
        services.AddDataScopeProvider<Order, OwnOrdersProvider>();

        var providers = services.Where(d => d.ServiceType == typeof(IDataScopeProvider<Order>)).ToList();
        Assert.Equal([typeof(OwnOrdersProvider), typeof(DepartmentOrdersProvider)], providers.Select(d => d.ImplementationType));
        Assert.All(providers, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    private sealed class Order;

    private sealed class OwnOrdersProvider : IDataScopeProvider<Order>
    {
        public string ResourceName => "Orders";

        public string ScopeName => "Own";

        public ValueTask<Expression<Func<Order, bool>>> BuildPredicateAsync(
            DataScopeContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Expression<Func<Order, bool>>>(_ => false);
    }

    private sealed class DepartmentOrdersProvider : IDataScopeProvider<Order>
    {
        public string ResourceName => "Orders";

        public string ScopeName => "Department";

        public ValueTask<Expression<Func<Order, bool>>> BuildPredicateAsync(
            DataScopeContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Expression<Func<Order, bool>>>(_ => false);
    }
}
