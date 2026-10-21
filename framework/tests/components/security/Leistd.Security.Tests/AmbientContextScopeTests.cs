using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Leistd.AmbientContext;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Xunit;

namespace Leistd.Security.Tests;

public class AmbientContextScopeTests
{
    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "Test"));

    // 建立顺序与释放顺序必须相反：还原动作是写回父值，同序释放会让外层拿到内层的值。
    [Fact]
    public void Contributors_are_released_in_reverse_order()
    {
        var log = new List<string>();
        var sp = Build(log, new RecordingContributor("a", log), new RecordingContributor("b", log));

        using (sp.GetRequiredService<IAmbientContext>().Begin(Principal()))
        {
        }

        Assert.Equal(["enter:a", "enter:b", "dispose:b", "dispose:a", "dispose:principal"], log);
    }

    // 留一半上下文比完全不建立更危险：调用方会以为整个作用域可信。
    [Fact]
    public void A_failing_contributor_rolls_back_the_ones_already_entered()
    {
        var log = new List<string>();
        var sp = Build(log, new RecordingContributor("a", log), new ThrowingContributor());
        var ambient = sp.GetRequiredService<IAmbientContext>();

        Assert.Throws<InvalidOperationException>(() => ambient.Begin(Principal()));

        Assert.Equal(["enter:a", "dispose:a", "dispose:principal"], log);
        Assert.Null(sp.GetRequiredService<ICurrentUser>().Id);
    }

    // 释放是弹栈式的，执行两次会把外层作用域也一并还原掉。
    [Fact]
    public void Disposing_the_scope_twice_does_not_unwind_the_outer_scope()
    {
        var sp = Build([]);
        var ambient = sp.GetRequiredService<IAmbientContext>();
        var currentUser = sp.GetRequiredService<ICurrentUser>();

        var outer = Principal();
        using (ambient.Begin(outer))
        {
            var inner = ambient.Begin(Principal());
            inner.Dispose();
            inner.Dispose();

            Assert.Equal(Guid.Parse(outer.FindFirst("sub")!.Value), currentUser.Id);
        }
    }

    private static ServiceProvider Build(List<string> log, params IAmbientContextContributor[] contributors)
    {
        var services = new ServiceCollection().AddAmbientContext();
        services.AddSingleton<ICurrentPrincipalAccessor>(new LoggingPrincipalAccessor(log));
        foreach (var contributor in contributors)
        {
            services.AddSingleton<IAmbientContextContributor>(contributor);
        }
        return services.BuildServiceProvider();
    }

    private sealed class RecordingContributor(string name, List<string> log) : IAmbientContextContributor
    {
        public IDisposable? Enter(AmbientContextEnterContext context)
        {
            log.Add($"enter:{name}");
            return new Leistd.Disposables.DisposeAction(() => log.Add($"dispose:{name}"));
        }
    }

    private sealed class ThrowingContributor : IAmbientContextContributor
    {
        public IDisposable? Enter(AmbientContextEnterContext context) =>
            throw new InvalidOperationException("contributor failed");
    }

    // 直接实现接口而不是派生 CurrentPrincipalAccessor：AmbientContext 经接口调用 Change，
    // 用 new 隐藏基类方法根本不会被调到。
    private sealed class LoggingPrincipalAccessor(List<string> log) : ICurrentPrincipalAccessor
    {
        private readonly AsyncLocal<ClaimsPrincipal?> _current = new();

        public ClaimsPrincipal? Principal => _current.Value;

        public IDisposable Change(ClaimsPrincipal principal)
        {
            var parent = _current.Value;
            _current.Value = principal;
            return new Leistd.Disposables.DisposeAction(() =>
            {
                _current.Value = parent;
                log.Add("dispose:principal");
            });
        }
    }
}
