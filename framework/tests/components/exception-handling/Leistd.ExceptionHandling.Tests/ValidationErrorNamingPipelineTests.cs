using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ExceptionHandling.AspNetCore.Handlers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Xunit;
using Leistd.ExceptionHandling.AspNetCore.Constants;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>两条验证错误响应路径均遵循宿主的属性命名策略。</summary>
/// <remarks>
/// 自动 400 使用 MVC 的 AddJsonOptions，显式验证异常经 IProblemDetailsService 使用 ConfigureHttpJsonOptions，须分别配置。
/// 全大写策略验证 <see cref="ErrorItem"/> 的 FIELD/DETAIL；顶层 ProblemDetails 字段与扩展键 errors 不随属性命名策略变化。
/// </remarks>
public class ValidationErrorNamingPipelineTests(ValidationErrorNamingPipelineTests.HostFixture fixture)
    : IClassFixture<ValidationErrorNamingPipelineTests.HostFixture>
{
    // 会改变所有属性名的自定义命名策略：全大写。若任一路径固定了属性名/未跟随宿主策略，断言即失败。
    private sealed class UpperCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToUpperInvariant();
    }

    private static void ApplyUpper(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = new UpperCaseNamingPolicy();
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }

    // 使用 HostBuilder + UseTestServer，避开 WebHostBuilder 与 TestServer(IWebHostBuilder) 的 .NET 10 弃用 API。
    private static Task<IHost> StartHostAsync(IStringLocalizer? localizer = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddGlobalExceptionHandler(_ => { });
                        if (localizer is not null)
                        {
                            services.AddSingleton(localizer);
                        }

                        // 同一命名策略应用到两套 JSON 配置——正是模板组合根的做法。
                        services.ConfigureHttpJsonOptions(o => ApplyUpper(o.SerializerOptions));
                        services.AddControllers()
                            .AddApplicationPart(typeof(ProbeController).Assembly)
                            .AddJsonOptions(o => ApplyUpper(o.JsonSerializerOptions))
                            .ConfigureApiValidation();
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            // 未安装关联 ID 中间件时，自动校验响应仍以 ASP.NET Core 的请求 ID 为准。
                            context.Response.Headers["X-Test-Request-Id"] = context.TraceIdentifier;
                            if (context.Request.Headers.ContainsKey("X-Test-Activity"))
                            {
                                using var activity = new Activity("validation-request")
                                    .SetIdFormat(ActivityIdFormat.W3C)
                                    .Start();
                                context.Response.Headers["X-Test-Activity-Trace-Id"] = activity.TraceId.ToHexString();
                                await next();
                                return;
                            }

                            await next();
                        });
                        app.UseExceptionHandler();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
            })
            .StartAsync();
    }

    [Fact]
    public async Task Automatic_and_explicit_validation_use_same_host_naming_policy_and_validation_type()
    {
        var client = fixture.Client;

        // 自动 400：缺 Name → 校验失败。
        var r400 = await client.PostAsJsonAsync("/probe/auto", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r400.StatusCode);
        var body400 = await r400.Content.ReadAsStringAsync();

        // 应用边界显式验证失败。
        var explicitValidation = await client.PostAsync("/probe/manual", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, explicitValidation.StatusCode);
        var explicitBody = await explicitValidation.Content.ReadAsStringAsync();

        // 关键断言在 errors 数组内每个 ErrorItem 的**属性名**上：两条响应都必须把 Field/Detail 序列化为大写
        // FIELD/DETAIL（跟随宿主命名策略），而不出现小写 field/detail 项键。
        // （注：errors 本身是 ProblemDetails.Extensions 的字典键，按字典键原样写出——不受命名策略影响；顶层
        // type/title/status/detail/instance 由内置 ProblemDetailsJsonConverter 以固定小写写出，同样不受策略影响。
        // 命名策略只作用于扩展项的**值对象**，这正是这里要证明两条验证路径一致、
        // 且 ErrorItem 无 [JsonPropertyName] 的点。）
        foreach (var body in new[] { body400, explicitBody })
        {
            Assert.Contains("\"FIELD\":", body);
            Assert.Contains("\"DETAIL\":", body);
            // ErrorItem 未固定 [JsonPropertyName]：不应出现小写项键（field 仅来自 ErrorItem，顶层无此成员）。
            Assert.DoesNotContain("\"field\":", body);
        }

        // 校验 wire contract：两条验证路径必须携带同一个稳定 problem type。
        Assert.Equal(ProblemTypes.ValidationError, ReadType(body400));
        Assert.Equal(ProblemTypes.ValidationError, ReadType(explicitBody));
        using var automatic = JsonDocument.Parse(body400);
        // 输入校验是协议层失败：字段错误落在 errors 里，不合成业务码
        Assert.False(automatic.RootElement.TryGetProperty("code", out _));
    }

    // 字段名要与请求体的 JSON 契约同名，前端才能把错误落回对应的输入框。
    // 自动 400 默认写出 C# 属性名（Name），而显式验证的约定与 JSON 契约都是 camelCase（name）——
    // 同一个字段在两条路径上叫法不同，调用方只能靠大小写不敏感去猜。
    // 用全大写策略断言：跟随的是宿主策略，而不是写死了某一种命名。
    [Fact]
    public async Task Auto400_field_names_follow_the_host_json_naming_policy()
    {
        var client = fixture.Client;

        Assert.Equal(["DISPLAYNAME"], await ReadFieldsAsync(client, "/probe/auto-properties"));
    }

    // 请求体漏掉必填键时，错误要落在字段上而不是 "$"：必填由 [Required] 表达（引用 = null!、值类型可空），
    // 不用 C# required——后者让缺键在反序列化阶段就失败，只剩解析错误，前端无从定位到输入框。
    [Fact]
    public async Task Auto400_for_missing_keys_names_each_required_field()
    {
        var client = fixture.Client;

        var fields = await ReadFieldsAsync(client, "/probe/auto-required");

        Assert.Equal(["ENABLED", "NAME"], fields.Order());
    }

    // 读不成 JSON 的请求体只报"哪个字段读不成"，不回显解析器的异常消息（行号、字节位置）
    [Fact]
    public async Task Auto400_for_malformed_json_does_not_echo_parser_details()
    {
        var client = fixture.Client;

        var response = await client.PostAsync(
            "/probe/auto",
            new StringContent("""{"displayName":""", System.Text.Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        // 每条字段错误都有可展示的文案，读不成 JSON 的那条回落到通用句而不是空串
        Assert.All(document.RootElement.GetProperty("errors").EnumerateArray(), error =>
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("DETAIL").GetString())));
        Assert.DoesNotContain("BytePositionInLine", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", raw, StringComparison.Ordinal);
    }

    // 两条校验路径的标题同一取法：显式验证按 Title:{状态码} 本地化，自动 400 默认写死英文。
    [Fact]
    public async Task Auto400_title_is_localized_the_same_way_as_business_errors()
    {
        var localizer = new StubLocalizer(new Dictionary<string, string> { ["Title:400"] = "请求参数有误" });
        using var host = await StartHostAsync(localizer);
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/probe/auto", new { });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("请求参数有误", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Auto400_title_keeps_the_english_sentence_without_localization()
    {
        var client = fixture.Client;

        var response = await client.PostAsJsonAsync("/probe/auto", new { });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("One or more validation errors occurred.", document.RootElement.GetProperty("title").GetString());
    }

    private static async Task<List<string?>> ReadFieldsAsync(HttpClient client, string path)
    {
        var response = await client.PostAsJsonAsync(path, new { });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(item => item.GetProperty("FIELD").GetString())
            .Distinct()
            .ToList();
    }

    private sealed class StubLocalizer(IReadOnlyDictionary<string, string> map) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            map.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            map.Select(kv => new LocalizedString(kv.Key, kv.Value, resourceNotFound: false));
    }

    /// <summary>自动 400 的 <c>traceId</c> 由官方写出：有 Activity 时是它的 W3C 标识，第二段即 TraceId。</summary>
    [Fact]
    public async Task Auto400_uses_the_official_activity_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/auto")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-Test-Activity", "true");

        var response = await fixture.Client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traceId = document.RootElement.GetProperty("traceId").GetString();

        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(response.Headers.GetValues("X-Test-Activity-Trace-Id").Single(), traceId!.Split('-')[1]);
    }

    private static string? ReadType(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
    }

    /// <summary>默认配置的共享宿主；需要替换本地化器的用例另建宿主。</summary>
    public sealed class HostFixture : IAsyncLifetime
    {
        public IHost Host { get; private set; } = default!;

        public HttpClient Client { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            Host = await StartHostAsync();
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

// 注意：MVC 控制器发现要求类型为**顶层 public**（嵌套 public 类型的 Type.IsPublic 为 false，会被
// ControllerFeatureProvider 忽略），故 ProbeController 不能内嵌在测试类中。
[ApiController]
[Route("[controller]")]
public sealed class ProbeController : ControllerBase
{
    // 自动 400：缺 Name → [ApiController] 模型校验失败 → ConfigureApiValidation 的 factory 产出 errors。
    [HttpPost("auto")]
    public IActionResult Auto([FromBody] ProbeInput input) => Ok();

    // 属性式 DTO（模板的输入 DTO 都是这种写法）。位置记录的键来自构造参数，不受命名策略影响，见 ConfigureApiValidation 的说明。
    [HttpPost("auto-properties")]
    public IActionResult AutoProperties([FromBody] ProbePropertyInput input) => Ok();

    // 缺键：必填写成 [Required]，而不是 C# required。
    [HttpPost("auto-required")]
    public IActionResult AutoRequired([FromBody] ProbeRequiredInput input) => Ok();

    // 显式验证：标准 ValidationException 由全局处理器产出 errors。
    [HttpPost("manual")]
    public IActionResult Manual() => throw new ValidationException(
        new ValidationResult("invalid phone", ["phone"]), null, null);
}

public sealed record ProbeInput([Required] string Name);

public sealed record ProbePropertyInput
{
    [Required]
    public string? DisplayName { get; init; }
}

public sealed record ProbeRequiredInput
{
    [Required]
    public string Name { get; init; } = null!;

    [Required]
    public bool? Enabled { get; init; }
}
