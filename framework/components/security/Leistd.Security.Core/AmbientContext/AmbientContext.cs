using System.Security.Claims;
using Leistd.AmbientContext;
using Leistd.Security.Claims;

namespace Leistd.Security.AmbientContext;

internal sealed class AmbientContext(
    ICurrentPrincipalAccessor principalAccessor,
    IEnumerable<IAmbientContextContributor> contributors) : IAmbientContext
{
    public IDisposable Begin(ClaimsPrincipal principal, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // 主体先建立：其它维度可能读 ICurrentUser（如按当前用户取租户）。
        var entered = new List<IDisposable> { principalAccessor.Change(principal) };

        try
        {
            var context = new AmbientContextEnterContext(principal, correlationId);
            foreach (var contributor in contributors)
            {
                if (contributor.Enter(context) is { } scope)
                {
                    entered.Add(scope);
                }
            }
        }
        catch
        {
            // 部分建立即回滚：调用方会以为整个作用域可信，而实际只有部分维度生效
            DisposeAll(entered);
            throw;
        }

        return new AmbientContextScope(entered);
    }

    public AmbientContextSnapshot Capture()
    {
        var states = new Dictionary<Type, object?>();
        foreach (var contributor in contributors)
        {
            states[contributor.GetType()] = contributor.Capture();
        }

        return new AmbientContextSnapshot(principalAccessor.Principal, states);
    }

    public IDisposable Restore(AmbientContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 与 Begin 同序：主体先还原，其它维度可能读它。匿名捕获也要显式还原成空主体，
        // 否则执行流会继承线程上残留的别的主体。
        var entered = new List<IDisposable>
        {
            principalAccessor.Change(snapshot.Principal ?? new ClaimsPrincipal(new ClaimsIdentity()))
        };

        try
        {
            foreach (var contributor in contributors)
            {
                snapshot.States.TryGetValue(contributor.GetType(), out var state);
                if (contributor.Restore(state) is { } scope)
                {
                    entered.Add(scope);
                }
            }
        }
        catch
        {
            DisposeAll(entered);
            throw;
        }

        return new AmbientContextScope(entered);
    }

    // 逆序释放：还原动作往往是写回父值，与建立顺序相反才能层层退回。
    private static void DisposeAll(List<IDisposable> entered)
    {
        for (var i = entered.Count - 1; i >= 0; i--)
        {
            entered[i].Dispose();
        }
    }

    private sealed class AmbientContextScope(List<IDisposable> entered) : IDisposable
    {
        private List<IDisposable>? _entered = entered;

        public void Dispose()
        {
            // 幂等：还原是弹栈式的，执行两次会把外层作用域也一并还原掉。
            var pending = Interlocked.Exchange(ref _entered, null);
            if (pending is not null)
            {
                DisposeAll(pending);
            }
        }
    }
}
