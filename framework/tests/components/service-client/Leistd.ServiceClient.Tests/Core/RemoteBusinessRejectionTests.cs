using System.Net;
using System.Text;
using Leistd.ExceptionHandling;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary>远端业务拒绝的公开说明与转交：只有带业务码的 4xx 才能交还调用方，说明只取远端的业务 detail。</summary>
public class RemoteBusinessRejectionTests
{
    private static HttpResponseMessage Problem(HttpStatusCode status, string body) => new(status)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Patch, "https://issuer.internal.example/api/v1/auth/me/preferences"),
        Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
    };

    [Fact]
    public async Task A_business_problem_keeps_its_detail_apart_from_the_diagnostic_message()
    {
        using var response = Problem(HttpStatusCode.Forbidden,
            """{"status":403,"code":"Review:PolicyRefusal","detail":"Localized policy refusal","traceId":"t-1"}""");

        var remote = await response.CreateRemoteErrorAsync();

        Assert.Equal("Localized policy refusal", remote.Detail);
        Assert.Contains("issuer.internal.example", remote.Message);
    }

    [Fact]
    public async Task A_business_rejection_becomes_a_business_exception_with_the_remote_status()
    {
        using var response = Problem(HttpStatusCode.Forbidden,
            """{"status":403,"code":"Review:PolicyRefusal","detail":"Localized policy refusal"}""");
        var remote = await response.CreateRemoteErrorAsync();

        var rejection = Assert.IsType<RemoteBusinessException>(remote.ToBusinessException("Refused."));

        Assert.Equal("Review:PolicyRefusal", rejection.Code);
        Assert.Equal("Localized policy refusal", rejection.Message);
        Assert.Equal(403, ((IHasHttpStatusCode)rejection).HttpStatusCode);
        Assert.Same(remote, rejection.InnerException);
    }

    [Fact]
    public async Task Without_a_remote_detail_the_fallback_message_is_used()
    {
        using var response = Problem(HttpStatusCode.Conflict, """{"status":409,"code":"Review:Conflict"}""");
        var remote = await response.CreateRemoteErrorAsync();

        Assert.Equal("Refused.", remote.ToBusinessException("Refused.")!.Message);
    }

    // 不带业务码的 401/403 分不清是用户凭证还是交换配置出错；5xx 是上游故障：都不交还调用方
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"status":403,"detail":"Forbidden by gateway"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":401,"title":"Unauthorized"}""")]
    [InlineData(HttpStatusCode.BadGateway, """{"status":502,"code":"Gateway:Down","detail":"down"}""")]
    public async Task Other_failures_are_not_turned_into_business_rejections(HttpStatusCode status, string body)
    {
        using var response = Problem(status, body);
        var remote = await response.CreateRemoteErrorAsync();

        Assert.Null(remote.ToBusinessException("Refused."));
    }

    [Fact]
    public async Task A_protocol_failure_has_no_public_detail()
    {
        using var response = Problem(HttpStatusCode.Forbidden, """{"status":403,"detail":"Forbidden by gateway"}""");

        var remote = await response.CreateRemoteErrorAsync();

        Assert.Null(remote.Detail);
    }
}
