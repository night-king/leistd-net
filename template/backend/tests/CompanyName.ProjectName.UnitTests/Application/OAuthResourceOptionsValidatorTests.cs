#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Auth.OAuth;
using CompanyName.ProjectName.Application.Auth.Options;

namespace CompanyName.ProjectName.UnitTests.Application;

public sealed class OAuthResourceOptionsValidatorTests
{
    [Fact]
    public void The_default_catalogue_has_no_conflicts_and_maps_the_api_scope_to_one_resource()
    {
        var options = new OAuthResourceOptions();
        Assert.True(new OAuthResourceOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal([options.Resource], OAuthScopes.ResourcesOf(options, [options.Resource]));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("own")]
    [InlineData("duplicate")]
    [InlineData("scope")]
    [InlineData("owner")]
    public void Invalid_catalogue_entries_fail_with_configuration_keys(string kind)
    {
        var options = new OAuthResourceOptions { ApiResources = [new OAuthApiResource { Name = "downstream" }] };
        switch (kind)
        {
            case "blank": options.Resource = " "; break;
            case "own": options.ApiResources[0].Name = options.Resource; break;
            case "duplicate": options.ApiResources = [new OAuthApiResource { Name = "downstream" }, new OAuthApiResource { Name = "downstream" }]; break;
            case "scope": options.ApiResources[0].Scope = "openid"; break;
            case "owner": options.ApiResources[0].OwnerClientId = " "; break;
        }
        var result = new OAuthResourceOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.All(result.Failures, failure => Assert.StartsWith("OAuth:", failure));
    }
}
#endif
