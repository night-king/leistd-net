#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Auth.OpenIddict;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>重新认证证明比较的是会话开始时间的亚秒精度：auth_time 只到秒，同一秒里已经存在的会话凭它分辨不出。</summary>
public sealed class ConnectInteractionProtectorTests
{
    private static readonly DateTime IssuedAt = new(2026, 10, 3, 0, 0, 0, 500, DateTimeKind.Utc);

    private readonly ConnectInteractionProtector _protector =
        new(new EphemeralDataProtectionProvider(), Options.Create(new ClaimTypeOptions()));

    [Fact]
    public void Only_a_session_started_after_the_proof_redeems_it_within_the_same_second()
    {
        var proof = _protector.CreateReauthentication("urn:request", IssuedAt);

        Assert.False(_protector.IsReauthenticated(proof, "urn:request", IssuedAt.AddMilliseconds(-1)));
        Assert.False(_protector.IsReauthenticated(proof, "urn:request", IssuedAt));
        Assert.True(_protector.IsReauthenticated(proof, "urn:request", IssuedAt.AddMilliseconds(1)));
    }

    [Fact]
    public void A_proof_is_bound_to_its_request_and_needs_a_current_session()
    {
        var proof = _protector.CreateReauthentication("urn:request", IssuedAt);

        Assert.False(_protector.IsReauthenticated(proof, "urn:other", IssuedAt.AddMilliseconds(1)));
        Assert.False(_protector.IsReauthenticated(proof, "urn:request", null));
        Assert.False(_protector.IsReauthenticated("forged", "urn:request", IssuedAt.AddMilliseconds(1)));
    }
}
#endif
