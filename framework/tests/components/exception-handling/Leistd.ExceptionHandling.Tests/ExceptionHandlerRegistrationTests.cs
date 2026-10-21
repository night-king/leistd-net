using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.AspNetCore.Handlers;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary><c>AddGlobalExceptionHandler</c> 与 <c>ConfigureApiValidation</c> 的注册面。</summary>
/// <remarks>
/// <see cref="IExceptionHandler"/> 是多实现链：业务异常处理器登记两份时，官方中间件会按顺序问两遍，
/// 宿主自己的处理器也必须与它并存而不是被替换。
/// </remarks>
public sealed class ExceptionHandlerRegistrationTests
{
    [Fact]
    public void Registration_adds_one_singleton_business_exception_handler()
    {
        var services = new ServiceCollection();

        services.AddGlobalExceptionHandler();

        services.AssertSingle<IExceptionHandler>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IExceptionHandler, BusinessExceptionHandler>();
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddGlobalExceptionHandler());

    [Fact]
    public void Repeated_registration_keeps_one_business_exception_handler()
    {
        var services = new ServiceCollection();

        services.AddGlobalExceptionHandler().AddGlobalExceptionHandler();

        services.AssertSingle<IExceptionHandler>(ServiceLifetime.Singleton);
    }

    // 宿主自己的异常处理器与组件的并存：按实现类型去重，不是按服务类型替换
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_host_exception_handler_coexists_in_either_order(bool hostFirst)
    {
        var services = new ServiceCollection();
        if (hostFirst)
        {
            services.AddExceptionHandler<HostExceptionHandler>();
        }

        services.AddGlobalExceptionHandler();

        if (!hostFirst)
        {
            services.AddExceptionHandler<HostExceptionHandler>();
        }

        var handlers = services.Where(d => d.ServiceType == typeof(IExceptionHandler)).Select(d => d.ImplementationType).ToList();
        Assert.Equal(2, handlers.Count);
        Assert.Contains(typeof(BusinessExceptionHandler), handlers);
        Assert.Contains(typeof(HostExceptionHandler), handlers);
    }

    [Fact]
    public void Api_validation_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddControllers().ConfigureApiValidation());

    private sealed class HostExceptionHandler : IExceptionHandler
    {
        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}
