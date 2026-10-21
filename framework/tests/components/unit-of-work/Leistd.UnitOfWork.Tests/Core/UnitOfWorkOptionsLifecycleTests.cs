using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>选项模式接入与 <c>Initialize</c> 的一次性约定。</summary>
public class UnitOfWorkOptionsLifecycleTests
{
    private static IServiceProvider Build(Action<UnitOfWorkOptions>? configure = null)
        => new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork(configure)
            .AddLogging()
            .BuildServiceProvider();

    /// <summary><c>Begin</c> 默认<b>并入</b>当前工作单元，不新建。</summary>
    /// <remarks>
    /// 默认新建的那一支代价不对称：独立 DI 作用域、独立 DbContext、同一个库上的第二个事务，
    /// 与外层未提交的写入互相加锁。框架自己的拦截器一直显式传 <c>false</c>——
    /// 默认值必须与它一致，否则手动路径的默认恰好是危险的那个。
    /// </remarks>
    [Fact]
    public async Task Begin_joins_the_current_unit_of_work_by_default()
    {
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var outer = manager.Begin(requiresNew: true);
        using var joined = manager.Begin();
        using var independent = manager.Begin(requiresNew: true);

        Assert.Equal(outer.Id, joined.Id);
        Assert.NotEqual(outer.Id, independent.Id);
    }

    [Fact]
    public async Task Begin_without_options_works_in_both_requiresNew_modes()
    {
        // Initialize 拒绝 null，而 Begin(requiresNew: false) 不带选项且无环境工作单元时
        // 手里只有 null。选项必须在 Begin 里定案。
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var reused = manager.Begin(requiresNew: false);
        Assert.NotNull(reused.Options);
        Assert.True(reused.Options.IsTransactional);

        using var fresh = manager.Begin(requiresNew: true);
        Assert.NotNull(fresh.Options);
    }

    [Fact]
    public async Task Begin_without_options_and_requiresNew_defaults_to_transactional()
    {
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.True(uow.Options.IsTransactional);
    }

    [Fact]
    public async Task Begin_without_options_respects_the_configured_transaction_mode()
    {
        var provider = Build(options => options.IsTransactional = false);
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.False(uow.Options.IsTransactional);
    }

    [Fact]
    public async Task RequiresNew_controls_reuse_independently_of_the_transaction_mode()
    {
        var provider = Build(options => options.IsTransactional = false);
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var outer = manager.Begin();
        using var reused = manager.Begin(requiresNew: false);
        using var fresh = manager.Begin(requiresNew: true);

        Assert.Equal(outer.Id, reused.Id);
        Assert.NotEqual(outer.Id, fresh.Id);
        Assert.False(fresh.Options.IsTransactional);
    }

    [Fact]
    public void Attribute_inherits_the_configured_transaction_mode_when_unspecified()
    {
        var attribute = new Attributes.UnitOfWorkAttribute();
        var defaults = new UnitOfWorkOptions { IsTransactional = false };

        var options = attribute.CreateOptionsFromDefault(defaults);

        Assert.False(options.IsTransactional);
    }

    [Fact]
    public void Attribute_can_explicitly_override_the_transaction_mode()
    {
        var attribute = new Attributes.UnitOfWorkAttribute(isTransactional: true);
        var defaults = new UnitOfWorkOptions { IsTransactional = false };

        var options = attribute.CreateOptionsFromDefault(defaults);

        Assert.True(options.IsTransactional);
    }

    [Fact]
    public async Task Configured_default_options_reach_the_unit_of_work()
    {
        var provider = Build(options => options.Timeout = TimeSpan.FromSeconds(42));
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.Equal(TimeSpan.FromSeconds(42), uow.Options.Timeout);
    }

    [Fact]
    public async Task Initialize_cannot_be_called_twice()
    {
        // 子工作单元的 Initialize 转发给父级；允许覆盖时，持有子工作单元的调用方
        // 就能在事务已按旧值开启之后改掉隔离级别与超时
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.Throws<InvalidOperationException>(() => uow.Initialize(new UnitOfWorkOptions()));
    }

    [Fact]
    public async Task Initialize_rejects_null_options()
    {
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.Throws<ArgumentNullException>(() => uow.Initialize(null!));
    }

    [Fact]
    public async Task Unit_of_work_exposes_its_service_provider_through_the_contract()
    {
        // 契约上有 ServiceProvider，EF 提供方不必向下转型到具体实现
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);

        Assert.NotNull(uow.ServiceProvider);
        Assert.NotSame(provider, uow.ServiceProvider);
    }


    [Fact]
    public async Task Completing_a_transactional_uow_with_a_cancelled_token_commits_nothing()
    {
        // 事务型：BeforeCommit 之后、提交之前有一次取消检查。
        // 请求被客户端放弃后再提交是纯粹的浪费，还会落库一份没人关心的写入。
        //
        // 挂真事务假件才有鉴别力：不挂的话根本没有提交动作，
        // "什么都没提交"这个断言自动成立，回退实现也不会变红
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);
        Assert.True(uow.Options.IsTransactional);

        var log = new UnitOfWorkLifecycleTests.CallLog();
        var transaction = new UnitOfWorkLifecycleTests.ProbeTransactionApi(log);
        uow.AddTransactionApi("probe", transaction);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.CompleteAsync(cts.Token));

        Assert.False(transaction.Committed);
        Assert.False(uow.IsCompleted);
    }

    [Fact]
    public async Task Completing_a_non_transactional_uow_does_not_report_persisted_work_as_cancelled()
    {
        // 非事务型没有提交阶段：SaveChanges 已各自落库，此处再抛取消
        // 只会把一个不可逆的成功报告成取消，诱导调用方重试并重复写入。
        // 假件的 SaveChangesAsync 不看 token，模拟"保存已成功、之后 token 才取消"
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(new UnitOfWorkOptions { IsTransactional = false });
        Assert.False(uow.Options.IsTransactional);

        var log = new UnitOfWorkLifecycleTests.CallLog();
        var database = new UnitOfWorkLifecycleTests.ProbeDatabaseApi(log, "probe");
        uow.AddDatabaseApi("probe", database);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await uow.CompleteAsync(cts.Token);

        Assert.True(database.SaveChangesCalled);
        Assert.True(uow.IsCompleted);
    }

    [Fact]
    public async Task Completing_with_a_live_token_succeeds()
    {
        var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(requiresNew: true);
        using var cts = new CancellationTokenSource();

        await uow.CompleteAsync(cts.Token);

        Assert.True(uow.IsCompleted);
    }
}
