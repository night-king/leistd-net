using System.Security.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Xunit;

namespace Leistd.ServiceClient.Tests.Core;

public sealed class CurrentUserTests
{
    [Fact]
    public void Name_reads_the_standard_name_claim()
    {
        var principal = Principal(
            new Claim("name", "Ada Lovelace"),
            new Claim(ClaimTypes.GivenName, "Ada"));

        var currentUser = new CurrentUser(new StubPrincipalAccessor(principal), Microsoft.Extensions.Options.Options.Create(new Leistd.Security.Claims.ClaimTypeOptions()));

        Assert.Equal("Ada Lovelace", currentUser.Name);
    }

    [Fact]
    public void Name_does_not_treat_given_name_as_a_full_name()
    {
        var currentUser = new CurrentUser(new StubPrincipalAccessor(
            Principal(new Claim(ClaimTypes.GivenName, "Ada"))), Microsoft.Extensions.Options.Options.Create(new Leistd.Security.Claims.ClaimTypeOptions()));

        Assert.Null(currentUser.Name);
    }

    [Fact]
    public void Username_prefers_preferred_username_claim()
    {
        var currentUser = new CurrentUser(new StubPrincipalAccessor(
            Principal(
                new Claim("preferred_username", "ada"),
                new Claim("name", "Ada Lovelace"),
                new Claim(ClaimTypes.Name, "legacy-ada"))), Microsoft.Extensions.Options.Options.Create(new Leistd.Security.Claims.ClaimTypeOptions()));

        Assert.Equal("ada", currentUser.Username);
    }

    [Fact]
    public void Username_falls_back_to_name_claim()
    {
        var currentUser = new CurrentUser(new StubPrincipalAccessor(
            Principal(
                new Claim("name", "Ada Lovelace"),
                new Claim(ClaimTypes.Name, "legacy-ada"))), Microsoft.Extensions.Options.Options.Create(new Leistd.Security.Claims.ClaimTypeOptions()));

        Assert.Equal("Ada Lovelace", currentUser.Username);
    }

    [Fact]
    public void Username_falls_back_to_dotnet_name_claim()
    {
        var currentUser = new CurrentUser(new StubPrincipalAccessor(
            Principal(new Claim(ClaimTypes.Name, "legacy-ada"))), Microsoft.Extensions.Options.Options.Create(new Leistd.Security.Claims.ClaimTypeOptions()));

        Assert.Equal("legacy-ada", currentUser.Username);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    private sealed class StubPrincipalAccessor(ClaimsPrincipal principal) : ICurrentPrincipalAccessor
    {
        public ClaimsPrincipal? Principal => principal;

        public IDisposable Change(ClaimsPrincipal replacement) =>
            throw new NotSupportedException();
    }
}
