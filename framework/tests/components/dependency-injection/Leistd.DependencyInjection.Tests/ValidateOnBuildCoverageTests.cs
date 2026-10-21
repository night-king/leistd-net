using Castle.DynamicProxy;
using Leistd.DependencyInjection.DynamicProxy.Extensions;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.DependencyInjection.Extensions;
using Leistd.DynamicProxy.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.DependencyInjection.Tests;

// 织入把描述符改写成工厂型，Microsoft DI 就再也看不到它的构造函数图。
// 若不在改写前补校验，开着 ValidateOnBuild 恰好漏掉 AOP 服务。
//
// 这条补覆盖的前提是注册回调为纯函数：回调里若再注册服务（如在回调里注册仓储），
// 预校验会把尚未注册的依赖误报成缺失。仓储由 AddDddDbContext<T>() 显式注册，前提成立。
public class ValidateOnBuildCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_dependency_is_reported_whether_or_not_the_service_is_woven(bool weave)
    {
        Assert.ThrowsAny<Exception>(() => Build(weave, registerDependency: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Satisfiable_graph_still_builds(bool weave)
    {
        var provider = Build(weave, registerDependency: true);

        Assert.NotNull(provider.GetRequiredService<IService>());
    }

    [Fact]
    public void Weaving_is_still_applied_after_the_validation_pass()
    {
        var provider = Build(weave: true, registerDependency: true);

        Assert.StartsWith("Castle.Proxies", provider.GetRequiredService<IService>().GetType().Namespace);
    }

    private static IServiceProvider Build(bool weave, bool registerDependency)
    {
        var services = new ServiceCollection();
        if (registerDependency)
        {
            services.AddSingleton<IDependency, Dependency>();
        }

        services.AddTransient<IService, ServiceWithDependency>();
        services.AddSingleton<NoopInterceptor>();
        if (weave)
        {
            services.OnServiceRegistered(context =>
            {
                if (context.ServiceType == typeof(IService))
                {
                    context.AddInterceptor(typeof(NoopInterceptor));
                }
            });
        }

        var factory = new DynamicProxyServiceRegistrationCallbackFactory(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        return factory.CreateServiceProvider(factory.CreateBuilder(services));
    }

    public interface IDependency;

    public class Dependency : IDependency;

    public interface IService;

    public class ServiceWithDependency(IDependency dependency) : IService
    {
        public IDependency Dependency { get; } = dependency;
    }

    public class NoopInterceptor : BaseAsyncInterceptor
    {
        protected override async Task InterceptAsync(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task> proceed)
            => await proceed(invocation, proceedInfo);

        protected override async Task<TResult> InterceptAsync<TResult>(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
            => await proceed(invocation, proceedInfo);
    }
}
