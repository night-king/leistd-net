using Leistd.Auditing.EntityFrameworkCore.Conventions;
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.Ddd.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary><see cref="DddEntityConvention"/> 的模型约定，以及 <c>AddDddDbContext</c> 只给基座上下文挂拦截器。</summary>
public sealed class DddEntityConventionTests
{
    [Fact]
    public void Base_contexts_apply_the_convention_to_audit_columns_and_the_stamp()
    {
        var model = Model<ConventionDbContext>(options => new ConventionDbContext(options));

        var article = model.FindEntityType(typeof(Article))!;
        Assert.Equal(64, article.FindProperty(nameof(Article.CreatorId))!.GetMaxLength());
        Assert.Equal(64, article.FindProperty(nameof(Article.LastModifierId))!.GetMaxLength());
        Assert.Equal(64, article.FindProperty(nameof(Article.DeleterId))!.GetMaxLength());

        var stamp = article.FindProperty(nameof(Article.ConcurrencyStamp))!;
        Assert.Equal(40, stamp.GetMaxLength());
        Assert.True(stamp.IsConcurrencyToken);
        Assert.False(stamp.IsNullable);
    }

    // 显式 Fluent 配置覆盖约定，且只覆盖被配置的那一项
    [Fact]
    public void Explicit_configuration_wins_over_the_convention()
    {
        var model = Model<ConventionDbContext>(options => new ConventionDbContext(options));

        var note = model.FindEntityType(typeof(Note))!;
        Assert.Equal(128, note.FindProperty(nameof(Note.CreatorId))!.GetMaxLength());

        var stamp = note.FindProperty(nameof(Note.ConcurrencyStamp))!;
        Assert.Equal(80, stamp.GetMaxLength());
        Assert.True(stamp.IsConcurrencyToken);
        Assert.False(stamp.IsNullable);
    }

    // 不继承基座的上下文（如控制库）显式注册同一约定
    [Fact]
    public void Plain_contexts_can_register_the_same_convention()
    {
        var model = Model<PlainDbContext>(options => new PlainDbContext(options));

        var article = model.FindEntityType(typeof(Article))!;
        Assert.Equal(64, article.FindProperty(nameof(Article.CreatorId))!.GetMaxLength());
        Assert.True(article.FindProperty(nameof(Article.ConcurrencyStamp))!.IsConcurrencyToken);
    }

    // 派生实体才实现契约、属性却声明在已映射的基实体上：约定仍要作用到该属性
    [Fact]
    public void A_contract_implemented_by_a_derived_entity_reaches_the_inherited_property()
    {
        var model = Model<ConventionDbContext>(options => new ConventionDbContext(options));

        var stamp = model.FindEntityType(typeof(StampedReply))!.FindProperty(nameof(StampedReply.ConcurrencyStamp))!;
        Assert.Equal(40, stamp.GetMaxLength());
        Assert.True(stamp.IsConcurrencyToken);
    }

    [Fact]
    public void Only_base_contexts_get_the_save_interceptors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDddInfrastructure();
        services.AddDbContext<ConventionDbContext>(options => options.UseInMemoryDatabase($"base-{Guid.NewGuid()}"));
        services.AddDbContext<PlainDbContext>(options => options.UseInMemoryDatabase($"plain-{Guid.NewGuid()}"));
        // 重复登记不能挂第二层：多数重复执行碰巧幂等，只有数量能看出来
        services.AddDddDbContext<ConventionDbContext>();
        services.AddDddDbContext<ConventionDbContext>();
        services.AddDddDbContext<PlainDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal(3, Interceptors(scope.ServiceProvider.GetRequiredService<ConventionDbContext>()).Count);
        Assert.Empty(Interceptors(scope.ServiceProvider.GetRequiredService<PlainDbContext>()));
    }

    // 先登记、后 AddDbContext 同样生效：挂载走 ConfigureDbContext，与注册先后无关
    [Fact]
    public void Interceptors_are_attached_when_the_context_is_declared_before_AddDbContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDddInfrastructure();
        services.AddDddDbContext<ConventionDbContext>();
        services.AddDbContext<ConventionDbContext>(options => options.UseInMemoryDatabase($"order-{Guid.NewGuid()}"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Equal(3, Interceptors(scope.ServiceProvider.GetRequiredService<ConventionDbContext>()).Count);
    }

    private static List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor> Interceptors(DbContext dbContext)
        => [.. dbContext.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors ?? []];

    private static IModel Model<TDbContext>(Func<DbContextOptions<TDbContext>, TDbContext> create)
        where TDbContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TDbContext>()
            .UseInMemoryDatabase($"convention-{Guid.NewGuid()}")
            .Options;
        using var dbContext = create(options);
        return dbContext.GetService<IDesignTimeModel>().Model;
    }

    private sealed class Article : FullAuditedEntity<Guid>, IHasConcurrencyStamp
    {
        public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();
    }

    private sealed class Note : FullAuditedEntity<Guid>, IHasConcurrencyStamp
    {
        public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();
    }

    private class Reply : Entity<Guid>
    {
        public string ConcurrencyStamp { get; set; } = ConcurrencyStamps.New();
    }

    private sealed class StampedReply : Reply, IHasConcurrencyStamp;

    private sealed class ConventionDbContext(DbContextOptions<ConventionDbContext> options)
        : BaseDbContext(options, serviceProvider: null)
    {
        public DbSet<Article> Articles => Set<Article>();

        public DbSet<Note> Notes => Set<Note>();

        public DbSet<Reply> Replies => Set<Reply>();

        public DbSet<StampedReply> StampedReplies => Set<StampedReply>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Note>(b =>
            {
                b.Property(x => x.CreatorId).HasMaxLength(128);
                b.Property(x => x.ConcurrencyStamp).HasMaxLength(80);
            });
    }

    private sealed class PlainDbContext(DbContextOptions<PlainDbContext> options) : DbContext(options)
    {
        public DbSet<Article> Articles => Set<Article>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Add(_ => new AuditingEntityConvention());
            configurationBuilder.Conventions.Add(_ => new DddEntityConvention());
        }
    }
}
