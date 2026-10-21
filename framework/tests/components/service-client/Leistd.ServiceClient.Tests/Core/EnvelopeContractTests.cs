using System.Net;
using System.Text;
using System.Text.Json;
using Leistd.ExceptionHandling;
using Leistd.Response.Wrappers;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

// ServiceClient 内联了信封的读取形状，不引用 Leistd.Response——信封在那里只是
// "被调方长什么样"的反序列化目标，不该让每个消费者都传染上信封包。
// 代价是两份定义没有编译期关联：Result<T> 改字段或 JSON 命名时读侧不会跟着变，
// 而失效是静默的（反序列化出默认值）。本组用例把两者钉在一起。
public class EnvelopeContractTests
{
    [Fact]
    public async Task ReadResultAsync_reads_what_Result_writes()
    {
        var payload = new Payload("42", 7);
        var response = Json(Result<Payload>.Ok(payload));

        var data = await response.ReadResultAsync<Payload>();

        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task ReadResultAsync_carries_the_message_of_a_failed_Result()
    {
        var response = Json(Result<Payload>.Fail(40001, "参数不合法"));

        var error = await Assert.ThrowsAsync<RemoteServiceException>(
            () => response.ReadResultAsync<Payload>());

        Assert.Contains("40001", error.Message);
        Assert.Contains("参数不合法", error.Message);
    }

    [Fact]
    public async Task ReadResultAsync_without_payload_accepts_a_plain_Result()
    {
        var response = Json(Result.Ok());

        await response.ReadResultAsync();
    }

    [Fact]
    public async Task ReadResultAsync_without_payload_surfaces_a_failed_Result()
    {
        var response = Json(Result.Fail(50003, "上游不可用"));

        await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync());
    }

    // 信封失败形态的字段明细必须还原成 RemoteServiceException.Errors——
    // 与 Problem Details 走的是同一个 ErrorItem，调用方不该因为被调方选了信封而拿不到。
    [Fact]
    public async Task ReadResultAsync_restores_the_field_errors_of_an_envelope_failure()
    {
        var response = Json(Result.Fail(42201, "校验失败") with
        {
            Errors = [new ErrorItem("必须填写", "name", "Error:Required")]
        });

        var error = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync());

        var item = Assert.Single(error.Errors);
        Assert.Equal("name", item.Field);
        Assert.Equal("必须填写", item.Detail);
        Assert.Equal("Error:Required", item.Code);
    }

    // 非 2xx 的信封走错误还原路径：信封写入器的失败形态里数字 code 是 HTTP 状态，
    // 业务码只在 errorCode 里，还原时不得把状态当业务码。
    [Fact]
    public async Task Remote_error_restores_the_envelope_errorCode_rather_than_its_status()
    {
        var response = Json(Result.Fail(400, "参数不合法") with
        {
            ErrorCode = "Order:Invalid",
            Errors = [new ErrorItem("必须填写", "name", null)]
        }, HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync());

        Assert.Equal("Order:Invalid", error.ErrorCode);
        Assert.Equal("name", Assert.Single(error.Errors).Field);
    }

    // 成功信封的 code 契约是 0。改掉它两边必须同时改。
    [Fact]
    public void Successful_Result_uses_zero_as_its_code()
    {
        Assert.Equal(0, Result.Ok().Code);
        Assert.Equal(0, Result<Payload>.Ok(new Payload("1", 1)).Code);
    }

    private static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        // 与 ServiceClient 的默认反序列化选项同口径（Web 默认即 camelCase）。
        Content = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Encoding.UTF8,
            "application/json"),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/api/thing")
    };

    private sealed record Payload(string Id, int Count);
}
