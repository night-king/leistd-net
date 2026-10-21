using System.Net;
using Leistd.Security.AspNetCore;
using Leistd.Security.RequestContext;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Security.Tests;

public sealed class RequestClientInfoTests
{
    [Fact]
    public void Registration_is_idempotent_transient_and_keeps_a_host_implementation()
    {
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddRequestClientInfo());
        var services = new ServiceCollection().AddRequestClientInfo();
        services.AssertSingle<IRequestClientInfo>(ServiceLifetime.Transient);
        services.AssertSingle<IHttpContextAccessor>(ServiceLifetime.Singleton);
        var custom = new ServiceCollection().AddTransient<IRequestClientInfo, HostClientInfo>().AddRequestClientInfo();
        custom.AssertResolvesTo<IRequestClientInfo, HostClientInfo>();
    }

    [Fact]
    public async Task Values_follow_the_current_request_and_native_forwarded_headers()
    {
        using var provider = new ServiceCollection().AddRequestClientInfo().BuildServiceProvider();
        var contexts = provider.GetRequiredService<IHttpContextAccessor>();
        var info = provider.GetRequiredService<IRequestClientInfo>();
        Assert.Null(info.IpAddress);
        Assert.Null(info.UserAgent);
        var request = new DefaultHttpContext();
        request.Connection.RemoteIpAddress = IPAddress.Loopback;
        request.Request.Headers.UserAgent = "browser-one";
        request.Request.Headers["X-Forwarded-For"] = "192.0.2.1";
        contexts.HttpContext = request;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        options.KnownProxies.Add(IPAddress.Loopback);
        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options))
            .Invoke(request);
        Assert.Equal("192.0.2.1", info.IpAddress);
        Assert.Equal("browser-one", info.UserAgent);

        contexts.HttpContext = new DefaultHttpContext();
        contexts.HttpContext.Request.Headers.UserAgent = " ";
        Assert.Null(info.IpAddress);
        Assert.Null(info.UserAgent);
        contexts.HttpContext = null;
        Assert.Null(info.UserAgent);
    }

    private sealed class HostClientInfo : IRequestClientInfo
    {
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
