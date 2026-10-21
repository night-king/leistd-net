using Leistd.ServiceClient.Abstractions;
using Leistd.ServiceClient.AspNetCore;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.ServiceClient.Tests.AspNetCore;

/// <summary><c>AddUserAccessTokenAccessor</c> 的注册面：一个单例读取器，宿主先注册的实现不被替换。</summary>
/// <remarks>
/// 读取器在发送请求时才读 <see cref="HttpContext"/>，因此可以是单例；被池化的处理器捕获的也只是这一个无状态实例。
/// </remarks>
public sealed class UserAccessTokenAccessorRegistrationTests
{
    [Fact]
    public void Registration_adds_a_singleton_accessor_and_the_http_context_accessor()
    {
        var services = new ServiceCollection();

        services.AddUserAccessTokenAccessor("Bearer");

        services.AssertSingle<IUserAccessTokenAccessor>(ServiceLifetime.Singleton);
        services.AssertSingle<IHttpContextAccessor>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddUserAccessTokenAccessor("Bearer"));

    // 非 Web 宿主或自定义证明来源先注册自己的实现，组件不得把它换回按 HttpContext 读取
    [Fact]
    public void A_host_registered_accessor_is_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserAccessTokenAccessor, HostAccessor>();

        services.AddUserAccessTokenAccessor("Bearer");

        services.AssertResolvesTo<IUserAccessTokenAccessor, HostAccessor>();
    }

    [Fact]
    public void Authenticated_ticket_registration_is_idempotent_and_keeps_a_host_accessor()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddAuthenticatedUserAccessTokenAccessor("Session"));
        var services = new ServiceCollection();
        services.AddSingleton<IUserAccessTokenAccessor, HostAccessor>();
        services.AddAuthenticatedUserAccessTokenAccessor("Session");
        services.AssertSingle<IHttpContextAccessor>(ServiceLifetime.Singleton);
        services.AssertResolvesTo<IUserAccessTokenAccessor, HostAccessor>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_scheme_is_rejected(string scheme)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddUserAccessTokenAccessor(scheme));
    }

    private sealed class HostAccessor : IUserAccessTokenAccessor
    {
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>("host");
    }
}
