using System.Net.Http.Json;
using System.Text.Json;
using Leistd.ExceptionHandling.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.ExceptionHandling.Tests;

public sealed class ExceptionDetailsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stack_trace_follows_the_diagnostic_option(bool includeDetails)
    {
        using var host = await StartAsync(includeDetails);

        var response = await host.GetTestClient().GetAsync("/");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(includeDetails, problem.TryGetProperty("stackTrace", out _));
    }

    private static async Task<IHost> StartAsync(bool includeDetails)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services => services.AddGlobalExceptionHandler(options =>
                    options.IncludeExceptionDetails = includeDetails))
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new InvalidOperationException("diagnostic only"));
                }))
            .StartAsync();
        return host;
    }
}
