using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.TestBase.Assertions;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary><c>AddRepository</c> 登记的实现所带的自定义仓储接口（如 <c>IUserRepository</c>）可直接注入。</summary>
/// <remarks>
/// 自定义接口与默认接口指向同一实现、同一生命周期；同一自定义接口出现两个实现时，
/// 后注册静默胜出会让调用方拿到哪个由顺序决定，因此与默认接口一样在注册期拒绝，且保留先登记的实现。
/// </remarks>
public sealed class CustomRepositoryInterfaceRegistrationTests
{
    [Fact]
    public void Custom_interface_is_registered_scoped_to_the_same_implementation()
    {
        var services = new ServiceCollection();

        services.AddDddDbContext<CustomerDbContext>(o => o.AddRepository<Customer, CustomerRepository>());

        services.AssertSingle<ICustomerRepository>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<ICustomerRepository, CustomerRepository>();
        services.AssertImplementedBy<IRepository<Customer>, CustomerRepository>();
        services.AssertImplementedBy<IRepository<Customer, Guid>, CustomerRepository>();
        // 不区分实体的非泛型基接口不登记：多个实体共用它，登记了只会互相冲突
        services.AssertNotRegistered<IRepository>();
    }

    [Fact]
    public void Custom_interface_resolves_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDddInfrastructure();
        services.AddDbContext<CustomerDbContext>(o => o.UseInMemoryDatabase($"custom-repo-{Guid.NewGuid()}"));
        services.AddDddDbContext<CustomerDbContext>(o => o.AddRepository<Customer, CustomerRepository>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<CustomerRepository>(scope.ServiceProvider.GetRequiredService<ICustomerRepository>());
    }

    [Fact]
    public void Identical_repeated_registration_keeps_one_custom_descriptor()
    {
        ServiceCollectionAssertions.AssertIdempotent(services =>
            services.AddDddDbContext<CustomerDbContext>(o => o.AddRepository<Customer, CustomerRepository>()));
    }

    [Fact]
    public void Another_implementation_of_the_same_custom_interface_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddDddDbContext<CustomerDbContext>(o => o.AddRepository<Customer, CustomerRepository>());

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddDddDbContext<ArchiveDbContext>(o => o.AddRepository<Customer, ArchivedCustomerRepository>()));

        Assert.Contains("already registered", error.Message);
        Assert.Contains(nameof(ArchivedCustomerRepository), error.Message);
        services.AssertImplementedBy<ICustomerRepository, CustomerRepository>();
    }

    public sealed class Customer : Entity<Guid>;

    public interface ICustomerRepository : IRepository<Customer, Guid>;

    private sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<Customer> Customers => Set<Customer>();
    }

    private sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options)
        : BaseDbContext(options, serviceProvider: null);

    private sealed class CustomerRepository(IDbContextProvider<CustomerDbContext> dbContextProvider, IUnitOfWorkManager uow)
        : EfCoreRepository<CustomerDbContext, Customer, Guid>(dbContextProvider, uow), ICustomerRepository;

    private sealed class ArchivedCustomerRepository(IDbContextProvider<ArchiveDbContext> dbContextProvider, IUnitOfWorkManager uow)
        : EfCoreRepository<ArchiveDbContext, Customer, Guid>(dbContextProvider, uow), ICustomerRepository;
}
