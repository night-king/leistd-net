using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

/// <summary>
/// 真实 MVC 管道：<see cref="IValidatableObject.Validate"/> 产出的错误经 <see cref="DependencyInjection.ConfigureApiValidation"/>
/// 后与特性错误同一口径——字段名是成员的校验模型名（跟随 JSON 命名策略、保留嵌套前缀），文案经宿主的
/// DataAnnotations 本地化器翻译。
/// </summary>
public class ValidatableObjectValidationPipelineTests
{
    private const string LocalizedRoleMessage = "每个角色名不能超过 8 个字符。";
    private const string LocalizedRequiredMessage = "必须填写 Name。";

    [Fact]
    public async Task Member_names_follow_the_json_naming_policy_for_body_dtos()
    {
        using var host = await StartHostAsync(camelCase: true);

        var errors = await PostAsync(host, "/validatable/body", new { name = "ok", roles = new[] { "too-long-role" } });

        Assert.Equal([("roles", RoleProbeInput.RoleTooLongMessage)], errors);
    }

    [Fact]
    public async Task Nested_member_names_keep_the_property_prefix()
    {
        using var host = await StartHostAsync(camelCase: true);

        var errors = await PostAsync(host, "/validatable/nested", new { shippingAddress = new { postalCode = "abc" } });

        Assert.Equal([("shippingAddress.postalCode", AddressProbeInput.PostalCodeMessage)], errors);
    }

    // 不给成员名的是模型级错误：落在模型自身的键上（嵌套对象即其属性键），与内置行为相同
    [Fact]
    public async Task Errors_without_member_names_use_the_model_key()
    {
        using var host = await StartHostAsync(camelCase: true);

        var errors = await PostAsync(host, "/validatable/nested", new { shippingAddress = new { postalCode = "", city = "" } });

        Assert.Equal([("shippingAddress", AddressProbeInput.EmptyAddressMessage)], errors);
    }

    // 成员路径按属性逐段换算、下标保留；对不上任何属性的成员名无从换算，原样使用
    [Fact]
    public async Task Member_paths_are_resolved_and_unknown_member_names_are_kept()
    {
        using var host = await StartHostAsync(camelCase: true);

        var errors = await PostAsync(host, "/validatable/body", new { name = "conflict", roles = Array.Empty<string>() });

        Assert.Equal(
            [("Alias", RoleProbeInput.ConflictMessage), ("roles[0]", RoleProbeInput.ConflictMessage)],
            errors.OrderBy(error => error.Field, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Member_names_follow_the_json_naming_policy_for_query_dtos()
    {
        using var host = await StartHostAsync(camelCase: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/validatable/query?name=ok&roles=too-long-role");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([("roles", RoleProbeInput.RoleTooLongMessage)], await ReadErrorsAsync(response));
    }

    // 查询参数在校验前已按属性名建好模型状态条目，特性错误同样要换回校验模型名
    [Fact]
    public async Task Attribute_errors_on_query_dtos_follow_the_json_naming_policy()
    {
        using var host = await StartHostAsync(camelCase: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/validatable/query?name=ok&roles=a&roles=b&roles=c&roles=d");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([("roles", RoleProbeInput.TooManyRolesMessage)], await ReadErrorsAsync(response));
    }

    // 带参数名前缀绑定时前缀照旧，其后的成员换成校验模型名
    [Fact]
    public async Task Prefixed_query_keys_keep_the_parameter_prefix()
    {
        using var host = await StartHostAsync(camelCase: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/validatable/query?input.name=ok&input.roles=too-long-role");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([("input.roles", RoleProbeInput.RoleTooLongMessage)], await ReadErrorsAsync(response));
    }

    // 宿主没设命名策略时属性名即 JSON 名：字段名就是 C# 属性名
    [Fact]
    public async Task Member_names_are_property_names_without_a_naming_policy()
    {
        using var host = await StartHostAsync(camelCase: false);

        var errors = await PostAsync(host, "/validatable/nested", new { ShippingAddress = new { PostalCode = "abc" } });

        Assert.Equal([("ShippingAddress.PostalCode", AddressProbeInput.PostalCodeMessage)], errors);

        using var client = host.GetTestClient();
        var response = await client.GetAsync("/validatable/query?name=ok&roles=too-long-role");
        Assert.Equal([("Roles", RoleProbeInput.RoleTooLongMessage)], await ReadErrorsAsync(response));
    }

    // 与特性文案同一本地化器：宿主的 DataAnnotationLocalizerProvider 决定资源，文案作资源键
    [Fact]
    public async Task Messages_are_localized_with_the_data_annotations_localizer()
    {
        var factory = new StubLocalizerFactory(new Dictionary<string, string>
        {
            [RoleProbeInput.RoleTooLongMessage] = LocalizedRoleMessage,
            ["{0} is required."] = LocalizedRequiredMessage
        });
        using var host = await StartHostAsync(camelCase: true, factory);

        // 属性有错时 MVC 不再执行类型级校验，两类文案分两次请求取
        var attributeErrors = await PostAsync(host, "/validatable/body", new { roles = Array.Empty<string>() });
        var errors = await PostAsync(host, "/validatable/body", new { name = "ok", roles = new[] { "too-long-role" } });

        Assert.Equal([("name", LocalizedRequiredMessage)], attributeErrors);
        Assert.Equal([("roles", LocalizedRoleMessage)], errors);
        // 资源由宿主的提供委托选定，而不是按 DTO 类型另取
        Assert.Equal([typeof(SharedValidationResource)], factory.RequestedTypes.Distinct());
    }

    // 资源里没有的文案原样返回
    [Fact]
    public async Task Messages_without_a_resource_keep_the_original_text()
    {
        using var host = await StartHostAsync(camelCase: true, new StubLocalizerFactory(new Dictionary<string, string>()));

        var errors = await PostAsync(host, "/validatable/body", new { name = "ok", roles = new[] { "too-long-role" } });

        Assert.Equal([("roles", RoleProbeInput.RoleTooLongMessage)], errors);
    }

    // 宿主没启用 DataAnnotations 本地化：容器里有本地化工厂也不翻译，与特性文案一致
    [Fact]
    public async Task Messages_are_not_localized_without_data_annotations_localization()
    {
        var factory = new StubLocalizerFactory(new Dictionary<string, string>
        {
            [RoleProbeInput.RoleTooLongMessage] = LocalizedRoleMessage
        });
        using var host = await StartHostAsync(camelCase: true, factory, enableDataAnnotationsLocalization: false);

        var errors = await PostAsync(host, "/validatable/body", new { name = "ok", roles = new[] { "too-long-role" } });

        Assert.Equal([("roles", RoleProbeInput.RoleTooLongMessage)], errors);
        Assert.Empty(factory.RequestedTypes);
    }

    private static async Task<List<(string? Field, string? Detail)>> PostAsync(IHost host, string path, object body)
    {
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await ReadErrorsAsync(response);
    }

    private static async Task<List<(string? Field, string? Detail)>> ReadErrorsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // 无命名策略时 ErrorItem 的属性名是 Field/Detail，按大小写不敏感读取只为兼容两种宿主；被断言的是值
        return document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(item => (Read(item, "field"), Read(item, "detail")))
            .ToList();

        static string? Read(JsonElement item, string name) =>
            item.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value.GetString();
    }

    private static Task<IHost> StartHostAsync(
        bool camelCase,
        StubLocalizerFactory? localizerFactory = null,
        bool enableDataAnnotationsLocalization = true)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddGlobalExceptionHandler();
                    if (localizerFactory is not null)
                    {
                        services.AddSingleton<IStringLocalizerFactory>(localizerFactory);
                    }

                    var mvc = services.AddControllers()
                        .AddApplicationPart(typeof(ValidatableProbeController).Assembly)
                        .AddJsonOptions(options =>
                        {
                            if (!camelCase)
                                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                        });
                    if (localizerFactory is not null && enableDataAnnotationsLocalization)
                    {
                        mvc.AddDataAnnotationsLocalization(options =>
                            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedValidationResource)));
                    }

                    mvc.ConfigureApiValidation();
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();
    }

    private sealed class SharedValidationResource;

    private sealed class StubLocalizerFactory(IReadOnlyDictionary<string, string> map) : IStringLocalizerFactory
    {
        public List<Type> RequestedTypes { get; } = [];

        public IStringLocalizer Create(Type resourceSource)
        {
            RequestedTypes.Add(resourceSource);
            return new StubLocalizer(map);
        }

        public IStringLocalizer Create(string baseName, string location) => new StubLocalizer(map);
    }

    private sealed class StubLocalizer(IReadOnlyDictionary<string, string> map) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            map.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            map.TryGetValue(name, out var value)
                ? new LocalizedString(name, string.Format(value, arguments), resourceNotFound: false)
                : new LocalizedString(name, string.Format(name, arguments), resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            map.Select(kv => new LocalizedString(kv.Key, kv.Value, resourceNotFound: false));
    }
}

[ApiController]
[Route("validatable")]
public sealed class ValidatableProbeController : ControllerBase
{
    [HttpPost("body")]
    public IActionResult Body([FromBody] RoleProbeInput input) => Ok();

    [HttpGet("query")]
    public IActionResult Query([FromQuery] RoleProbeInput input) => Ok();

    [HttpPost("nested")]
    public IActionResult Nested([FromBody] OrderProbeInput input) => Ok();
}

public sealed record RoleProbeInput : IValidatableObject
{
    public const string RoleTooLongMessage = "Each role name cannot exceed 8 characters.";
    public const string ConflictMessage = "The first role conflicts with the name.";
    public const string TooManyRolesMessage = "Roles cannot contain more than 3 items.";

    [Required(ErrorMessage = "{0} is required.")]
    public string? Name { get; init; }

    [MaxLength(3, ErrorMessage = TooManyRolesMessage)]
    public List<string>? Roles { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Roles?.Exists(role => role.Length > 8) == true)
            yield return new ValidationResult(RoleTooLongMessage, [nameof(Roles)]);
        if (Name == "conflict")
            yield return new ValidationResult(ConflictMessage, ["Roles[0]", "Alias"]);
    }
}

public sealed record OrderProbeInput
{
    public AddressProbeInput? ShippingAddress { get; init; }
}

public sealed record AddressProbeInput : IValidatableObject
{
    public const string PostalCodeMessage = "The postal code must contain digits only.";
    public const string EmptyAddressMessage = "Enter a postal code or a city.";

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(PostalCode) && string.IsNullOrEmpty(City))
            yield return new ValidationResult(EmptyAddressMessage);
        else if (!string.IsNullOrEmpty(PostalCode) && !PostalCode.All(char.IsAsciiDigit))
            yield return new ValidationResult(PostalCodeMessage, [nameof(PostalCode)]);
    }
}
