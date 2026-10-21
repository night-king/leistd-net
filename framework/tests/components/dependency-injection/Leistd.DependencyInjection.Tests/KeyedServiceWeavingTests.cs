using Castle.DynamicProxy;
using Leistd.DependencyInjection.DynamicProxy.Extensions;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.DependencyInjection.Extensions;
using Leistd.DynamicProxy.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.DependencyInjection.Tests;

// 织入把描述符改写成工厂型，键控注册最容易在这一步被静默改成非键控。
public class KeyedServiceWeavingTests
{
    [Fact]
    public void Keyed_registration_survives_weaving_and_stays_intercepted()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICounter, Counter>("primary");
        services.AddSingleton<CountingInterceptor>();
        // 仅按 ServiceType 判定的约定——文档明示它对实现类型未知的描述符同样成立
        services.OnServiceRegistered(context =>
        {
            if (context.ServiceType == typeof(ICounter))
            {
                context.AddInterceptor(typeof(CountingInterceptor));
            }
        });

        var provider = Build(services);

        var resolved = provider.GetKeyedService<ICounter>("primary");
        Assert.NotNull(resolved);

        resolved!.Increment();
        Assert.Equal(1, provider.GetRequiredService<CountingInterceptor>().Calls);
    }

    [Fact]
    public void Keyed_and_unkeyed_registrations_of_the_same_service_stay_separate()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICounter, Counter>("primary");
        services.AddSingleton<ICounter, Counter>();
        services.AddSingleton<CountingInterceptor>();
        services.OnServiceRegistered(context =>
        {
            if (context.ServiceType == typeof(ICounter))
            {
                context.AddInterceptor(typeof(CountingInterceptor));
            }
        });

        var provider = Build(services);

        Assert.NotNull(provider.GetKeyedService<ICounter>("primary"));
        Assert.NotNull(provider.GetService<ICounter>());
        Assert.Null(provider.GetKeyedService<ICounter>("missing"));
    }

    [Fact]
    public void Service_key_is_visible_to_registration_callbacks()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICounter, Counter>("primary");
        services.AddSingleton<ICounter, Counter>();

        var seen = new List<object?>();
        services.OnServiceRegistered(context =>
        {
            if (context.ServiceType == typeof(ICounter))
            {
                seen.Add(context.ServiceKey);
            }
        });

        Build(services);

        Assert.Equal(2, seen.Count);
        Assert.Contains("primary", seen);
        Assert.Contains(null, seen);
    }

    [Fact]
    public void Keyed_factory_registration_receives_its_key_when_woven()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICounter>("primary", (_, key) => new Counter { Tag = key?.ToString() });
        services.AddSingleton<CountingInterceptor>();
        services.OnServiceRegistered(context =>
        {
            if (context.ServiceType == typeof(ICounter))
            {
                context.AddInterceptor(typeof(CountingInterceptor));
            }
        });

        var provider = Build(services);

        Assert.Equal("primary", provider.GetRequiredKeyedService<ICounter>("primary").Tag);
    }

    private static IServiceProvider Build(IServiceCollection services)
    {
        var factory = new DynamicProxyServiceRegistrationCallbackFactory();
        return factory.CreateServiceProvider(factory.CreateBuilder(services));
    }

    public interface ICounter
    {
        string? Tag { get; }

        void Increment();
    }

    public class Counter : ICounter
    {
        public string? Tag { get; init; }

        public virtual void Increment()
        {
        }
    }

    public class CountingInterceptor : BaseAsyncInterceptor
    {
        public int Calls { get; private set; }

        protected override async Task InterceptAsync(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        {
            Calls++;
            await proceed(invocation, proceedInfo);
        }

        protected override async Task<TResult> InterceptAsync<TResult>(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
        {
            Calls++;
            return await proceed(invocation, proceedInfo);
        }
    }
}
