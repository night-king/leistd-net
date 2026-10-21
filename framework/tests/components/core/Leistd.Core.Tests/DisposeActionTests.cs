using Leistd.Disposables;
using Xunit;

namespace Leistd.Core.Tests;

/// <summary>作用域还原的通用载体：全框架的 <c>Change()</c> 都返回它。</summary>
public class DisposeActionTests
{
    [Fact]
    public void Action_runs_on_dispose()
    {
        var ran = 0;

        new DisposeAction(() => ran++).Dispose();

        Assert.Equal(1, ran);
    }

    // 还原动作往往是弹栈或写回父值：执行两次会把外层作用域一并还原掉。
    // 这条幂等保证是 AmbientContext 嵌套作用域正确性的前提。
    [Fact]
    public void Repeated_dispose_runs_the_action_only_once()
    {
        var ran = 0;
        var action = new DisposeAction(() => ran++);

        action.Dispose();
        action.Dispose();
        action.Dispose();

        Assert.Equal(1, ran);
    }

    // 并发释放同样只能有一方执行——实现用 Interlocked.Exchange 取出并置空。
    [Fact]
    public async Task Concurrent_dispose_runs_the_action_only_once()
    {
        var ran = 0;
        var action = new DisposeAction(() => Interlocked.Increment(ref ran));

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(action.Dispose)));

        Assert.Equal(1, ran);
    }

    [Fact]
    public void Null_action_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new DisposeAction(null!));
    }

    // 动作抛出时异常必须透出：吞掉它等于让"还原失败"变成静默的上下文泄漏。
    [Fact]
    public void Exception_from_the_action_propagates()
    {
        var action = new DisposeAction(() => throw new InvalidOperationException("restore failed"));

        Assert.Throws<InvalidOperationException>(action.Dispose);
        // 已经取出过动作，再次释放不重复抛
        action.Dispose();
    }
}
