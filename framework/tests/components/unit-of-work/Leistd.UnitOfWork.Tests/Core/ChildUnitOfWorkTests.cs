using Leistd.EventBus.Events;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>嵌套工作单元：子级把一切都委托给父级，唯独提交与释放是空操作。</summary>
/// <remarks>
/// <para>这是工作单元里最容易出错、后果最重的一段：子级若真的提交，内层方法一返回
/// 事务就落地了，外层再回滚也追不回来；子级若真的释放，父级的连接会在外层还在用时被关掉。</para>
/// <para>子级类型是 <c>internal</c>，这里一律经 <c>IUnitOfWorkManager.Begin(requiresNew: false)</c>
/// 取得——那正是调用方唯一能拿到它的途径，也是真正需要被钉住的路径。</para>
/// </remarks>
public sealed class ChildUnitOfWorkTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .BuildServiceProvider();

    private sealed record Placed : ILocalEvent
    {
        public Guid EventId { get; } = Guid.CreateVersion7();
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
    }

    /// <summary>记录提交与释放次数的事务资源。</summary>
    /// <remarks>
    /// <see cref="ITransactionApi"/> 只有提交，没有回滚：工作单元的回滚是"不提交 + 释放"，
    /// 因此这里用 <see cref="Disposed"/> 而不是回滚计数来观察放弃路径。
    /// </remarks>
    private sealed class RecordingTransactionApi : ITransactionApi
    {
        public int Commits { get; private set; }
        public bool Disposed { get; private set; }

        public Task CommitAsync()
        {
            Commits++;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Child_shares_the_parent_identity_and_options()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        Assert.Equal(parent.Id, child.Id);
        Assert.Same(parent.Options, child.Options);
        Assert.Same(parent.ServiceProvider, child.ServiceProvider);
        Assert.Equal(parent.Outer, child.Outer);
    }

    // 子级提交必须什么都不做：真提交会让内层方法一返回事务就落地，
    // 外层随后的失败再也回滚不了。
    [Fact]
    public async Task Completing_a_child_does_not_commit_anything()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var transaction = new RecordingTransactionApi();
        parent.AddTransactionApi("db", transaction);

        var child = manager.Begin(requiresNew: false);
        await child.CompleteAsync();

        Assert.Equal(0, transaction.Commits);
        Assert.False(parent.IsCompleted);
    }

    // 子级释放同样是空操作：真释放会关掉父级还在用的连接。
    [Fact]
    public async Task Disposing_a_child_leaves_the_parent_alive()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var transaction = new RecordingTransactionApi();
        parent.AddTransactionApi("db", transaction);

        (manager.Begin(requiresNew: false)).Dispose();

        Assert.False(parent.IsDisposed);
        Assert.False(transaction.Disposed);
    }

    // 子级回滚必须真的传到父级：内层决定放弃时，整个外层事务都要作废，
    // 之后父级再提交也不得落库。
    [Fact]
    public async Task Rolling_back_from_a_child_prevents_the_parent_from_committing()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var transaction = new RecordingTransactionApi();
        parent.AddTransactionApi("db", transaction);

        var child = manager.Begin(requiresNew: false);
        await child.RollbackAsync();
        await parent.CompleteAsync();

        Assert.Equal(0, transaction.Commits);
    }

    // 数据库与事务 API 全部登记在父级上：子级自己存一份会让同一个连接被解析出两次。
    [Fact]
    public async Task Database_and_transaction_apis_are_registered_on_the_parent()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        var transaction = new RecordingTransactionApi();
        child.AddTransactionApi("db", transaction);

        Assert.Same(transaction, parent.FindTransactionApi("db"));
        Assert.Same(transaction, child.FindTransactionApi("db"));
        Assert.Null(child.FindDatabaseApi("missing"));
        Assert.Null(child.FindTransactionApi("missing"));
    }

    // 领域事件登记到父级，跟随父级的提交相位发布——留在子级会随子级一起消失。
    [Fact]
    public async Task Pending_events_are_handed_to_the_parent()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        var exception = Record.Exception(() => child.AddPendingEvents([new Placed()]));

        Assert.Null(exception);
        Assert.False(parent.IsCompleted);
    }

    // 子级订阅父级的失败事件，退订也要真的作用在父级上——
    // 只加不减会让已经退出的作用域继续收到通知。
    [Fact]
    public async Task Failure_subscriptions_add_and_remove_on_the_parent()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);
        var calls = 0;

        void Handler(object? _, UnitOfWorkFailedEventArgs __) => calls++;

        child.Failed += Handler;
        child.Failed -= Handler;
        parent.Dispose();

        Assert.Equal(0, calls);
    }

    // 状态一律读父级：子级自己记一份会在父级完成/释放后给出过期答案，
    // 而调用方普遍用 IsCompleted 决定要不要重复提交。
    [Fact]
    public async Task Lifecycle_state_is_read_from_the_parent()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        Assert.False(child.IsCompleted);
        Assert.False(child.IsDisposed);

        await parent.CompleteAsync();
        Assert.True(child.IsCompleted);

        parent.Dispose();
        Assert.True(child.IsDisposed);
    }

    // 保存、登记数据库 API、设置外层、初始化选项全部转发给父级：
    // 子级留一份就意味着同一个连接被解析出两次，事务边界随之分裂。
    [Fact]
    public async Task Delegating_members_all_reach_the_parent()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        var database = new RecordingDatabaseApi();
        child.AddDatabaseApi("db", database);
        Assert.Same(database, parent.FindDatabaseApi("db"));

        await child.SaveChangesAsync();
        Assert.Equal(1, database.Saves);

        child.SetOuter(null);
        Assert.Equal(parent.Id, child.Id);

        // Initialize 转发到父级，而父级已经初始化过——抛出正是"确实转发了"的证据。
        // 子级若自己吞掉这次调用，嵌套作用域就能悄悄换掉外层的事务选项。
        Assert.Throws<InvalidOperationException>(() => child.Initialize(new UnitOfWorkOptions()));
    }

    private sealed class RecordingDatabaseApi : IDatabaseApi, ISupportsSavingChanges
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ToString_identifies_the_child_and_carries_the_shared_id()
    {
        await using var provider = Build();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var parent = manager.Begin();
        var child = manager.Begin(requiresNew: false);

        Assert.Equal($"[ChildUnitOfWork {parent.Id}]", child.ToString());
        Assert.Contains(parent.Id.ToString(), parent.ToString());
    }
}
