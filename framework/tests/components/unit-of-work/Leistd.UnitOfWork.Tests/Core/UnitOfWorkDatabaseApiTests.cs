using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CoreUnitOfWork = Leistd.UnitOfWork.DefaultUnitOfWork;

namespace Leistd.UnitOfWork.Tests.Core;

public class UnitOfWorkDatabaseApiTests
{
    [Fact]
    public async Task Complete_saves_and_commits_every_registered_api()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        var firstDatabase = new TrackingDatabaseApi();
        var secondDatabase = new TrackingDatabaseApi();
        var firstTransaction = new TrackingTransactionApi();
        var secondTransaction = new TrackingTransactionApi();

        unitOfWork.AddDatabaseApi("first", firstDatabase);
        unitOfWork.AddDatabaseApi("second", secondDatabase);
        unitOfWork.AddTransactionApi("first", firstTransaction);
        unitOfWork.AddTransactionApi("second", secondTransaction);

        await unitOfWork.CompleteAsync();

        Assert.Equal(1, firstDatabase.SaveCount);
        Assert.Equal(1, secondDatabase.SaveCount);
        Assert.Equal(1, firstTransaction.CommitCount);
        Assert.Equal(1, secondTransaction.CommitCount);
    }

    /// <summary>手动冲刷把变更推到每个数据库 API，但<b>不</b>提交事务。</summary>
    /// <remarks>
    /// 这是自增主键、计算列、换发后的并发标记等"必须先落库才能取值"场景的唯一手段。
    /// 断言的关键是提交次数仍为 0——冲刷不等于提交，原子性不因它削弱。
    /// </remarks>
    [Fact]
    public async Task Manual_save_changes_flushes_without_committing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        var database = new TrackingDatabaseApi();
        var transaction = new TrackingTransactionApi();
        unitOfWork.AddDatabaseApi("only", database);
        unitOfWork.AddTransactionApi("only", transaction);

        await unitOfWork.SaveChangesAsync();
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(2, database.SaveCount);
        Assert.Equal(0, transaction.CommitCount);
        Assert.False(unitOfWork.IsCompleted);
    }

    /// <summary>已回滚时冲刷是空操作，与 <c>CompleteAsync</c> 一致。</summary>
    [Fact]
    public async Task Manual_save_changes_is_a_no_op_after_rollback()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        var database = new TrackingDatabaseApi();
        unitOfWork.AddDatabaseApi("only", database);

        await unitOfWork.RollbackAsync();
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(0, database.SaveCount);
    }

    /// <summary>
    /// 已完成之后冲刷抛异常：事务已经提交，此时 <c>SaveChanges</c> 会开一个新的隐式事务，
    /// 落到本工作单元的原子边界之外。
    /// </summary>
    [Fact]
    public async Task Manual_save_changes_throws_after_completion()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        unitOfWork.AddDatabaseApi("only", new TrackingDatabaseApi());
        await unitOfWork.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.SaveChangesAsync());
    }

    /// <summary>回滚覆盖每个已登记的 API；释放只覆盖事务 API。</summary>
    /// <remarks>
    /// 工作单元只释放它亲手创建的东西，而它亲手创建的一定是事务。数据库 API 是指向
    /// 作用域或宿主所有物的句柄，因此 <c>IDatabaseApi</c> 不要求 <c>IDisposable</c>，
    /// 工作单元也不释放它——释放了就是重复释放。
    /// </remarks>
    [Fact]
    public async Task Rollback_covers_every_api_and_dispose_covers_transactions()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        var databases = new[] { new TrackingDatabaseApi(), new TrackingDatabaseApi() };
        var transactions = new[] { new TrackingTransactionApi(), new TrackingTransactionApi() };

        unitOfWork.AddDatabaseApi("first", databases[0]);
        unitOfWork.AddDatabaseApi("second", databases[1]);
        unitOfWork.AddTransactionApi("first", transactions[0]);
        unitOfWork.AddTransactionApi("second", transactions[1]);

        await unitOfWork.RollbackAsync();
        unitOfWork.Dispose();

        Assert.All(databases, api => Assert.Equal(1, api.RollbackCount));
        Assert.All(transactions, api =>
        {
            Assert.Equal(1, api.RollbackCount);
            Assert.True(api.IsDisposed);
        });
    }

    [Fact]
    public void Database_and_transaction_apis_are_addressed_by_stable_keys()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));
        var firstDatabase = new TrackingDatabaseApi();
        var secondDatabase = new TrackingDatabaseApi();
        var firstTransaction = new TrackingTransactionApi();
        var secondTransaction = new TrackingTransactionApi();

        unitOfWork.AddDatabaseApi("first", firstDatabase);
        unitOfWork.AddDatabaseApi("second", secondDatabase);
        unitOfWork.AddTransactionApi("first", firstTransaction);
        unitOfWork.AddTransactionApi("second", secondTransaction);

        Assert.Same(firstDatabase, unitOfWork.FindDatabaseApi("first"));
        Assert.Same(secondDatabase, unitOfWork.FindDatabaseApi("second"));
        Assert.Same(firstTransaction, unitOfWork.FindTransactionApi("first"));
        Assert.Same(secondTransaction, unitOfWork.FindTransactionApi("second"));
        Assert.Throws<InvalidOperationException>(() => unitOfWork.AddDatabaseApi("first", secondDatabase));
        Assert.Throws<InvalidOperationException>(() => unitOfWork.AddTransactionApi("first", secondTransaction));
    }

    /// <summary>前一个事务已提交、后一个失败时，异常消息必须点明已提交与失败的 key。</summary>
    /// <remarks>
    /// 已提交的事务无法回滚，运维需要知道该核对哪一部分。不为它单列异常类型——
    /// 与普通提交失败的处置方式相同（都是 500、都需要人工核对），信息全部由消息承载；
    /// 使用 <see cref="InvalidOperationException"/> 表达框架状态已违反原子性；详细消息仅写日志，不对 API 调用方暴露。
    /// </remarks>
    [Fact]
    public async Task Failure_after_a_successful_commit_names_the_committed_and_failed_keys()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));

        unitOfWork.AddTransactionApi("first", new TrackingTransactionApi());
        unitOfWork.AddTransactionApi("second", new ThrowingTransactionApi());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CompleteAsync());

        Assert.Contains("partially committed", error.Message, StringComparison.Ordinal);
        Assert.Contains("first", error.Message, StringComparison.Ordinal);
        Assert.Contains("'second' failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("manual reconciliation", error.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    /// <summary>第一个事务就失败时不是部分提交——什么都没提交，回滚足以收场，原异常原样上抛。</summary>
    [Fact]
    public async Task Failure_on_the_first_commit_is_not_reported_as_a_partial_commit()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var unitOfWork = new CoreUnitOfWork(provider, Microsoft.Extensions.Options.Options.Create(new UnitOfWorkOptions()));

        unitOfWork.AddTransactionApi("only", new ThrowingTransactionApi());

        var error = await Record.ExceptionAsync(() => unitOfWork.CompleteAsync());

        Assert.NotNull(error);
        Assert.DoesNotContain("partially committed", error.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingTransactionApi : ITransactionApi
    {
        public Task CommitAsync() => throw new InvalidOperationException("commit failed");

        public void Dispose()
        {
        }
    }

    private sealed class TrackingDatabaseApi : IDatabaseApi, ISupportsSavingChanges, ISupportsRollback
    {
        public int SaveCount { get; private set; }
        public int RollbackCount { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingTransactionApi : ITransactionApi, ISupportsRollback
    {
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public Task CommitAsync()
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => IsDisposed = true;
    }
}
