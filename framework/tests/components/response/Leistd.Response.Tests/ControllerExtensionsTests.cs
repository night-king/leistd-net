using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Leistd.ExceptionHandling;
using Leistd.Response.AspNetCore.Extensions;
using Leistd.Response.Wrappers;
using Xunit;

namespace Leistd.Response.Tests;

public class ControllerExtensionsTests
{
    private sealed class TestController : ControllerBase
    {
        public TestController() => ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { TraceIdentifier = "test-trace" }
        };
    }

    // 信封里字段错误的线上形状必须与 ProblemDetails 的 errors 一致，否则同一个服务会因为
    // 启用信封与否而吐出两种形状。
    [Fact]
    public void Field_errors_serialize_to_detail_field_code()
    {
        var envelope = Result.Fail(422, "invalid") with { Errors = [new ErrorItem("is required", "name", null)] };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // code 为空时省略，与 ProblemDetails 的 errors 逐字段一致。
        Assert.Contains("\"errors\":[{\"detail\":\"is required\",\"field\":\"name\"}]", json);
    }

    [Fact]
    public void OkResult_wraps_data_with_zero_code()
    {
        var result = Assert.IsType<OkObjectResult>(new TestController().OkResult(new { Id = 1 }));

        Assert.Equal(0, Assert.IsAssignableFrom<Result>(result.Value).Code);
    }

    [Fact]
    public void Optional_failure_fields_are_omitted_without_host_wide_null_suppression()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var success = JsonSerializer.Serialize(Result.Ok(), options);
        var failure = JsonSerializer.Serialize(Result.Fail(409, "Conflict") with
        {
            TraceId = "trace-id",
            ErrorCode = "Order:Conflict"
        }, options);

        Assert.DoesNotContain("traceId", success);
        Assert.DoesNotContain("errorCode", success);
        Assert.DoesNotContain("errors", success);
        Assert.Contains("\"traceId\":\"trace-id\"", failure);
        Assert.Contains("\"errorCode\":\"Order:Conflict\"", failure);
        Assert.DoesNotContain("errors", failure);
    }
}
