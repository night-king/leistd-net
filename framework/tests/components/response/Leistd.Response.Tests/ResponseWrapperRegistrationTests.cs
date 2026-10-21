using Leistd.Response.AspNetCore;
using Leistd.Response.AspNetCore.Filters;
using Leistd.ExceptionHandling.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Response.Tests;

/// <summary>统一响应包装的注册面。</summary>
/// <remarks>
/// 挂在 <see cref="IMvcBuilder"/> 而不是 <see cref="IServiceCollection"/> 是刻意的设计约束
/// （组件不替宿主调 <c>AddControllers()</c>）。这条约束只写在注释里就会在某次
/// "顺手加个 AddResponseWrapper(this IServiceCollection)" 时失效。
/// </remarks>
public class ResponseWrapperRegistrationTests
{
    private static (ServiceProvider Provider, IMvcBuilder Mvc) BuildMvc()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var mvc = services.AddControllers();
        return (services.BuildServiceProvider(), mvc);
    }

    [Fact]
    public void Wrapper_filter_is_added_to_the_mvc_filter_chain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddResponseWrapper();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MvcOptions>>().Value;

        Assert.Contains(options.Filters, f =>
            f is TypeFilterAttribute t && t.ImplementationType == typeof(ResultWrapperFilter));
    }

    [Fact]
    public void Registration_returns_the_same_builder_for_chaining()
    {
        var (provider, mvc) = BuildMvc();
        using var _ = provider;

        Assert.Same(mvc, mvc.AddResponseWrapper());
    }

    // 宿主自己的 MVC 配置必须仍然生效：组件只往过滤器链上追加，
    // 不得重建 MVC 入口——那会让宿主的 AddJsonOptions 之类被覆盖或顺序错乱。
    [Fact]
    public void Host_mvc_configuration_survives_the_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers(o => o.MaxModelValidationErrors = 7).AddResponseWrapper();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(7, provider.GetRequiredService<IOptions<MvcOptions>>().Value.MaxModelValidationErrors);
    }

    // 重复调用会挂两遍过滤器，同一个响应被包两层。宿主组合根拆分时很容易发生。
    [Fact]
    public void Registering_twice_adds_the_filter_only_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var mvc = services.AddControllers();
        mvc.AddResponseWrapper();
        mvc.AddResponseWrapper();

        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        Assert.Single(filters, f =>
            f is TypeFilterAttribute t && t.ImplementationType == typeof(ResultWrapperFilter));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Envelope_writer_goes_first_independent_of_registration_order(bool wrapperFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var mvc = services.AddControllers();
        if (wrapperFirst)
        {
            mvc.AddResponseWrapper();
            mvc.ConfigureApiValidation();
        }
        else
        {
            mvc.ConfigureApiValidation();
            mvc.AddResponseWrapper();
        }

        // 宿主在包装器之后才注册默认写入器（AddGlobalExceptionHandler 内部调 AddProblemDetails）也不能抢到前面
        services.AddProblemDetails();

        using var provider = services.BuildServiceProvider();
        Assert.Equal("ResultProblemDetailsWriter",
            provider.GetServices<IProblemDetailsWriter>().First().GetType().Name);
        Assert.Single(provider.GetRequiredService<IOptions<MvcOptions>>().Value.ModelMetadataDetailsProviders,
            metadata => metadata is SystemTextJsonValidationMetadataProvider);
    }
}
