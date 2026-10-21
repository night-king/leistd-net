using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>官方 OpenAPI 文档：Development 下收录控制器与组件端点，其他环境不暴露接口清单。</summary>
public sealed class OpenApiDocumentTests
{
    [Fact]
    public async Task Derived_page_rules_are_used_by_query_binding_and_openapi()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.AddControllers()
                .AddApplicationPart(typeof(CustomPageController).Assembly));
        });
        using var client = host.CreateClient();

        using var defaults = JsonDocument.Parse(await client.GetStringAsync("/__tests/custom-page"));
        Assert.Equal(20, defaults.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__tests/custom-page?limit=1500")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/__tests/custom-page?limit=2001")).StatusCode);

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/__tests/custom-page")
            .GetProperty("get").GetProperty("parameters").EnumerateArray();
        var limit = parameters.Single(parameter => parameter.GetProperty("name").GetString()!
            .Equals(nameof(PageRequest.Limit), StringComparison.OrdinalIgnoreCase));
        var schema = limit.GetProperty("schema");
        Assert.Equal(1, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(2000, schema.GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task Development_serves_a_document_with_controllers_and_component_endpoints()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        var document = await host.CreateClient().GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"openapi\"", document);
        // 控制器经 ApiExplorer 收录
        Assert.Contains("\"/api/v1/users", document);
        // 组件以 Minimal API 映射的端点同样收录
        Assert.Contains("\"/api/v1/settings", document);
    }
#if (LocalIdentity && IncludeMultiTenancy)

    /// <summary>租户列表固定排序：文档不声明 <c>sorting</c>，客户端不会把它当成可用的排序参数。</summary>
    [Fact]
    public async Task Tenant_listing_does_not_declare_a_sorting_parameter()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        using var document = JsonDocument.Parse(await host.CreateClient().GetStringAsync("/openapi/v1.json"));
        var names = document.RootElement.GetProperty("paths").GetProperty("/api/v1/tenants")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("keyword", names);
        Assert.DoesNotContain("sorting", names, StringComparer.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public async Task Other_environments_do_not_expose_the_document()
    {
        using var factory = new ProjectWebApplicationFactory();

        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.DoesNotContain("\"openapi\"", await response.Content.ReadAsStringAsync());
    }
}

[ApiController]
[Route("__tests/custom-page")]
public sealed class CustomPageController : ControllerBase
{
    [HttpGet]
    public CustomPageInput Get([FromQuery] CustomPageInput input) => input;
}

public sealed record CustomPageInput : PageRequest
{
    [Range(1, 2000)]
    public override int Limit { get; init; } = 20;
}
