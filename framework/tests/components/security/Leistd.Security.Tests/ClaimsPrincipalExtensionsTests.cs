using System.Security.Claims;
using Leistd.Security.Claims;
using Xunit;

namespace Leistd.Security.Tests;

/// <summary>"是否匿名"的唯一规则：任一身份已认证即已认证。</summary>
public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void A_null_principal_is_anonymous()
        => Assert.False(((ClaimsPrincipal?)null).HasAuthenticatedIdentity());

    [Fact]
    public void A_principal_without_any_authenticated_identity_is_anonymous()
        => Assert.False(new ClaimsPrincipal([new ClaimsIdentity(), new ClaimsIdentity()]).HasAuthenticatedIdentity());

    /// <summary>与官方授权管线一致；只看第一个身份会把它误判为匿名。</summary>
    [Fact]
    public void A_principal_authenticated_only_by_a_later_identity_is_authenticated()
        => Assert.True(new ClaimsPrincipal([new ClaimsIdentity(), new ClaimsIdentity([], "Test")]).HasAuthenticatedIdentity());
}
