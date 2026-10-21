using Leistd.MultiTenancy.Exceptions;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Leistd.AmbientContext;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.AspNetCore;
using Leistd.Security;
using Leistd.Security.Claims;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

// 环境上下文的租户维度：只认 claim，不跑完整解析链。
// 贡献者随 Leistd.MultiTenancy.AspNetCore 分发（claim 类型在它的 MultiTenancyOptions 上），
// 因此这里用 AddMultiTenancy 组装——它是个类库，Hub 调用与后台入口同样引用它。
public class TenantAmbientContextTests
{
    [Fact]
    public void Tenant_claim_is_established_for_the_scope_and_restored_after()
    {
        var tenantId = Guid.NewGuid();
        var (ambient, currentTenant) = Build();

        using (ambient.Begin(Authenticated((CustomClaimTypes.TenantId, tenantId.ToString()))))
        {
            Assert.Equal(tenantId, currentTenant.Id);
        }

        Assert.Null(currentTenant.Id);
    }

    /// <summary>只有后续身份已认证的主体同样建立它声明的租户，不被当成匿名跳过。</summary>
    [Fact]
    public void A_later_authenticated_identity_establishes_its_tenant()
    {
        var tenantId = Guid.NewGuid();
        var (ambient, currentTenant) = Build();
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity(),
            new ClaimsIdentity([new Claim(CustomClaimTypes.TenantId, tenantId.ToString())], "Test")
        ]);

        using (ambient.Begin(principal))
        {
            Assert.Equal(tenantId, currentTenant.Id);
        }
    }

    // 无声明即宿主，但必须显式置位：入口可能继承了外层租户上下文。
    [Fact]
    public void Absent_tenant_claim_switches_to_the_host_view()
    {
        var outer = Guid.NewGuid();
        var (ambient, currentTenant) = Build();

        using (currentTenant.Change(outer))
        using (ambient.Begin(Authenticated()))
        {
            Assert.Null(currentTenant.Id);
        }
    }

    // 有声明但不是 Guid：签发端与消费端口径不一致，属配置错误。
    // 绝不能退回宿主视角——那会把租户用户静默放进宿主分区。
    [Fact]
    public void Malformed_tenant_claim_fails_closed()
    {
        var (ambient, currentTenant) = Build();

        var error = Assert.Throws<InvalidTenantClaimException>(
            () => ambient.Begin(Authenticated((CustomClaimTypes.TenantId, "not-a-guid"))));

        Assert.Equal(CustomClaimTypes.TenantId, error.ClaimType);
        Assert.Null(currentTenant.Id);
    }

    // 未认证主体没有可信租户来源，不猜，保持进入前的状态。
    [Fact]
    public void Unauthenticated_principal_leaves_the_tenant_untouched()
    {
        var outer = Guid.NewGuid();
        var (ambient, currentTenant) = Build();

        using (currentTenant.Change(outer))
        using (ambient.Begin(new ClaimsPrincipal(new ClaimsIdentity())))
        {
            Assert.Equal(outer, currentTenant.Id);
        }
    }

    /// <summary>
    /// 捕获的是当前租户上下文本身：在 <c>Change</c> 作用域里入队、主体又不带租户声明的任务，
    /// 还原后回到同一个租户
    /// </summary>
    /// <remarks>
    /// 后台队列靠它把入队时的上下文带进任务。只按主体声明重建的话，宿主为某个租户代办、
    /// 切了租户再入队的任务会落回宿主分区，读写都去了宿主库。
    /// </remarks>
    [Fact]
    public void A_captured_tenant_is_restored_even_without_a_tenant_claim()
    {
        var tenantId = Guid.NewGuid();
        var sp = BuildProvider();
        var ambient = sp.GetRequiredService<IAmbientContext>();
        var currentTenant = sp.GetRequiredService<ICurrentTenant>();
        var principalAccessor = sp.GetRequiredService<ICurrentPrincipalAccessor>();

        AmbientContextSnapshot snapshot;
        using (principalAccessor.Change(Authenticated()))
        using (currentTenant.Change(tenantId, "acme"))
        {
            snapshot = ambient.Capture();
        }

        Assert.Null(currentTenant.Id);
        using (ambient.Restore(snapshot))
        {
            Assert.Equal(tenantId, currentTenant.Id);
            Assert.Equal("acme", currentTenant.Name);
            Assert.True(principalAccessor.Principal?.Identity?.IsAuthenticated);
        }

        Assert.Null(currentTenant.Id);
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    // 宿主改了租户 claim 名：非 HTTP 入口按新名建立租户，旧名不再被当作租户
    [Fact]
    public void A_configured_tenant_claim_name_is_honoured()
    {
        var tenantId = Guid.NewGuid();
        var sp = BuildProvider(tenantClaimType: "tid");
        var ambient = sp.GetRequiredService<IAmbientContext>();
        var currentTenant = sp.GetRequiredService<ICurrentTenant>();

        using (ambient.Begin(Authenticated(("tid", tenantId.ToString()), (CustomClaimTypes.TenantId, "stale"))))
        {
            Assert.Equal(tenantId, currentTenant.Id);
        }
    }

    private static (IAmbientContext Ambient, ICurrentTenant CurrentTenant) Build()
    {
        var sp = BuildProvider();
        return (sp.GetRequiredService<IAmbientContext>(), sp.GetRequiredService<ICurrentTenant>());
    }

    private static ServiceProvider BuildProvider(string? tenantClaimType = null)
    {
        return new ServiceCollection()
            .AddLogging()
            .Configure<ClaimTypeOptions>(options => options.TenantId = tenantClaimType ?? options.TenantId)
            // 真实宿主总有 IConfiguration：AddMultiTenancy 绑定 Leistd:MultiTenancy
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddAmbientContext()
            // 声明即定案，不查注册表——正是本贡献者服务的形态。
            .AddMultiTenancy(o => o.ValidateResolvedTenant = false)
            .BuildServiceProvider();
    }
}
