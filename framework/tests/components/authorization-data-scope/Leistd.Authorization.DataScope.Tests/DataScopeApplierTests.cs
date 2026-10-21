using System.Security.Claims;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.Authorization.DataScope.Services;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.DataScope.Tests;

public class DataScopeApplierTests : IAsyncLifetime
{
    private const string Resource = "Orders";
    private const string CurrentUserId = "u1";

    private SqliteConnection _connection = default!;
    private TestOrderDbContext _db = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestOrderDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TestOrderDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _db.Orders.AddRange(
            new TestOrder { OwnerId = CurrentUserId, OrganizationId = "org-1", Title = "本人 / 组织一" },
            new TestOrder { OwnerId = "u2", OrganizationId = "org-1", Title = "他人 / 组织一" },
            new TestOrder { OwnerId = "u3", OrganizationId = "org-2", Title = "他人 / 组织二" },
            new TestOrder { OwnerId = "u4", OrganizationId = "org-3", Title = "他人 / 组织三" });

        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_subject_sees_nothing()
    {
        var applier = CreateApplier(subject: null, assignments: []);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Empty(await query.ToListAsync());
    }

    [Fact]
    public async Task No_assignment_denies_by_default_instead_of_returning_everything()
    {
        var applier = CreateApplier(Subject(), assignments: []);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Empty(await query.ToListAsync());
    }

    [Fact]
    public async Task Super_admin_sees_everything()
    {
        var applier = CreateApplier(
            new PermissionSubject(CurrentUserId, [], IsSuperAdmin: true),
            assignments: []);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Equal(4, await query.CountAsync());
    }

    [Fact]
    public async Task Own_scope_is_translated_into_a_database_predicate()
    {
        var applier = CreateApplier(
            Subject(),
            [new DataScopeAssignment(Resource, DataOperations.Read, OwnScopeProvider.Scope)]);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        // 由 Sqlite 执行：EF Core 对无法翻译的谓词会抛异常，通过即证明生成了 SQL。
        var orders = await query.ToListAsync();

        Assert.Equal("本人 / 组织一", Assert.Single(orders).Title);
    }

    [Fact]
    public async Task Multiple_assigned_scopes_are_combined_as_a_union()
    {
        var applier = CreateApplier(
            Subject(),
            [
                new DataScopeAssignment(Resource, DataOperations.Read, OwnScopeProvider.Scope),
                new DataScopeAssignment(Resource, DataOperations.Read, OrganizationScopeProvider.Scope, "org-2")
            ]);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);
        var titles = await query.Select(x => x.Title).OrderBy(x => x).ToListAsync();

        // 本人的订单与 org-2 的订单取并集，而不是相互覆盖。
        Assert.Equal(["他人 / 组织二", "本人 / 组织一"], titles.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_unrestricted_scope_short_circuits_the_union()
    {
        var applier = CreateApplier(
            Subject(),
            [
                new DataScopeAssignment(Resource, DataOperations.Read, OwnScopeProvider.Scope),
                new DataScopeAssignment(Resource, DataOperations.Read, AllScopeProvider.Scope)
            ]);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Equal(4, await query.CountAsync());
    }

    [Fact]
    public async Task An_assignment_without_a_provider_does_not_widen_the_scope()
    {
        var applier = CreateApplier(
            Subject(),
            [new DataScopeAssignment(Resource, DataOperations.Read, "ScopeWithoutProvider")]);

        var query = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Empty(await query.ToListAsync());
    }

    [Fact]
    public async Task Read_and_update_scopes_can_differ()
    {
        // 读取覆盖整个组织，更新只允许本人：不能假设"能看就能改"。
        var applier = CreateApplier(
            Subject(),
            [
                new DataScopeAssignment(Resource, DataOperations.Read, OrganizationScopeProvider.Scope, "org-1"),
                new DataScopeAssignment(Resource, DataOperations.Update, OwnScopeProvider.Scope)
            ]);

        var readQuery = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);
        var updateQuery = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Update);

        Assert.Equal(2, await readQuery.CountAsync());
        Assert.Equal(1, await updateQuery.CountAsync());
    }

    [Fact]
    public async Task List_count_and_export_share_one_scope_entry_and_stay_consistent()
    {
        var applier = CreateApplier(
            Subject(),
            [new DataScopeAssignment(Resource, DataOperations.Read, OrganizationScopeProvider.Scope, "org-1")]);

        var scoped = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        var total = await scoped.CountAsync();
        var page = await scoped.OrderBy(x => x.Title).Skip(0).Take(1).ToListAsync();
        var exported = await scoped.OrderBy(x => x.Title).ToListAsync();

        // 分页总数、当前页与导出必须来自同一个范围入口，否则总数会与实际可见数据不符。
        Assert.Equal(2, total);
        Assert.Single(page);
        Assert.Equal(total, exported.Count);
        Assert.All(exported, order => Assert.Equal("org-1", order.OrganizationId));
    }

    private static PermissionSubject Subject()
        => new(CurrentUserId, ["r1"], IsSuperAdmin: false);

    private DefaultDataScopeApplier CreateApplier(
        PermissionSubject? subject,
        IReadOnlyList<DataScopeAssignment> assignments,
        params IDataScopeProvider<TestOrder>[] extraProviders)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataScopeProvider<TestOrder>>(new OwnScopeProvider());
        services.AddSingleton<IDataScopeProvider<TestOrder>>(new OrganizationScopeProvider());
        services.AddSingleton<IDataScopeProvider<TestOrder>>(new AllScopeProvider());

        foreach (var extra in extraProviders)
        {
            services.AddSingleton(extra);
        }

        return new DefaultDataScopeApplier(
            new FakeSubjectProvider(subject),
            services.BuildServiceProvider(),
            new FakeAssignmentProvider(assignments));
    }

    private sealed class FakeSubjectProvider(PermissionSubject? subject) : IPermissionSubjectProvider
    {
        public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(subject);

        public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => Task.FromResult(subject);
    }

    private sealed class FakeAssignmentProvider(IReadOnlyList<DataScopeAssignment> assignments)
        : IDataScopeAssignmentProvider
    {
        public ValueTask<IReadOnlyList<DataScopeAssignment>> GetAssignmentsAsync(
            PermissionSubject subject,
            string resourceName,
            string operation,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DataScopeAssignment> matched = assignments
                .Where(x => x.ResourceName == resourceName && x.Operation == operation)
                .ToList();

            return ValueTask.FromResult(matched);
        }
    }

    private sealed class OwnScopeProvider : IDataScopeProvider<TestOrder>
    {
        public const string Scope = "Own";

        public string ResourceName => Resource;
        public string ScopeName => Scope;

        public ValueTask<Expression<Func<TestOrder, bool>>> BuildPredicateAsync(
            DataScopeContext context,
            CancellationToken cancellationToken = default)
        {
            var userId = context.Subject.UserId;
            return ValueTask.FromResult<Expression<Func<TestOrder, bool>>>(order => order.OwnerId == userId);
        }
    }

    private sealed class OrganizationScopeProvider : IDataScopeProvider<TestOrder>
    {
        public const string Scope = "Organization";

        public string ResourceName => Resource;
        public string ScopeName => Scope;

        public ValueTask<Expression<Func<TestOrder, bool>>> BuildPredicateAsync(
            DataScopeContext context,
            CancellationToken cancellationToken = default)
        {
            var organizationIds = context.Assignments
                .Where(x => x.ScopeName == Scope && !string.IsNullOrWhiteSpace(x.ScopeValue))
                .Select(x => x.ScopeValue!)
                .ToList();

            return ValueTask.FromResult<Expression<Func<TestOrder, bool>>>(
                order => organizationIds.Contains(order.OrganizationId));
        }
    }

    [Fact]
    public async Task A_provider_contributing_nothing_does_not_open_everything()
    {
        // 少查一个条件、拿不到上下文的 Provider 只能表达"本范围不贡献可见性"，
        // 绝不能顺带被解释成"全部可见"——那会让整张表当场放开。其余范围照常生效。
        var applier = CreateApplier(
            Subject(),
            [new DataScopeAssignment(Resource, DataOperations.Read, SilentScopeProvider.Scope, null)],
            new SilentScopeProvider());

        var visible = await applier.ApplyAsync(_db.Orders, Resource, DataOperations.Read);

        Assert.Empty(visible);
    }

    /// <summary>不贡献可见性的 Provider，用于验证契约不会 fail-open。</summary>
    private sealed class SilentScopeProvider : IDataScopeProvider<TestOrder>
    {
        public const string Scope = "Silent";

        public string ResourceName => Resource;
        public string ScopeName => Scope;

        public ValueTask<Expression<Func<TestOrder, bool>>> BuildPredicateAsync(
            DataScopeContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Expression<Func<TestOrder, bool>>>(_ => false);
    }

    private sealed class AllScopeProvider : IDataScopeProvider<TestOrder>
    {
        public const string Scope = "All";

        public string ResourceName => Resource;
        public string ScopeName => Scope;

        // "全部可见"必须显式表达，不存在"什么都不返回就等于不限制"的写法。
        public ValueTask<Expression<Func<TestOrder, bool>>> BuildPredicateAsync(
            DataScopeContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Expression<Func<TestOrder, bool>>>(_ => true);
    }
}

public class TestOrder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string OwnerId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Title { get; set; } = default!;
}

public class TestOrderDbContext(DbContextOptions<TestOrderDbContext> options) : DbContext(options)
{
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
        });
    }
}
