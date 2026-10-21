using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

public sealed class RepositoryReadQueryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly QueryDbContext _db;
    private readonly VisibleRepository _repository;

    public RepositoryReadQueryTests()
    {
        _connection.Open();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDddInfrastructure();
        services.AddDbContext<QueryDbContext>(options => options.UseSqlite(_connection));
        services.AddDddDbContext<QueryDbContext>();
        services.AddScoped<VisibleRepository>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<QueryDbContext>();
        _db.Database.EnsureCreated();
        _db.AddRange(new Item(1, false), new Item(2, true));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        _repository = _scope.ServiceProvider.GetRequiredService<VisibleRepository>();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("first")]
    [InlineData("one")]
    [InlineData("list")]
    public async Task Entity_reads_apply_the_override_and_load_related_data(string operation)
    {
        IEnumerable<Item> results = operation switch
        {
            "id" => [(await _repository.GetByIdAsync(1))!],
            "first" => [(await _repository.GetFirstAsync(item => true, query => query.OrderByDescending(item => item.Id)))!],
            "one" => [(await _repository.GetOneAsync(item => true))!],
            _ => await _repository.GetListAsync()
        };

        var item = Assert.Single(results);
        Assert.Equal(1, item.Id);
        Assert.NotNull(item.Detail);
        Assert.Equal(EntityState.Unchanged, _db.Entry(item).State);
    }

    [Fact]
    public async Task Count_and_any_apply_the_same_read_filter()
    {
        Assert.Equal(1, await _repository.CountAsync());
        Assert.False(await _repository.AnyAsync(item => item.Hidden));
        Assert.Null(await _repository.GetByIdAsync(2));
    }

    [Fact]
    public async Task A_tracked_read_can_be_updated_and_writes_use_the_original_set()
    {
        var item = (await _repository.GetByIdAsync(1))!;
        Assert.Equal(EntityState.Unchanged, _db.Entry(item).State);
        item.Hidden = true;
        await _repository.UpdateAsync(item);
        await _repository.InsertAsync(new Item(3, true));
        _db.ChangeTracker.Clear();

        Assert.True((await _db.Items.SingleAsync(entity => entity.Id == 1)).Hidden);
        Assert.Equal(3, await _db.Items.CountAsync());
    }

    [Fact]
    public async Task Id_deletion_respects_the_read_filter_and_predicate_deletion_uses_the_original_set()
    {
        await _repository.DeleteAsync(2);
        Assert.True(await _db.Items.AnyAsync(item => item.Id == 2));

        await _repository.DeleteManyAsync(item => item.Hidden);
        Assert.False(await _db.Items.AnyAsync(item => item.Id == 2));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    public sealed class Item : Entity<int>
    {
        private Item() { }
        public Item(int id, bool hidden)
        {
            Id = id;
            Hidden = hidden;
            Detail = new Detail { Id = id };
        }
        public bool Hidden { get; set; }
        public int DetailId { get; set; }
        public Detail? Detail { get; set; }
    }

    public sealed class Detail
    {
        public int Id { get; set; }
    }

    private sealed class QueryDbContext(DbContextOptions<QueryDbContext> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    private sealed class VisibleRepository(IDbContextProvider<QueryDbContext> contexts, IUnitOfWorkManager uow)
        : EfCoreRepository<QueryDbContext, Item, int>(contexts, uow)
    {
        public override async Task<IQueryable<Item>> GetQueryableAsync(CancellationToken cancellationToken = default) =>
            (await base.GetQueryableAsync(cancellationToken)).Where(item => !item.Hidden).Include(item => item.Detail);
    }
}
