using Leistd.Auditing.Abstractions;
using Leistd.Ddd.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

public sealed class OwnedQueryFilterTests
{
    [Fact]
    public async Task Owned_contract_implementations_use_the_owner_filter_and_allow_valid_model_creation()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new OwnedDbContext(new DbContextOptionsBuilder<OwnedDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new Owner { Id = 1, Details = new Details { IsDeleted = true } },
            new Owner { Id = 2, IsDeleted = true, Details = new Details() });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var owner = await db.Owners.SingleAsync();
        Assert.Equal(1, owner.Id);
        Assert.True(owner.Details.IsDeleted);
        Assert.Equal(2, await db.Owners.IgnoreQueryFilters().CountAsync());
    }

    private sealed class OwnedDbContext(DbContextOptions<OwnedDbContext> options) : BaseDbContext(options, null)
    {
        public DbSet<Owner> Owners => Set<Owner>();
        protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Owner>().OwnsOne(owner => owner.Details);
    }

    private sealed class Owner : ISoftDelete
    {
        public int Id { get; set; }
        public bool IsDeleted { get; set; }
        public Details Details { get; set; } = new();
    }

    private sealed class Details : ISoftDelete
    {
        public bool IsDeleted { get; set; }
    }
}
