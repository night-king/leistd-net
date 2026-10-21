using Leistd.Data.EntityFrameworkCore.Modeling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Data.EntityFrameworkCore.Tests;

public sealed class ModelBuilderExtensionsTests
{
    [Fact]
    public async Task Named_filters_compose_and_can_be_ignored_independently()
    {
        await using var database = new Database<FilteredContext>(options => new FilteredContext(options));
        var db = database.Context;
        db.AddRange(new Item { Id = 1, Visible = true, Tenant = 1 }, new Item { Id = 2, Visible = false, Tenant = 1 },
            new Item { Id = 3, Visible = true, Tenant = 2 }, new DerivedItem { Id = 4, Visible = true, Tenant = 1 });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<Item>().CountAsync());
        Assert.Equal(3, await db.Set<Item>().IgnoreQueryFilters(["Visible"]).CountAsync());
        Assert.Equal(3, await db.Set<Item>().IgnoreQueryFilters(["Tenant"]).CountAsync());
        Assert.Equal(4, await db.Set<Item>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(4, (await db.Set<DerivedItem>().SingleAsync()).Id);
    }

    [Fact]
    public async Task Owned_contract_types_are_loaded_through_the_owner_without_a_separate_filter()
    {
        await using var database = new Database<OwnedContext>(options => new OwnedContext(options));
        var db = database.Context;
        db.Add(new Owner { Id = 1, Details = new Details { Visible = false } });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.False((await db.Set<Owner>().SingleAsync()).Details.Visible);
    }

    [Fact]
    public async Task Shared_type_entities_receive_filters_on_each_named_entity()
    {
        await using var database = new Database<SharedContext>(options => new SharedContext(options));
        var db = database.Context;
        foreach (var name in new[] { "First", "Second" })
        {
            db.Set<SharedItem>(name).AddRange(new SharedItem { Id = 1, Visible = true }, new SharedItem { Id = 2, Visible = false });
        }
        await db.SaveChangesAsync();
        Assert.Single(await db.Set<SharedItem>("First").ToListAsync());
        Assert.Single(await db.Set<SharedItem>("Second").ToListAsync());
        Assert.Equal(2, await db.Set<SharedItem>("First").IgnoreQueryFilters(["Visible"]).CountAsync());
    }

    [Fact]
    public void Invalid_arguments_fail_even_when_the_model_has_no_entities()
    {
        var builder = new ModelBuilder();
        Assert.Throws<ArgumentNullException>(() => ((ModelBuilder)null!).ApplyGlobalFilters<IVisible>("Visible", item => item.Visible));
        Assert.Throws<ArgumentException>(() => builder.ApplyGlobalFilters<IVisible>(" ", item => item.Visible));
        Assert.Throws<ArgumentNullException>(() => builder.ApplyGlobalFilters<IVisible>("Visible", null!));
    }

    private interface IVisible { bool Visible { get; } }
    private interface ITenant { int Tenant { get; } }
    private class Item : IVisible, ITenant
    {
        public int Id { get; set; }
        public bool Visible { get; set; }
        public int Tenant { get; set; }
    }
    private sealed class DerivedItem : Item;
    private sealed class Owner
    {
        public int Id { get; set; }
        public Details Details { get; set; } = new();
    }
    private sealed class Details : IVisible { public bool Visible { get; set; } }
    private sealed class SharedItem : IVisible
    {
        public int Id { get; set; }
        public bool Visible { get; set; }
    }

    private sealed class FilteredContext(DbContextOptions<FilteredContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>().HasDiscriminator<string>("Kind");
            modelBuilder.Entity<DerivedItem>();
            modelBuilder.ApplyGlobalFilters<IVisible>("Visible", item => false);
            modelBuilder.ApplyGlobalFilters<IVisible>("Visible", item => item.Visible);
            modelBuilder.ApplyGlobalFilters<ITenant>("Tenant", item => item.Tenant == 1);
        }
    }
    private sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>().OwnsOne(owner => owner.Details);
            modelBuilder.ApplyGlobalFilters<IVisible>("Visible", item => item.Visible);
        }
    }
    private sealed class SharedContext(DbContextOptions<SharedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var name in new[] { "First", "Second" }) modelBuilder.SharedTypeEntity<SharedItem>(name).ToTable(name);
            modelBuilder.ApplyGlobalFilters<IVisible>("Visible", item => item.Visible);
        }
    }
    private sealed class Database<TContext> : IAsyncDisposable where TContext : DbContext
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public TContext Context { get; }
        public Database(Func<DbContextOptions<TContext>, TContext> create)
        {
            _connection.Open();
            Context = create(new DbContextOptionsBuilder<TContext>().UseSqlite(_connection).Options);
            Context.Database.EnsureCreated();
        }
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
