using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Leistd.TestBase.Doubles;

/// <summary>在被拦截上下文的首次 SaveChanges 之前执行一次给定动作，用于构造确定性的写入交错。</summary>
/// <remarks>动作通常经另一个上下文（共用同一连接或事务）抢先写库，模拟并发写入方。</remarks>
public sealed class RunOnceBeforeSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
{
    private bool _executed;

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!_executed)
        {
            _executed = true;
            await action();
        }

        return result;
    }
}
