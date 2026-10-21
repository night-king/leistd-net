using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class HealthEndpointTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Live_and_ready_endpoints_should_be_available()
    {
        using var client = factory.CreateProjectClient();
        var live = await client.GetAsync("/api/health/live");
        var ready = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Readiness_dependency_failure_must_not_fail_liveness()
    {
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "readiness-dependency-test",
                    () => HealthCheckResult.Unhealthy(),
                    tags: ["ready"])));
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);

        var live = await client.GetAsync("/api/health/live");
        var ready = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }
}
