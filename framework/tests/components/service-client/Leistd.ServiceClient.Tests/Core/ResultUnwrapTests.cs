using System.Net;
using System.Text;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Leistd.ServiceClient.Tests.TestDoubles;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

public class ResultUnwrapTests
{
    private sealed record OrderDto(Guid Id, string Name);

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string? body, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://demo/api/orders/1"),
        };
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        return response;
    }

    [Fact]
    public async Task Success_envelope_returns_data()
    {
        var id = Guid.NewGuid();
        using var response = Response(HttpStatusCode.OK,
            $$$"""{"code":0,"message":null,"data":{"id":"{{{id}}}","name":"下单"}}""");

        var data = await response.ReadResultAsync<OrderDto>();

        Assert.NotNull(data);
        Assert.Equal(id, data.Id);
        Assert.Equal("下单", data.Name);
    }

    [Fact]
    public async Task Envelope_without_data_and_zero_code_does_not_throw()
    {
        using var response = Response(HttpStatusCode.OK, """{"code":0,"message":"ok"}""");

        await response.ReadResultAsync();
    }

    [Fact]
    public async Task Nonzero_envelope_code_throws_RemoteServiceException()
    {
        using var response = Response(HttpStatusCode.OK, """{"code":50001,"message":"库存不足"}""");

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Equal("50001", exception.ErrorCode);   // 2xx 信封的非零 code 是业务码，按不变文化转字符串
        Assert.Contains("库存不足", exception.Message);
    }

    [Fact]
    public async Task Problem_details_restore_code_trace_id_and_field_errors()
    {
        using var response = Response(HttpStatusCode.UnprocessableContent,
            """
            {"type":"https://err/validation","title":"Unprocessable Entity","status":422,
             "code":"Order:Invalid","message":"参数校验失败","traceId":"00-abc-def-01",
             "errors":[{"field":"name","detail":"必填","code":"Required"}]}
            """);

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Equal(422, exception.RemoteStatusCode);
        Assert.Equal("Order:Invalid", exception.ErrorCode);
        Assert.Equal("00-abc-def-01", exception.RemoteTraceId);
        Assert.Contains("00-abc-def-01", exception.Message);
        var error = Assert.Single(exception.Errors);
        Assert.Equal(("name", "必填", "Required"), (error.Field, error.Detail, error.Code));
    }

    /// <summary>官方 <c>HttpValidationProblemDetails</c> 的字典形 <c>errors</c>：每条消息各成一项，字段名与全部消息都保留。</summary>
    [Fact]
    public async Task Dictionary_shaped_errors_keep_every_message_per_field()
    {
        using var response = Response(HttpStatusCode.BadRequest,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.",
             "status":400,"traceId":"00-abc-def-01",
             "errors":{"Name":["The Name field is required.","Name is too short."],"Quantity":["Must be positive."]}}
            """);

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Equal(
            [("Name", "The Name field is required."), ("Name", "Name is too short."), ("Quantity", "Must be positive.")],
            exception.Errors.Select(error => (error.Field, error.Detail)));
        Assert.All(exception.Errors, error => Assert.Null(error.Code));
    }

    [Fact]
    public async Task Numeric_code_is_not_taken_as_a_business_error_code()
    {
        // 信封模式下协议层失败只有数字 code（即 HTTP 状态）、没有 errorCode；
        // 把它当业务码会让调用方按 "404" 这种字符串分支
        using var response = Response(HttpStatusCode.NotFound,
            """{"status":404,"code":404,"message":"不存在","traceId":"t-2"}""");

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Null(exception.ErrorCode);
        Assert.Equal("t-2", exception.RemoteTraceId);
        Assert.Contains("不存在", exception.Message);
    }

    [Fact]
    public async Task Optional_numeric_envelope_restores_errorCode_instead_of_status_code()
    {
        using var response = Response(HttpStatusCode.Conflict,
            """{"code":409,"errorCode":"Order:Conflict","message":"订单状态冲突","traceId":"t-3"}""");

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Equal(409, exception.RemoteStatusCode);
        Assert.Equal("Order:Conflict", exception.ErrorCode);
        Assert.Equal("t-3", exception.RemoteTraceId);
    }

    [Fact]
    public async Task Non_json_error_response_keeps_a_body_excerpt()
    {
        using var response = Response(HttpStatusCode.BadGateway, "<html>gateway error</html>", "text/html");

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Equal(502, exception.RemoteStatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.Contains("<html>gateway error</html>", exception.ResponseBody);
    }

    [Fact]
    public async Task Empty_body_throws_ServiceClientException()
    {
        using var response = Response(HttpStatusCode.OK, null);

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Contains("response body is empty", exception.Message);
        Assert.Equal(ServiceClientFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task Invalid_json_throws_ServiceClientException()
    {
        using var response = Response(HttpStatusCode.OK, "not-json");

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() => response.ReadResultAsync<OrderDto>());

        Assert.Contains("deserialization failed", exception.Message);
        Assert.Equal(ServiceClientFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Theory]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ResponseEnded)]
    public async Task Interrupted_response_body_is_classified_as_invalid_response(HttpRequestError error)
    {
        using var response = Response(HttpStatusCode.OK, null);
        response.Content = new ThrowingHttpContent(new HttpIOException(error, "response body ended"));

        var exception = await Assert.ThrowsAsync<ServiceClientException>(() => response.ReadContentAsync<OrderDto>());

        Assert.Equal(ServiceClientFailureKind.InvalidResponse, exception.FailureKind);
        var transportException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.IsType<HttpIOException>(transportException.InnerException);
    }

    [Fact]
    public async Task Unwrapped_endpoint_is_deserialized_directly()
    {
        var id = Guid.NewGuid();
        using var response = Response(HttpStatusCode.OK, $$$"""{"id":"{{{id}}}","name":"raw"}""");

        var data = await response.ReadContentAsync<OrderDto>();

        Assert.NotNull(data);
        Assert.Equal(id, data.Id);
    }

    [Fact]
    public async Task Unwrapped_endpoint_error_is_restored_as_well()
    {
        using var response = Response(HttpStatusCode.NotFound,
            """{"status":404,"code":"Order:NotFound","message":"不存在","traceId":"t-1"}""");

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadContentAsync<OrderDto>());

        Assert.Equal("Order:NotFound", exception.ErrorCode);
        Assert.Equal("t-1", exception.RemoteTraceId);
    }
}
