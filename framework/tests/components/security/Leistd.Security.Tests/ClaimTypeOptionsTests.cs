using System.Security.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Users;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Security.Tests;

/// <summary>主体标识与租户 claim 的唯一读取规则：当前用户、租户解析、判权、SignalR 寻址、操作记录都按它读。</summary>
/// <remarks>
/// 各处读法一旦不一，同一个主体就会在这里是 A、在那里是 B；宿主改了 claim 名也只该改一处。
/// </remarks>
public class ClaimTypeOptionsTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void Subject_wins_over_name_identifier()
    {
        var principal = Principal(new Claim(ClaimTypes.NameIdentifier, "nameid"), new Claim("sub", "subject"));

        Assert.Equal("subject", new ClaimTypeOptions().FindUserId(principal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_subject_falls_through_to_name_identifier(string subject)
    {
        var principal = Principal(new Claim("sub", subject), new Claim(ClaimTypes.NameIdentifier, "nameid"));

        Assert.Equal("nameid", new ClaimTypeOptions().FindUserId(principal));
    }

    // 读原始值：机器主体的 client:<id> 原样返回，是否是 GUID 由调用方决定
    [Fact]
    public void The_raw_user_id_is_returned_without_parsing()
    {
        Assert.Equal("client:worker-1", new ClaimTypeOptions().FindUserId(Principal(new Claim("sub", "client:worker-1"))));
    }

    [Fact]
    public void Configured_user_id_types_replace_the_default_order()
    {
        var options = new ClaimTypeOptions { UserIds = ["user_id"] };

        Assert.Equal("custom", options.FindUserId(Principal(new Claim("sub", "subject"), new Claim("user_id", "custom"))));
        Assert.Null(options.FindUserId(null));
    }

    [Fact]
    public void No_tenant_claim_means_host_and_one_guid_means_that_tenant()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();

        Assert.Equal(TenantClaim.Host, options.ReadTenant(Principal()));
        Assert.Equal(TenantClaim.Host, options.ReadTenant(null));
        Assert.Equal(new TenantClaim(true, tenantId), options.ReadTenant(Principal(new Claim("tenant_id", tenantId.ToString()))));
    }

    // 多条（即使值相同）或非 GUID 一律非法：租户名只经匿名请求提示传递，不进身份
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("duplicate")]
    public void Several_or_non_guid_tenant_claims_are_invalid(string shape)
    {
        var tenantId = Guid.CreateVersion7().ToString();
        var principal = shape == "duplicate"
            ? Principal(new Claim("tenant_id", tenantId), new Claim("tenant_id", tenantId))
            : Principal(new Claim("tenant_id", shape));

        Assert.False(new ClaimTypeOptions().ReadTenant(principal).IsValid);
    }

    // 同一主体被多个认证方案认证：策略评估合并出的每个身份各带一条相同的租户 claim，合法；值不一致则非法
    [Fact]
    public void The_same_tenant_across_merged_identities_is_valid_but_conflicting_tenants_are_not()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();
        var bearer = new ClaimsIdentity([new Claim("tenant_id", tenantId.ToString())], "Bearer");
        var cookie = new ClaimsIdentity([new Claim("tenant_id", tenantId.ToString())], "Cookie");
        var other = new ClaimsIdentity([new Claim("tenant_id", Guid.CreateVersion7().ToString())], "Cookie");

        Assert.Equal(new TenantClaim(true, tenantId), options.ReadTenant(new ClaimsPrincipal([bearer, cookie])));
        Assert.False(options.ReadTenant(new ClaimsPrincipal([bearer, other])).IsValid);
    }

    // 未认证的空身份不参与判定
    [Fact]
    public void An_empty_identity_does_not_take_part()
    {
        var tenantId = Guid.CreateVersion7();
        var tenantUser = new ClaimsIdentity([new Claim("sub", "7"), new Claim("tenant_id", tenantId.ToString())], "Bearer");

        Assert.Equal(new TenantClaim(true, tenantId), new ClaimTypeOptions().ReadTenant(new ClaimsPrincipal([new ClaimsIdentity(), tenantUser])));
    }

    // 两份合法的用户凭据：宿主的 42 号与租户 T 的 7 号。标识与租户分别取的话会拼出"租户 T 的 42 号"——另一个人。
    // 宿主凭据在前时判为非法；租户凭据在前时主体就是"租户 T 的 7 号"，两项取自同一份凭据，没有拼接
    [Fact]
    public void Two_user_credentials_never_combine_one_users_id_with_the_others_tenant()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();
        var hostUser = new ClaimsIdentity([new Claim("sub", "42")], "Cookie");
        var tenantUser = new ClaimsIdentity([new Claim("sub", "7"), new Claim("tenant_id", tenantId.ToString())], "Bearer");

        Assert.False(options.ReadTenant(new ClaimsPrincipal([hostUser, tenantUser])).IsValid);

        var tenantFirst = new ClaimsPrincipal([tenantUser, hostUser]);
        Assert.Equal(("7", new TenantClaim(true, tenantId)), (options.FindUserId(tenantFirst), options.ReadTenant(tenantFirst)));
    }

    // 用户标识与租户取自同一个身份：第一个带标识的身份
    [Fact]
    public void The_user_id_and_the_tenant_come_from_the_same_identity()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();
        // 第二个身份的 sub 在类型顺序上优先，但主体身份是第一个：按类型跨身份取会得到 "8"
        var first = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7"), new Claim("tenant_id", tenantId.ToString())], "Cookie");
        var second = new ClaimsIdentity([new Claim("sub", "8"), new Claim("tenant_id", tenantId.ToString())], "Bearer");
        var principal = new ClaimsPrincipal([first, second]);

        Assert.Equal("7", options.FindUserId(principal));
        Assert.Equal(new TenantClaim(true, tenantId), options.ReadTenant(principal));
    }

    // 服务间调用：被代表的用户在前、调用方机器身份（无租户）在后，主体是被代表的用户
    [Fact]
    public void The_first_identified_identity_is_the_subject_when_authentication_schemes_are_combined()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();
        var supplemental = new ClaimsIdentity([new Claim("sub", "7"), new Claim("tenant_id", tenantId.ToString())], "SupplementalAuthentication");
        var machine = new ClaimsIdentity([new Claim("sub", "client:worker-1")], "Bearer");
        var principal = new ClaimsPrincipal([supplemental, machine]);

        Assert.Equal("7", options.FindUserId(principal));
        Assert.Equal(new TenantClaim(true, tenantId), options.ReadTenant(principal));
    }

    // 名字、邮箱这类描述"这个人"的 claim 也取自主体身份：主体身份没带 name 时不能取到其他认证身份上的名字
    [Fact]
    public void The_current_users_descriptive_claims_come_from_the_subject_identity()
    {
        var supplemental = new ClaimsIdentity([new Claim("sub", "7"), new Claim("preferred_username", "alice")], "SupplementalAuthentication");
        var machine = new ClaimsIdentity([new Claim("sub", "client:worker-1"), new Claim("name", "Worker"), new Claim("email", "ops@example.com")], "Bearer");
        var principal = new ClaimsPrincipal([supplemental, machine]);
        var currentUser = new CurrentUser(new FixedPrincipalAccessor(principal), Options.Create(new ClaimTypeOptions()));

        Assert.Same(supplemental, new ClaimTypeOptions().FindSubjectIdentity(principal));
        Assert.Equal(("alice", null, null), (currentUser.Username, currentUser.Name, currentUser.Email));
    }

    // 没有带标识的身份时不存在拼接，按整个主体读
    [Fact]
    public void Without_a_subject_identity_descriptive_claims_are_read_from_the_whole_principal()
    {
        var principal = Principal(new Claim("name", "Guest"));
        var currentUser = new CurrentUser(new FixedPrincipalAccessor(principal), Options.Create(new ClaimTypeOptions()));

        Assert.Null(new ClaimTypeOptions().FindSubjectIdentity(principal));
        Assert.Equal("Guest", currentUser.Name);
    }

    // 补充认证身份没有主体标识时可以提供租户，各补充身份的租户须一致
    [Fact]
    public void An_identity_without_a_subject_id_supplies_the_tenant_of_a_tenantless_subject()
    {
        var options = new ClaimTypeOptions();
        var tenantId = Guid.CreateVersion7();
        var supplemental = new ClaimsIdentity([new Claim("tenant_id", tenantId.ToString())], "SupplementalAuthentication");
        var machine = new ClaimsIdentity([new Claim("sub", "client:worker-1")], "Bearer");
        var other = new ClaimsIdentity([new Claim("tenant_id", Guid.CreateVersion7().ToString())], "Other");

        Assert.Equal("client:worker-1", options.FindUserId(new ClaimsPrincipal([supplemental, machine])));
        Assert.Equal(new TenantClaim(true, tenantId), options.ReadTenant(new ClaimsPrincipal([supplemental, machine])));
        Assert.False(options.ReadTenant(new ClaimsPrincipal([supplemental, machine, other])).IsValid);
    }

    // 主体身份自带租户时，补充身份的租户不能改写它
    [Fact]
    public void A_supplemental_identity_cannot_override_the_subjects_own_tenant()
    {
        var options = new ClaimTypeOptions();
        var subject = new ClaimsIdentity([new Claim("sub", "7"), new Claim("tenant_id", Guid.CreateVersion7().ToString())], "Bearer");
        var supplemental = new ClaimsIdentity([new Claim("tenant_id", Guid.CreateVersion7().ToString())], "SupplementalAuthentication");

        Assert.False(options.ReadTenant(new ClaimsPrincipal([subject, supplemental])).IsValid);
    }

    // 宿主改了租户 claim 名：当前用户按新名读，旧名不再被当作租户
    [Fact]
    public void A_configured_tenant_claim_name_is_honoured_by_the_current_user()
    {
        var tenantId = Guid.CreateVersion7();
        var principal = Principal(new Claim("sub", Guid.NewGuid().ToString()), new Claim("tid", tenantId.ToString()), new Claim("tenant_id", "stale"));
        var currentUser = new CurrentUser(new FixedPrincipalAccessor(principal), Options.Create(new ClaimTypeOptions { TenantId = "tid" }));

        Assert.Equal(tenantId, currentUser.TenantId);
    }

    // 当前用户与直接读主体同一规则
    [Fact]
    public void The_current_user_subject_id_follows_the_same_rule()
    {
        var principal = Principal(new Claim("sub", "  "), new Claim(ClaimTypes.NameIdentifier, "client:worker-1"));
        var currentUser = new CurrentUser(new FixedPrincipalAccessor(principal), Options.Create(new ClaimTypeOptions()));

        Assert.Equal("client:worker-1", currentUser.SubjectId);
        Assert.Null(currentUser.Id);
    }

    private sealed class FixedPrincipalAccessor(ClaimsPrincipal? principal) : ICurrentPrincipalAccessor
    {
        public ClaimsPrincipal? Principal { get; } = principal;
        public IDisposable Change(ClaimsPrincipal principal) => throw new NotSupportedException();
    }
}
