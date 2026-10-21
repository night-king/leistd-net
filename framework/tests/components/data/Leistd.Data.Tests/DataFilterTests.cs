using Leistd.Data.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Data.Tests;

/// <summary>数据过滤器的嵌套开关：软删除与租户隔离都建立在它之上，还原错一次就是越权读。</summary>
public class DataFilterTests
{
    private interface ISoftDeleteMarker;
    private interface ITenantMarker;

    private static IDataFilter NewFilter()
    {
        var provider = new ServiceCollection().AddDataFilters().BuildServiceProvider();
        return provider.GetRequiredService<IDataFilter>();
    }

    [Fact]
    public void Standalone_registration_is_idempotent_and_preserves_host_filters()
    {
        var services = new ServiceCollection().AddDataFilters().AddDataFilters();
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services,
            descriptor => descriptor.ServiceType == typeof(IDataFilter)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services,
            descriptor => descriptor.ServiceType == typeof(IDataFilter<>)).Lifetime);
        using var defaults = services.BuildServiceProvider();
        Assert.True(defaults.GetRequiredService<IDataFilter>().IsEnabled<ITenantMarker>());

        var host = NewFilter();
        var custom = new ServiceCollection();
        custom.AddSingleton(host);
        custom.AddSingleton(typeof(IDataFilter<>), typeof(HostDataFilter<>));
        custom.AddDataFilters().AddDataFilters();
        using var provider = custom.BuildServiceProvider();
        Assert.Same(host, provider.GetRequiredService<IDataFilter>());
        Assert.IsType<HostDataFilter<ITenantMarker>>(provider.GetRequiredService<IDataFilter<ITenantMarker>>());
    }

    private sealed class HostDataFilter<TFilter> : DataFilter<TFilter> where TFilter : class;

    // 默认启用：过滤器的存在意义就是默认拦住，需要放行时显式开口子。
    [Fact]
    public void Filters_are_enabled_by_default()
    {
        Assert.True(NewFilter().IsEnabled<ISoftDeleteMarker>());
    }

    [Fact]
    public void Disable_applies_only_inside_the_returned_scope()
    {
        var filter = NewFilter();

        using (filter.Disable<ISoftDeleteMarker>())
        {
            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
        }

        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
    }

    // 嵌套逐层还原：内层重新启用后退出，必须回到外层的"禁用"，而不是回到默认的"启用"。
    // 这一条错了，一个本该继续看到已删除数据的作用域会突然被过滤，或者反过来。
    [Fact]
    public void Nested_scopes_unwind_to_the_enclosing_state_not_to_the_default()
    {
        var filter = NewFilter();

        using (filter.Disable<ISoftDeleteMarker>())
        {
            using (filter.Enable<ISoftDeleteMarker>())
            {
                Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
            }

            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
        }

        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
    }

    // 标记类型之间互不影响：关掉软删除过滤不得顺带关掉租户隔离。
    [Fact]
    public void Markers_are_independent()
    {
        var filter = NewFilter();

        using (filter.Disable<ISoftDeleteMarker>())
        {
            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
            Assert.True(filter.IsEnabled<ITenantMarker>());
        }
    }

    // 同一标记必须复用同一个 DataFilter<T> 实例，否则两次 Disable 各自记在不同实例上，
    // 状态互相看不见。
    [Fact]
    public void The_same_marker_resolves_to_the_same_underlying_filter()
    {
        var filter = NewFilter();

        using (filter.Disable<ISoftDeleteMarker>())
        {
            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
            using (filter.Disable<ISoftDeleteMarker>())
            {
                Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
            }
            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
        }
    }

    // 状态挂在 AsyncLocal 上：并行分支各自独立，不能互相污染。
    // 两个信号把交错顺序钉死：分支 B 一定在分支 A 的禁用作用域打开期间读取，
    // 状态一旦改为跨分支共享（静态字段或普通字段），B 就会读到 A 的禁用而变红。
    [Fact]
    public async Task Scopes_do_not_leak_across_async_branches()
    {
        var filter = NewFilter();
        var disabledInBranchA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedInBranchB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var branchA = Task.Run(async () =>
        {
            using (filter.Disable<ISoftDeleteMarker>())
            {
                disabledInBranchA.SetResult();
                await observedInBranchB.Task;
                return filter.IsEnabled<ISoftDeleteMarker>();
            }
        });
        var branchB = Task.Run(async () =>
        {
            await disabledInBranchA.Task;
            var enabled = filter.IsEnabled<ISoftDeleteMarker>();
            observedInBranchB.SetResult();
            return enabled;
        });

        Assert.False(await branchA);
        Assert.True(await branchB);
        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
    }

    // 父流程先读过一次状态，再分出并行分支：分支 A 的禁用不得被兄弟分支 B 看到，也不得回流到父流程。
    // 状态若是挂在 AsyncLocal 上的可变对象，父流程那次读取会让两个分支捕获同一个实例，
    // A 的修改就会穿过执行上下文的复制泄漏出去；每次开关都必须写入新值。
    [Fact]
    public async Task A_branch_scope_does_not_leak_to_siblings_after_the_parent_has_read_the_state()
    {
        var filter = NewFilter();
        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
        var disabledInBranchA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedElsewhere = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var branchA = Task.Run(async () =>
        {
            using (filter.Disable<ISoftDeleteMarker>())
            {
                disabledInBranchA.SetResult();
                await observedElsewhere.Task;
                return filter.IsEnabled<ISoftDeleteMarker>();
            }
        });
        var branchB = Task.Run(async () =>
        {
            await disabledInBranchA.Task;
            return filter.IsEnabled<ISoftDeleteMarker>();
        });

        var enabledInBranchB = await branchB;
        await disabledInBranchA.Task;
        var enabledInParent = filter.IsEnabled<ISoftDeleteMarker>();
        observedElsewhere.SetResult();

        Assert.True(enabledInBranchB);
        Assert.True(enabledInParent);
        Assert.False(await branchA);
    }

    // 分支内的多层嵌套按进入的逆序逐层还原，且全程不影响父流程已经读到的状态。
    [Fact]
    public async Task Nested_scopes_inside_a_branch_unwind_in_order_without_touching_the_parent()
    {
        var filter = NewFilter();
        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());

        var observed = await Task.Run(() =>
        {
            var states = new List<bool>();
            using (filter.Disable<ISoftDeleteMarker>())
            {
                states.Add(filter.IsEnabled<ISoftDeleteMarker>());
                using (filter.Enable<ISoftDeleteMarker>())
                {
                    states.Add(filter.IsEnabled<ISoftDeleteMarker>());
                    using (filter.Disable<ISoftDeleteMarker>())
                    {
                        states.Add(filter.IsEnabled<ISoftDeleteMarker>());
                    }
                    states.Add(filter.IsEnabled<ISoftDeleteMarker>());
                }
                states.Add(filter.IsEnabled<ISoftDeleteMarker>());
            }
            states.Add(filter.IsEnabled<ISoftDeleteMarker>());
            return states;
        });

        Assert.Equal([false, true, false, true, false, true], observed);
        Assert.True(filter.IsEnabled<ISoftDeleteMarker>());
    }

    // 重复释放同一个作用域会多还原一层，把外层的状态也还原掉。
    // DisposeAction 的幂等保证正是为了挡住这个，这里从过滤器一侧再钉一次。
    [Fact]
    public void Disposing_an_inner_scope_twice_does_not_unwind_the_outer_one()
    {
        var filter = NewFilter();

        using (filter.Disable<ISoftDeleteMarker>())
        {
            var inner = filter.Enable<ISoftDeleteMarker>();
            inner.Dispose();
            inner.Dispose();

            Assert.False(filter.IsEnabled<ISoftDeleteMarker>());
        }
    }
}
