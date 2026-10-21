using System.Security.Claims;
using Leistd.Authorization.AspNetCore.Permissions;
using Leistd.Authorization.Checking;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Tests.TestDoubles;
using Leistd.MultiTenancy.Context;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Leistd.Authorization.Tests.Core;

/// <summary>为显式主体判权：评估传入的主体本身，不串用当前主体在作用域里缓存的授予，也不跨租户判定。</summary>
/// <remarks>
/// 检查器是 Scoped 的，当前主体的授予在作用域内只读一次。同一作用域里先后判两个主体时，
/// 若第二个主体也落进那份缓存，拿到的就是前一个人的权限——越权不会报错，只会静默放行。
/// </remarks>
public class ExplicitPrincipalCheckTests
{
    private const string Read = TestPermissionDefinitionProvider.OrdersRead;
    private const string Write = TestPermissionDefinitionProvider.OrdersWrite;

    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();

    [Fact]
    public async Task Current_then_other_then_current_never_mixes_the_grants()
    {
        var alice = Principal("alice");
        var bob = Principal("bob");
        var (checker, provider) = Create(current: alice);

        Assert.True(await checker.IsGrantedAsync(Read));
        Assert.False(await checker.IsGrantedAsync(Write));

        Assert.False(await checker.IsGrantedAsync(bob, Read));
        Assert.True(await checker.IsGrantedAsync(bob, Write));
        Assert.Equal(new[] { false, true }, await ResultsAsync(checker, bob));

        Assert.True(await checker.IsGrantedAsync(alice, Read));
        Assert.False(await checker.IsGrantedAsync(Write));
        Assert.Equal(new[] { true, false }, await ResultsAsync(checker, alice));

        // 当前主体只解析一次（显式传入同一引用也走缓存）；另一主体每次单独解析
        Assert.Equal(1, provider.CurrentCalls);
        Assert.Equal(3, provider.ExplicitCalls);
    }

    // 当前主体在作用域内被换掉（Change / Begin，或策略按认证方案重设 HttpContext.User）：
    // 换上来的主体此时就是"当前主体"，快照必须按它重新加载，而不是沿用前一个人的授予
    [Fact]
    public async Task Switching_the_current_principal_reloads_the_snapshot()
    {
        var alice = Principal("alice");
        var bob = Principal("bob");
        var accessor = new SwitchablePrincipalAccessor(alice);
        var (checker, _) = Create(accessor, new SwitchableTenant(null));

        Assert.True(await checker.IsGrantedAsync(Read));

        using (accessor.Change(bob))
        {
            Assert.False(await checker.IsGrantedAsync(Read));
            Assert.True(await checker.IsGrantedAsync(bob, Write));
            Assert.False(await checker.IsGrantedAsync(bob, Read));
        }

        Assert.True(await checker.IsGrantedAsync(alice, Read));
        Assert.False(await checker.IsGrantedAsync(Write));
    }

    // 授予按当前租户读取：同一作用域切换租户后，前一个租户里读到的授予不能再用
    [Fact]
    public async Task Switching_the_current_tenant_reloads_the_snapshot()
    {
        var alice = Principal("alice");
        var tenant = new SwitchableTenant(TenantA);
        var (checker, provider) = Create(new SwitchablePrincipalAccessor(alice), tenant);

        await checker.IsGrantedAsync(Read);
        using (tenant.Change(TenantB))
        {
            await checker.IsGrantedAsync(Read);
        }

        await checker.IsGrantedAsync(Read);

        Assert.Equal(3, provider.CurrentCalls);
    }

    [Theory]
    [InlineData("tenant-b")]
    [InlineData("host")]
    [InlineData("two-claims")]
    [InlineData("not-a-guid")]
    public async Task A_principal_outside_the_current_tenant_is_denied(string shape)
    {
        Claim[] tenantClaims = shape switch
        {
            "tenant-b" => [new Claim(CustomClaimTypes.TenantId, TenantB.ToString())],
            "host" => [],
            "two-claims" => [new Claim(CustomClaimTypes.TenantId, TenantA.ToString()), new Claim(CustomClaimTypes.TenantId, TenantB.ToString())],
            _ => [new Claim(CustomClaimTypes.TenantId, "tenant-a")],
        };
        var (checker, _) = Create(current: Principal("alice"), tenantId: TenantA);

        // bob 在授予表里确有 Write：被拒绝只能是因为租户不符
        Assert.False(await checker.IsGrantedAsync(Principal("bob", tenantClaims), Write));
    }

    [Fact]
    public async Task A_principal_of_the_current_tenant_is_evaluated()
    {
        var (checker, _) = Create(current: Principal("alice"), tenantId: TenantA);

        Assert.True(await checker.IsGrantedAsync(
            Principal("bob", new Claim(CustomClaimTypes.TenantId, TenantA.ToString())), Write));
    }

    // 当前主体由两份合法用户凭据合并而成（宿主的 bob 在前、租户 A 的 alice 在后），当前租户为 A：
    // 提供器按主体身份取到 bob，而租户 A 里确有一个 bob 持有 Write——拼出来的是另一个人，两个入口都必须拒绝
    [Fact]
    public async Task A_current_principal_merging_two_users_across_tenants_is_denied()
    {
        var merged = new ClaimsPrincipal([
            new ClaimsIdentity([new Claim(CustomClaimTypes.Subject, "bob")], "Cookie"),
            new ClaimsIdentity([new Claim(CustomClaimTypes.Subject, "alice"), new Claim(CustomClaimTypes.TenantId, TenantA.ToString())], "Bearer")]);
        var (checker, _) = Create(current: merged, tenantId: TenantA);

        Assert.False(await checker.IsGrantedAsync(Write));
        Assert.False(await checker.IsGrantedAsync(merged, Write));
        Assert.Equal(new[] { false, false }, await ResultsAsync(checker, merged));
    }

    // 未接多租户时没有可比的租户，但非法声明同样拒绝：与当前主体路径一致
    [Fact]
    public async Task Without_multi_tenancy_an_explicit_principal_with_an_invalid_tenant_claim_is_denied()
    {
        var accessor = new SwitchablePrincipalAccessor(Principal("alice"));
        var checker = new DefaultPermissionChecker(
            new ClaimsSubjectProvider(accessor),
            TestPermissionDefinitions.CreateManager(),
            new SubjectGrantStore(new() { ["bob"] = [Write] }),
            currentTenant: null,
            accessor);

        Assert.False(await checker.IsGrantedAsync(Principal("bob", new Claim(CustomClaimTypes.TenantId, "tenant-a")), Write));
        Assert.True(await checker.IsGrantedAsync(Principal("bob"), Write));
    }

    // 只校验声明合法，不要求等于当前租户：宿主主体显式切入租户照常判定
    [Fact]
    public async Task A_host_principal_switched_into_a_tenant_is_still_evaluated()
    {
        var (checker, _) = Create(current: Principal("bob"), tenantId: TenantA);

        Assert.True(await checker.IsGrantedAsync(Write));
    }

    // 处理器评估 context.User：经 IAuthorizationService 为别的主体判权时，结果属于那个主体
    [Fact]
    public async Task The_authorization_handler_evaluates_the_principal_being_authorized()
    {
        var (checker, _) = Create(current: Principal("alice"));
        var handler = new PermissionAuthorizationHandler(checker);
        var requirement = new PermissionRequirement(Write);
        var context = new AuthorizationHandlerContext([requirement], Principal("bob"), resource: null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    private static async Task<bool[]> ResultsAsync(IPermissionChecker checker, ClaimsPrincipal principal)
    {
        var result = await checker.IsGrantedAsync(principal, [Read, Write]);
        return [result.Results[Read], result.Results[Write]];
    }

    private static ClaimsPrincipal Principal(string userId, params Claim[] extra)
        => new(new ClaimsIdentity([new Claim(CustomClaimTypes.Subject, userId), .. extra], "Test"));

    private static (DefaultPermissionChecker Checker, ClaimsSubjectProvider Provider) Create(
        ClaimsPrincipal current,
        Guid? tenantId = null)
        => Create(new SwitchablePrincipalAccessor(current), new SwitchableTenant(tenantId));

    private static (DefaultPermissionChecker Checker, ClaimsSubjectProvider Provider) Create(
        SwitchablePrincipalAccessor accessor,
        SwitchableTenant tenant)
    {
        var provider = new ClaimsSubjectProvider(accessor);
        var store = new SubjectGrantStore(new()
        {
            ["alice"] = [Read],
            ["bob"] = [Write],
        });
        var checker = new DefaultPermissionChecker(
            provider,
            TestPermissionDefinitions.CreateManager(),
            store,
            tenant,
            accessor);
        return (checker, provider);
    }

    private sealed class ClaimsSubjectProvider(ICurrentPrincipalAccessor accessor) : IPermissionSubjectProvider
    {
        public int CurrentCalls { get; private set; }

        public int ExplicitCalls { get; private set; }

        public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
        {
            CurrentCalls++;
            return Task.FromResult(accessor.Principal is { } current ? Map(current) : null);
        }

        public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            ExplicitCalls++;
            return Task.FromResult(Map(principal));
        }

        private static PermissionSubject? Map(ClaimsPrincipal principal)
            => new ClaimTypeOptions().FindUserId(principal) is { } userId ? new PermissionSubject(userId, [], IsSuperAdmin: false) : null;
    }

    private sealed class SubjectGrantStore(Dictionary<string, string[]> grants) : IPermissionGrantStore
    {
        public Task<PermissionGrantSet> GetGrantsAsync(
            string providerName,
            string providerKey,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(
            string providerName,
            IReadOnlyCollection<string> providerKeys,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(
            string userId,
            IReadOnlyCollection<string> roleIds,
            CancellationToken cancellationToken = default)
        {
            var names = grants.TryGetValue(userId, out var list) ? list : [];
            return Task.FromResult(new SubjectPermissionGrants(
                new PermissionGrantSet(PermissionGrantProviderNames.User, userId, names, names.Length),
                []));
        }
    }

    private sealed class SwitchableTenant(Guid? id) : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; } = id;
        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class SwitchablePrincipalAccessor(ClaimsPrincipal principal) : ICurrentPrincipalAccessor
    {
        public ClaimsPrincipal? Principal { get; private set; } = principal;

        public IDisposable Change(ClaimsPrincipal principal)
        {
            var previous = Principal;
            Principal = principal;
            return new Restore(() => Principal = previous);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
