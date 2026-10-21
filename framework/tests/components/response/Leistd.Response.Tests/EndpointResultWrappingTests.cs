using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.Response.AspNetCore.Attributes;
using Leistd.Response.AspNetCore.Extensions;
using Leistd.Response.Wrappers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.Response.Tests;

/// <summary>Minimal API 端点的统一响应包装：与 MVC 侧同一口径，只包 2xx 且带值的响应。</summary>
/// <remarks>
/// 组件经 <c>Map*</c> 提供的端点不走 MVC 过滤器；宿主若用信封形状，靠的就是这一层。
/// 包错（把错误或文件流包进信封）与漏包（信封形状在组件端点上缺席）都只在响应体里显形。
/// </remarks>
public sealed class EndpointResultWrappingTests(EndpointResultWrappingTests.HostFixture fixture)
    : IClassFixture<EndpointResultWrappingTests.HostFixture>
{
    private readonly IHost _host = fixture.Host;
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task A_plain_value_is_wrapped()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/plain");

        Assert.Equal("order", body.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Ok_with_a_value_is_wrapped()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/ok");

        Assert.Equal("order", body.GetProperty("data").GetProperty("name").GetString());
    }

    /// <summary><c>Created</c> 原样放行：重建成 JSON 会丢掉 <c>Location</c>，而状态码看上去还是 201。</summary>
    [Fact]
    public async Task Created_passes_through_with_its_location_header()
    {
        var response = await _client.PostAsync("/api/created", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/plain", response.Headers.Location?.ToString());
        Assert.Equal("order", body.GetProperty("name").GetString());
    }

    /// <summary>要让创建端点也走信封，由端点自己把信封放进结果——两种语义都在。</summary>
    [Fact]
    public async Task Created_can_carry_the_envelope_itself()
    {
        var response = await _client.PostAsync("/api/created-envelope", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/plain", response.Headers.Location?.ToString());
        Assert.Equal("order", body.GetProperty("data").GetProperty("name").GetString());
    }

    /// <summary>文件结果带着内容类型与文件名，包进信封等于把下载变成 JSON。</summary>
    [Fact]
    public async Task A_file_result_passes_through()
    {
        var response = await _client.GetAsync("/api/file");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("x", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("DELETE", "/api/empty", HttpStatusCode.NoContent)]
    [InlineData("GET", "/api/missing", HttpStatusCode.NotFound)]
    public async Task Valueless_and_unsuccessful_results_pass_through(string method, string path, HttpStatusCode expected)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("\"data\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_result_is_not_wrapped_twice()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/already");

        Assert.Equal("x", body.GetProperty("data").GetString());
    }

    /// <summary>200 的响应类型元数据同步改写成 <c>Result&lt;T&gt;</c>。</summary>
    /// <remarks>
    /// Minimal API 从处理器返回类型推断响应形状，包装后实际发出的是信封；元数据不跟着改，
    /// 生成的 OpenAPI 描述的就是另一种形状，而调用方是照着文档写代码的。
    /// </remarks>
    [Fact]
    public void The_success_response_metadata_is_rewritten_to_the_envelope()
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var plain = Single(endpoints, "/api/plain");
        var produces = plain.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Single(metadata => metadata.StatusCode == StatusCodes.Status200OK);
        Assert.True(produces.Type?.IsGenericType);
        Assert.Equal(typeof(Result<>), produces.Type!.GetGenericTypeDefinition());
    }

    /// <summary>原样放行的结果，元数据也保持原样——否则文档会说 201 返回信封，实际不是。</summary>
    [Fact]
    public void Pass_through_results_keep_their_metadata()
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var created = Single(endpoints, "/api/created");
        Assert.All(
            created.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(metadata => metadata.Type is { } type && type != typeof(void)),
            metadata => Assert.NotEqual(typeof(Result<>), metadata.Type!.IsGenericType ? metadata.Type.GetGenericTypeDefinition() : null));
    }

    /// <summary>标了 NoWrap 的端点既不包装响应，也不改元数据。</summary>
    [Fact]
    public void NoWrap_keeps_the_declared_metadata()
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var raw = Single(endpoints, "/api/raw");
        Assert.All(
            raw.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(metadata => metadata.Type is { IsGenericType: true }),
            metadata => Assert.NotEqual(typeof(Result<>), metadata.Type!.GetGenericTypeDefinition()));
    }

    /// <summary>运行时不包装的结果，元数据也不能被改成信封。</summary>
    /// <remarks>
    /// 改写的判据必须与过滤器的包装判据同源（看处理器返回类型），只按"状态码是 200"改，
    /// 就会让 <c>Json(...)</c>、文件这类原样放行的端点在文档里被声明成 <c>Result&lt;T&gt;</c>，
    /// 而客户端代码生成器是照着文档生成模型的。
    /// </remarks>
    [Theory]
    [InlineData("/api/json")]
    [InlineData("/api/declared-file")]
    public void Endpoints_whose_results_pass_through_keep_their_declared_metadata(string pattern)
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var endpoint = Single(endpoints, pattern);
        Assert.All(
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(metadata => metadata.Type is { IsGenericType: true }),
            metadata => Assert.NotEqual(typeof(Result<>), metadata.Type!.GetGenericTypeDefinition()));
    }

    /// <summary>多分支返回里只有 Ok&lt;T&gt; 那一支被包装，200 的元数据也只改这一支。</summary>
    [Fact]
    public async Task Only_the_ok_branch_of_a_multi_result_endpoint_is_wrapped()
    {
        var found = await _client.GetFromJsonAsync<JsonElement>("/api/either/true");
        Assert.Equal("order", found.GetProperty("data").GetProperty("name").GetString());

        using var missing = await _client.GetAsync("/api/either/false");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var endpoint = Single(_host.Services.GetRequiredService<EndpointDataSource>().Endpoints, "/api/either/{found:bool}");
        var ok = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Single(metadata => metadata.StatusCode == StatusCodes.Status200OK);
        Assert.Equal(typeof(Result<>), ok.Type!.GetGenericTypeDefinition());
        Assert.All(
            endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(metadata => metadata.StatusCode != StatusCodes.Status200OK && metadata.Type is { IsGenericType: true }),
            metadata => Assert.NotEqual(typeof(Result<>), metadata.Type!.GetGenericTypeDefinition()));
    }

    /// <summary>声明成 <c>object</c> 的处理器：运行时按裸值包装，元数据也必须改写。</summary>
    /// <remarks>把 object 排除在外，就会出现"响应里有信封、文档里没有"的不一致。</remarks>
    [Fact]
    public async Task An_object_returning_handler_is_wrapped_and_its_metadata_rewritten()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/object");
        Assert.Equal("order", body.GetProperty("data").GetProperty("name").GetString());

        // 推断出的 object 与显式声明的 Payload 两条 200 都要改：运行时两者描述的都是同一个信封响应
        var produces = Single(_host.Services.GetRequiredService<EndpointDataSource>().Endpoints, "/api/object")
            .Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .ToList();
        Assert.NotEmpty(produces);
        Assert.All(produces, metadata => Assert.Equal(typeof(Result<>), metadata.Type!.GetGenericTypeDefinition()));
    }

    /// <summary>两个 Ok&lt;T&gt; 分支：两条 200 元数据都要改，只认第一条会漏掉另一支。</summary>
    [Fact]
    public async Task Every_ok_branch_gets_its_metadata_rewritten()
    {
        var first = await _client.GetFromJsonAsync<JsonElement>("/api/two-ok/true");
        Assert.Equal("order", first.GetProperty("data").GetProperty("name").GetString());
        var second = await _client.GetFromJsonAsync<JsonElement>("/api/two-ok/false");
        Assert.Equal("plain", second.GetProperty("data").GetString());

        var produces = Single(_host.Services.GetRequiredService<EndpointDataSource>().Endpoints, "/api/two-ok/{first:bool}")
            .Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Where(metadata => metadata.StatusCode == StatusCodes.Status200OK)
            .ToList();

        Assert.Equal(2, produces.Count);
        Assert.All(produces, metadata => Assert.Equal(typeof(Result<>), metadata.Type!.GetGenericTypeDefinition()));
    }

    /// <summary>与上一条配对：运行时确实没有被包装。</summary>
    [Fact]
    public async Task A_self_built_json_result_is_not_wrapped()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/json");

        Assert.Equal("order", body.GetProperty("name").GetString());
    }

    private sealed record Payload(string Name);

    private static RouteEndpoint Single(IReadOnlyList<Endpoint> endpoints, string pattern)
        => endpoints.OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText == pattern);

    [Fact]
    public async Task NoWrap_metadata_opts_an_endpoint_out()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/raw");

        Assert.Equal("order", body.GetProperty("name").GetString());
    }

    /// <summary>本类用例共享的宿主：配置固定、用例之间没有逐测可变的宿主状态。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = default!;

        public HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            Host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services => services.AddRouting())
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            var group = endpoints.MapGroup("/api").WithResultWrapper();
                            group.MapGet("/plain", () => new { Name = "order" });
                            group.MapGet("/ok", () => TypedResults.Ok(new { Name = "order" }));
                            group.MapPost("/created", () => TypedResults.Created("/api/plain", new { Name = "order" }));
                            group.MapPost("/created-envelope", ()
                                => TypedResults.Created("/api/plain", Result<object>.Ok(new { Name = "order" })));
                            group.MapGet("/file", () => TypedResults.File("x"u8.ToArray(), "text/plain", "x.txt"));
                            // 宿主自建的 JSON 结果：运行时原样放行（不是 Ok<T>），元数据也不该被改
                            group.MapGet("/json", () => TypedResults.Json(new Payload("order"))).Produces<Payload>();
                            // 多分支返回：只有 Ok<T> 那一支会被包装，NotFound 一支照旧
                            group.MapGet("/either/{found:bool}", Results<Ok<Payload>, NotFound> (bool found)
                                => found ? TypedResults.Ok(new Payload("order")) : TypedResults.NotFound());
                            // 声明成 object 的处理器返回裸值：运行时会被包装，元数据也要跟着改
                            group.MapGet("/object", object () => new Payload("order")).Produces<Payload>();
                            // 两个 200 分支：两条元数据都要改
                            group.MapGet("/two-ok/{first:bool}", Results<Ok<Payload>, Ok<string>> (bool first)
                                => first ? TypedResults.Ok(new Payload("order")) : TypedResults.Ok("plain"));
                            // 文件端点显式声明 200：同理
                            group.MapGet("/declared-file", () => TypedResults.File("x"u8.ToArray(), "text/plain"))
                                .Produces<byte[]>(contentType: "text/plain");
                            group.MapDelete("/empty", () => TypedResults.NoContent());
                            group.MapGet("/missing", () => TypedResults.NotFound(new { Name = "order" }));
                            group.MapGet("/already", () => Result<string>.Ok("x"));
                            group.MapGet("/raw", () => new { Name = "order" }).WithMetadata(new NoWrapAttribute());
                        });
                    }))
                .StartAsync();
            Client = Host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
