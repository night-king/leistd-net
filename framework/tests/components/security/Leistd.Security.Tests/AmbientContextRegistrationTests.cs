using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Leistd.AmbientContext;
using Leistd.Security.AspNetCore;
using Leistd.Security.AspNetCore.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Xunit;

namespace Leistd.Security.Tests;

public class AmbientContextRegistrationTests
{
    private static ClaimsPrincipal Principal(Guid id) =>
        new(new ClaimsIdentity([new Claim("sub", id.ToString())], "Test"));

    // AddAmbientContext() 是非 HTTP 入口（后台作业、消息消费者）的注册面，
    // 必须能独立解析并读到显式建立的主体。
    [Fact]
    public void AddAmbientContext_alone_supports_begin_and_read()
    {
        var sp = new ServiceCollection().AddAmbientContext().BuildServiceProvider();
        var userId = Guid.NewGuid();

        Assert.Null(sp.GetRequiredService<ICurrentUser>().Id);

        using (sp.GetRequiredService<IAmbientContext>().Begin(Principal(userId)))
        {
            Assert.Equal(userId, sp.GetRequiredService<ICurrentUser>().Id);
        }

        Assert.Null(sp.GetRequiredService<ICurrentUser>().Id);
    }

    // 没有底层主体来源：没有 HTTP 上下文时不会去猜。
    [Fact]
    public void Ambient_accessor_has_no_underlying_principal_source()
    {
        var sp = new ServiceCollection().AddAmbientContext().BuildServiceProvider();

        var accessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();

        Assert.IsType<CurrentPrincipalAccessor>(accessor);
        Assert.Null(accessor.Principal);
    }

    // AddSecurity() 覆盖主体来源，且与 AddAmbientContext() 的调用顺序无关。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddSecurity_replaces_the_principal_source_regardless_of_order(bool ambientFirst)
    {
        var services = new ServiceCollection();
        if (ambientFirst)
        {
            services.AddAmbientContext();
            services.AddSecurity();
        }
        else
        {
            services.AddSecurity();
            services.AddAmbientContext();
        }

        var sp = services.BuildServiceProvider();

        Assert.IsType<HttpContextCurrentPrincipalAccessor>(sp.GetRequiredService<ICurrentPrincipalAccessor>());
        Assert.Single(services, d => d.ServiceType == typeof(ICurrentPrincipalAccessor));
    }
}
