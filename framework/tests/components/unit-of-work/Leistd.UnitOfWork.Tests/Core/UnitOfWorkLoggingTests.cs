using Leistd.EventBus.Local;
using Leistd.UnitOfWork.Database;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary>日志级别区分"真正的提交失败"与"调用方取消、正常回滚"。</summary>
/// <remarks>
/// 浏览器主动中断请求、每一次业务拒绝引起的回滚若分别记成 Error 与 Warning，
/// 真正需要被看见的提交失败就淹没在里面。
/// </remarks>
public sealed class UnitOfWorkLoggingTests
{
    /// <summary>调用方在提交前取消：什么都没提交，不是故障，只记 Debug。</summary>
    [Fact]
    public async Task A_caller_cancellation_before_commit_is_not_logged_as_an_error()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.CompleteAsync(cancelled.Token));
        uow.Dispose();

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Debug && record.Message.Contains("cancelled by the caller", StringComparison.Ordinal));
    }

    /// <summary>令牌没有取消的取消异常（如数据库超时）不是调用方取消，照样是提交失败。</summary>
    [Fact]
    public async Task A_cancellation_exception_the_caller_did_not_request_is_still_an_error()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.AddTransactionApi("db", new FailingCommitTransactionApi(new OperationCanceledException("timeout")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.CompleteAsync());
        uow.Dispose();

        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Error && record.Message.Contains("commit failed", StringComparison.Ordinal));
    }

    /// <summary>提交开始之后的取消异常是真故障，即使调用方令牌恰好已取消。</summary>
    /// <remarks>按"令牌已取消"一律降为 Debug 并写成"提交前取消"，会把提交中或提交后处理器的失败藏起来。</remarks>
    [Fact]
    public async Task A_cancellation_after_the_commit_started_is_still_an_error()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        using var caller = new CancellationTokenSource();
        uow.AddTransactionApi("db", new CancellingCommitTransactionApi(caller));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.CompleteAsync(caller.Token));
        uow.Dispose();

        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Error && record.Message.Contains("commit failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Message.Contains("cancelled by the caller", StringComparison.Ordinal));
    }

    /// <summary>非事务工作单元被调用方取消：已保存的部分不会回滚，记 Warning，不说"什么都没提交"。</summary>
    [Fact]
    public async Task A_caller_cancellation_of_a_non_transactional_unit_of_work_is_a_warning()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin(new UnitOfWorkOptions { IsTransactional = false });
        uow.AddDatabaseApi("db", new CancellingSaveDatabaseApi());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uow.CompleteAsync(cancelled.Token));
        uow.Dispose();

        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Warning && record.Message.Contains("not rolled back", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Message.Contains("nothing was committed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_commit_failure_is_logged_as_an_error()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();
        uow.AddTransactionApi("db", new FailingCommitTransactionApi(new InvalidOperationException("disk full")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.CompleteAsync());
        uow.Dispose();

        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Error && record.Message.Contains("commit failed", StringComparison.Ordinal));
    }

    /// <summary>回滚是结果不是原因：每一次业务拒绝都会回滚，只记 Debug。</summary>
    [Fact]
    public async Task A_rollback_is_logged_at_debug()
    {
        var (provider, logs) = Build();
        await using var _ = provider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin();

        await uow.RollbackAsync();
        uow.Dispose();

        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
        Assert.Contains(logs.GetSnapshot(), record =>
            record.Level == LogLevel.Debug && record.Message.Contains("rolling back", StringComparison.Ordinal));
    }

    private static (ServiceProvider Provider, FakeLogCollector Logs) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddFakeLogging();
        services.AddLocalEventBus();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetFakeLogCollector());
    }

    // 提交过程中调用方取消：令牌已取消，但提交已经开始
    private sealed class CancellingCommitTransactionApi(CancellationTokenSource caller) : ITransactionApi
    {
        public Task CommitAsync()
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        }

        public void Dispose() { }
    }

    private sealed class CancellingSaveDatabaseApi : IDatabaseApi, ISupportsSavingChanges
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
            => Task.FromCanceled(cancellationToken);
    }

    private sealed class FailingCommitTransactionApi(Exception exception) : ITransactionApi
    {
        public Task CommitAsync() => Task.FromException(exception);

        public void Dispose() { }
    }
}
