using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

/// <summary>
/// 远端是普通 ASP.NET Core 宿主、用官方 <c>AddValidation()</c> 校验 Minimal API 参数时，
/// 服务客户端按实际产出的 <c>errors</c> 还原字段错误——键名以官方序列化结果为准，不自行猜测。
/// </summary>
public class ValidationProblemInteropTests
{
    public sealed class CreateOrderInput
    {
        [Required]
        public string? Name { get; set; }

        [Range(1, 100)]
        public int Quantity { get; set; }
    }

    [Fact]
    public async Task Official_minimal_api_validation_errors_are_restored()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                    services.AddValidation();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapPost("/orders", (CreateOrderInput input) => Results.Ok()));
                }))
            .StartAsync();

        using var response = await host.GetTestClient()
            .PostAsJsonAsync("/orders", new { quantity = 0 });

        var exception = await Assert.ThrowsAsync<RemoteServiceException>(() => response.ReadResultAsync<object>());

        Assert.Equal(400, exception.RemoteStatusCode);
        Assert.Contains(exception.Errors, error => error.Field == "Name" && error.Detail.Length > 0);
        Assert.Contains(exception.Errors, error => error.Field == "Quantity" && error.Detail.Length > 0);
    }
}
