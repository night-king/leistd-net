using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.TestBase.Assertions;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>同一上下文重复调用 <c>AddDddDbContext</c> 时仓储注册的幂等边界。</summary>
/// <remarks>
/// 组合根拆分时同一行登记可能被调用两次：相同实现不改变解析结果，必须按幂等跳过；
/// 换成另一个实现则会让"谁先注册谁生效"决定仓储，必须拒绝且不改动已有注册。
/// </remarks>
public sealed class RepeatedDbContextRegistrationTests
{
    [Fact]
    public void Identical_repeated_call_keeps_one_registration_per_repository_interface()
    {
        ServiceCollectionAssertions.AssertIdempotent(
            services => services.AddDddDbContext<OrderDbContext>(o => o.AddDefaultRepositories()));

        var services = new ServiceCollection();
        services.AddDddDbContext<OrderDbContext>(o => o.AddDefaultRepositories());
        services.AddDddDbContext<OrderDbContext>(o => o.AddDefaultRepositories());

        services.AssertSingle<IRepository<Order>>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IRepository<Order>, EfCoreRepository<OrderDbContext, Order, Guid>>();
        services.AssertSingle<IRepository<Order, Guid>>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IRepository<Order, Guid>, EfCoreRepository<OrderDbContext, Order, Guid>>();
    }

    [Fact]
    public void Repeated_call_with_another_implementation_is_rejected_and_keeps_the_first()
    {
        var services = new ServiceCollection();
        services.AddDddDbContext<OrderDbContext>(o => o.AddDefaultRepositories());

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddDddDbContext<OrderDbContext>(o => o.AddRepository<Order, OrderRepository>()));

        Assert.Contains("already registered", error.Message);
        Assert.Contains(nameof(OrderRepository), error.Message);
        services.AssertImplementedBy<IRepository<Order>, EfCoreRepository<OrderDbContext, Order, Guid>>();
        services.AssertImplementedBy<IRepository<Order, Guid>, EfCoreRepository<OrderDbContext, Order, Guid>>();
    }

    [Fact]
    public void Identical_repeated_custom_repository_is_kept_once()
    {
        var services = new ServiceCollection();
        services.AddDddDbContext<OrderDbContext>(o => o.AddRepository<Order, OrderRepository>());
        services.AddDddDbContext<OrderDbContext>(o => o.AddRepository<Order, OrderRepository>());

        services.AssertImplementedBy<IRepository<Order>, OrderRepository>();
        services.AssertImplementedBy<IRepository<Order, Guid>, OrderRepository>();
    }

    private sealed class Order : Entity<Guid>, IAggregateRoot<Guid>;

    private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<Order> Orders => Set<Order>();
    }

    private sealed class OrderRepository(IDbContextProvider<OrderDbContext> dbContextProvider, IUnitOfWorkManager uow)
        : EfCoreRepository<OrderDbContext, Order, Guid>(dbContextProvider, uow);
}
