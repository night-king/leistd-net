using Microsoft.Extensions.Configuration;
using System.Text;
using Leistd.Security.Users;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Leistd.ServiceClient.Options;
using Leistd.ServiceClient.Refit;
using Leistd.TestBase.Doubles;
using Leistd.Tracing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Refit;
using Xunit;
using Leistd.Tracing.Abstractions;

namespace Leistd.ServiceClient.Tests.Refit;

/// <summary>
/// Refit 路径的数据格式矩阵与契约一致性：JSON/route/query、表单、multipart 文件上传、
/// 二进制下载、错误还原为 <see cref="RemoteServiceException"/>、标准管道（TraceId/用户头）透传。
/// </summary>
public sealed class RefitIntegrationTests(RefitIntegrationTests.HostFixture fixture)
    : IClassFixture<RefitIntegrationTests.HostFixture>
{
    private readonly WebApplication _host = fixture.Host;

    public sealed class FormatApiOptions : ServiceClientOptions;

    public sealed record ItemDto(Guid Id, string Name, string Q);

    public sealed record HeadersEchoDto(string? UserId, string? TraceId);

    public sealed record UploadEchoDto(string FileName, string? ContentType, long Size, string Note);

    public interface IFormatApi
    {
        [Get("/api/items/{id}")]
        Task<ItemDto> GetItemAsync(Guid id, [Query] string q, CancellationToken cancellationToken = default);

        [Get("/api/headers-echo")]
        Task<HeadersEchoDto> EchoHeadersAsync(CancellationToken cancellationToken = default);

        [Post("/api/form")]
        Task<Dictionary<string, string>> PostFormAsync(
            [Body(BodySerializationMethod.UrlEncoded)] Dictionary<string, object> form,
            CancellationToken cancellationToken = default);

        [Multipart]
        [Post("/api/upload")]
        Task<UploadEchoDto> UploadAsync(
            [AliasAs("file")] StreamPart file,
            [AliasAs("note")] string note,
            CancellationToken cancellationToken = default);

        [Get("/api/download")]
        Task<HttpResponseMessage> DownloadAsync(CancellationToken cancellationToken = default);

        [Get("/api/download-error")]
        Task<HttpResponseMessage> DownloadFailAsync(CancellationToken cancellationToken = default);

        [Get("/api/error")]
        Task<ItemDto> FailAsync(CancellationToken cancellationToken = default);
    }

    private static readonly byte[] DownloadPayload = Encoding.UTF8.GetBytes("binary-payload-0123456789");

    private ServiceProvider CreateCaller(ICurrentUser? currentUser = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddCorrelationIdCore();
        if (currentUser is not null)
        {
            services.AddSingleton(currentUser);
        }

        services.AddRefitServiceClient<IFormatApi, FormatApiOptions>(
                "FormatService",
                options => options.BaseAddress = "http://format-service")
            .ConfigurePrimaryHttpMessageHandler(() => _host.GetTestServer().CreateHandler());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Json_route_and_query_follow_web_conventions()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();
        var id = Guid.NewGuid();

        var item = await api.GetItemAsync(id, "中文 q&x");

        Assert.Equal(id, item.Id);
        Assert.Equal("item", item.Name);
        Assert.Equal("中文 q&x", item.Q); // query 值经编码往返无损
    }

    [Fact]
    public async Task Standard_pipeline_applies_to_refit_clients()
    {
        var userId = Guid.NewGuid();
        await using var caller = CreateCaller(new FakeCurrentUser(id: userId));
        var api = caller.GetRequiredService<IFormatApi>();
        var correlation = caller.GetRequiredService<ICorrelationIdProvider>();

        HeadersEchoDto echo;
        using (correlation.Change("trace-refit-1"))
        {
            echo = await api.EchoHeadersAsync();
        }

        Assert.Null(echo.UserId);
        Assert.Equal("trace-refit-1", echo.TraceId);
    }

    [Fact]
    public async Task Url_encoded_form_serializes_field_values()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();

        var echo = await api.PostFormAsync(new Dictionary<string, object>
        {
            ["grantType"] = "demo",
            ["count"] = 42,
            ["text"] = "a b&c 中文",
        });

        Assert.Equal("demo", echo["grantType"]);
        Assert.Equal("42", echo["count"]);
        Assert.Equal("a b&c 中文", echo["text"]);
    }

    [Fact]
    public async Task Multipart_upload_keeps_file_name_content_type_and_content()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();
        var bytes = Encoding.UTF8.GetBytes("file-content-payload");
        using var stream = new MemoryStream(bytes);

        var echo = await api.UploadAsync(
            new StreamPart(stream, "报表.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "月度报表");

        Assert.Equal("报表.xlsx", echo.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", echo.ContentType);
        Assert.Equal(bytes.Length, echo.Size);
        Assert.Equal("月度报表", echo.Note);
    }

    [Fact]
    public async Task Binary_download_streams_the_raw_response_with_error_checks()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();

        using var response = await api.DownloadAsync();
        await response.EnsureRemoteSuccessAsync();
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(DownloadPayload, bytes);
    }

    [Fact]
    public async Task Download_error_bypasses_the_exception_factory_and_needs_explicit_restore()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();

        // 返回 HttpResponseMessage 的方法拿到原始响应，Refit 不抛异常
        using var response = await api.DownloadFailAsync();
        Assert.Equal(500, (int)response.StatusCode);

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(
            () => response.EnsureRemoteSuccessAsync());
        Assert.Equal("Storage:Unavailable", exception.ErrorCode);
        Assert.Equal("rt-download", exception.RemoteTraceId);
    }

    [Fact]
    public async Task Remote_error_becomes_RemoteServiceException_instead_of_ApiException()
    {
        await using var caller = CreateCaller();
        var api = caller.GetRequiredService<IFormatApi>();

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => api.FailAsync());

        Assert.Equal(404, exception.RemoteStatusCode);
        Assert.Equal("Item:NotFound", exception.ErrorCode);
        Assert.Equal("rt-error", exception.RemoteTraceId);
        Assert.Contains("条目不存在", exception.Message);
        Assert.IsNotAssignableFrom<ApiException>(exception); // 错误契约与手写路径统一
    }

    /// <summary>本类用例共享的远端服务：端点固定、只回显请求，用例之间没有逐测可变的宿主状态。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public WebApplication Host { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            var app = builder.Build();

            app.MapGet("/api/items/{id:guid}", (Guid id, string q) =>
                Results.Json(new { id, name = "item", q }));

            app.MapGet("/api/headers-echo", (HttpContext context) => Results.Json(new
            {
                userId = context.Request.Headers["X-User-Id"].FirstOrDefault(),
                traceId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault(),
            }));

            app.MapPost("/api/form", async (HttpRequest request) =>
            {
                if (!request.HasFormContentType)
                {
                    return Results.BadRequest();
                }

                var form = await request.ReadFormAsync();
                return Results.Json(form.ToDictionary(f => f.Key, f => f.Value.ToString()));
            });

            app.MapPost("/api/upload", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var file = form.Files["file"];
                if (file is null)
                {
                    return Results.BadRequest();
                }

                return Results.Json(new
                {
                    fileName = file.FileName,
                    contentType = file.ContentType,
                    size = file.Length,
                    note = form["note"].ToString(),
                });
            });

            app.MapGet("/api/download", () => Results.Bytes(DownloadPayload, "application/octet-stream"));

            app.MapGet("/api/download-error", () => Results.Json(
                new { status = 500, code = "Storage:Unavailable", message = "存储不可用", traceId = "rt-download" },
                statusCode: 500));

            app.MapGet("/api/error", () => Results.Json(
                new { status = 404, code = "Item:NotFound", message = "条目不存在", traceId = "rt-error" },
                statusCode: 404));

            await app.StartAsync();
            Host = app;
        }

        public async Task DisposeAsync() => await Host.DisposeAsync();
    }
}
