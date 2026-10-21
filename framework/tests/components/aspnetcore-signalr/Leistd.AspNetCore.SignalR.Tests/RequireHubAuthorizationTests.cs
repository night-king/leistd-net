using Leistd.AspNetCore.SignalR.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Leistd.AspNetCore.SignalR.Tests.TestDoubles;

namespace Leistd.AspNetCore.SignalR.Tests;

/// <summary><c>RequireHubAuthorization()</c>：握手的授权元数据取 <see cref="HubIdentityOptions.PolicyName"/>，与调用期复评同源。</summary>
/// <remarks>两阶段的端到端验证见 <c>EndToEnd/HubAuthorizationTests</c>；这里钉住元数据本身。</remarks>
public sealed class RequireHubAuthorizationTests
{
    [Theory]
    [InlineData("Hub")]
    [InlineData(null)]
    public void Hub_endpoints_carry_the_configured_policy_or_the_default_policy(string? policyName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalRAmbientContext(options => options.PolicyName = policyName);
        var app = builder.Build();

        app.MapHub<TestHub>("/hubs/test").RequireHubAuthorization();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToArray();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            var authorize = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(policyName, authorize.Policy);
        });
    }
}
