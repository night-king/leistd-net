using Leistd.DependencyInjection.DynamicProxy.Extensions;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Leistd.DependencyInjection.Tests.KeyedServiceWeavingTests;

namespace Leistd.DependencyInjection.Tests;

/// <summary>组件的 <c>AddXxx</c> 被调用两次时回调会登记两次；同一拦截器不得因此被织入两层。</summary>
public class InterceptorDeduplicationTests
{
    [Fact]
    public void The_same_interceptor_registered_twice_is_woven_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICounter, Counter>();
        services.AddSingleton<CountingInterceptor>();
        for (var i = 0; i < 2; i++)
        {
            services.OnServiceRegistered(context =>
            {
                if (context.ServiceType == typeof(ICounter))
                {
                    context.AddInterceptor(typeof(CountingInterceptor));
                }
            });
        }

        var factory = new DynamicProxyServiceRegistrationCallbackFactory();
        var provider = factory.CreateServiceProvider(factory.CreateBuilder(services));

        provider.GetRequiredService<ICounter>().Increment();

        Assert.Equal(1, provider.GetRequiredService<CountingInterceptor>().Calls);
    }
}
