using Leistd.MultiTenancy.Context;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Exceptions;
using Leistd.TestBase.Doubles;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;

namespace Leistd.Settings.Tests.Core;

// 回落顺序：用户级 → 租户级 → 代码默认值。
public class SettingResolutionTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Falls_back_to_the_code_default_when_no_level_has_a_value()
    {
        var provider = Build(new FakeSettingStore());

        Assert.Equal("zh-CN", await provider.GetOrNullAsync("Display.Language"));
    }

    [Fact]
    public async Task A_tenant_value_overrides_the_code_default()
    {
        var store = new FakeSettingStore();
        store.Tenant["Display.Language"] = "en-US";

        Assert.Equal("en-US", await Build(store).GetOrNullAsync("Display.Language"));
    }

    [Fact]
    public async Task A_user_value_overrides_the_tenant_value()
    {
        var store = new FakeSettingStore();
        store.Tenant["Display.Language"] = "en-US";
        store.User["Display.Language"] = "ja-JP";

        Assert.Equal("ja-JP", await Build(store).GetOrNullAsync("Display.Language"));
    }

    // 只允许租户级的设置，即使库里存在用户级的行也不参与回落——
    // 否则改一次定义的 Scopes 就会让历史遗留行悄悄重新生效。
    [Fact]
    public async Task A_user_value_is_ignored_when_the_definition_forbids_the_user_scope()
    {
        var store = new FakeSettingStore();
        store.User["Export.MaxRowsPerFile"] = "leaked";

        Assert.Equal("Acme", await Build(store).GetOrNullAsync("Export.MaxRowsPerFile"));
    }

    // 匿名调用只回落到租户级，不去查用户级。
    [Fact]
    public async Task An_anonymous_caller_resolves_from_the_tenant_level()
    {
        var store = new FakeSettingStore();
        store.Tenant["Display.Language"] = "en-US";
        store.User["Display.Language"] = "ja-JP";

        var provider = Build(store, currentUser: new FakeCurrentUser());

        Assert.Equal("en-US", await provider.GetOrNullAsync("Display.Language"));
        Assert.Equal(0, store.UserReads);
    }

    // 未定义与"值为空"是两件事，不能都表现为返回默认值。
    [Fact]
    public async Task Reading_an_undefined_setting_fails()
    {
        var provider = Build(new FakeSettingStore());

        var error = await Assert.ThrowsAsync<UndefinedSettingException>(() => provider.GetOrNullAsync("Nope"));

        Assert.Equal("Nope", error.SettingName);
    }

    // 一次请求内只查一次库：同一请求里前后两次读取必须看到同一份值。
    [Fact]
    public async Task Values_are_loaded_once_per_scope()
    {
        var store = new FakeSettingStore();
        var provider = Build(store);

        await provider.GetOrNullAsync("Display.Language");
        await provider.GetOrNullAsync("Export.MaxRowsPerFile");
        await provider.GetAllAsync();

        Assert.Equal(1, store.TenantReads);
        Assert.Equal(1, store.UserReads);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("7", 7)]
    public async Task Typed_reads_convert_the_stored_string(string stored, int expected)
    {
        var store = new FakeSettingStore();
        store.Tenant["Display.PageSize"] = stored;

        Assert.Equal(expected, await Build(store).GetAsync<int>("Display.PageSize"));
    }

    [Fact]
    public async Task GetAll_can_be_narrowed_to_client_visible_settings()
    {
        var all = await Build(new FakeSettingStore()).GetAllAsync();
        var visible = await Build(new FakeSettingStore()).GetAllAsync(visibleToClientsOnly: true);

        Assert.Contains("Export.MaxRowsPerFile", all.Keys);
        Assert.DoesNotContain("Export.MaxRowsPerFile", visible.Keys);
        Assert.Contains("Display.Language", visible.Keys);
    }

    /// <summary>宿主上下文下，进程级设置从宿主那一行读出来；清掉那一行才回落到代码默认值。</summary>
    /// <remarks>
    /// 这条钉的是公共读取契约：把 Host 加进定义与写入端，却漏了解析端，症状是"写进去了、
    /// 读出来还是默认值"，而写入与界面都看不出异常——最难从现象反推的一类问题。
    /// </remarks>
    [Fact]
    public async Task A_host_value_is_resolved_in_the_host_context()
    {
        var store = new FakeSettingStore();
        store.Host["Logging.MinimumLevel"] = "Debug";

        Assert.Equal("Debug", await Build(store).GetOrNullAsync("Logging.MinimumLevel"));

        store.Host.Clear();
        Assert.Equal("Information", await Build(store).GetOrNullAsync("Logging.MinimumLevel"));
    }

    // 进程级设置不接在租户级的回落链上：库里存在同名的租户行也不参与解析，
    // 否则改一次定义就会让历史遗留行悄悄顶替进程级的值。
    [Fact]
    public async Task A_tenant_row_never_stands_in_for_a_host_value()
    {
        var store = new FakeSettingStore();
        store.Tenant["Logging.MinimumLevel"] = "leaked";

        Assert.Equal("Information", await Build(store).GetOrNullAsync("Logging.MinimumLevel"));
    }

    /// <summary>租户上下文下进程级设置不可读：单项读取抛错，批量读取不包含它。</summary>
    /// <remarks>
    /// 这里刻意不返回代码默认值。那个值看着有效，调用方分不出"这就是当前生效的级别"
    /// 和"这一层在当前上下文根本读不到"，而前者会被直接展示或用于判断。
    /// </remarks>
    [Fact]
    public async Task A_host_setting_is_not_readable_in_a_tenant_context()
    {
        var store = new FakeSettingStore { CanAccessHostScope = false };
        store.Host["Logging.MinimumLevel"] = "Debug";

        await Assert.ThrowsAsync<HostScopeUnavailableException>(
            () => Build(store).GetOrNullAsync("Logging.MinimumLevel"));

        var all = await Build(store).GetAllAsync();
        Assert.DoesNotContain("Logging.MinimumLevel", all.Keys);
        // 其余设置照常解析：一项读不到不该把整批读取打断
        Assert.Contains("Display.Language", all.Keys);
        Assert.Equal(0, store.HostReads);
    }

    // 第一次加载中途失败会在请求级缓存里留下半截状态：租户级已写入、用户级还是 null。
    // 调用方捕获异常后在同一 scope 内重试时，加载会因为「租户级已加载」直接返回，
    // 之后读用户级就是空引用——所以两次查询必须一起发布。
    [Fact]
    public async Task A_failed_load_does_not_poison_the_request_scoped_cache()
    {
        var store = new FakeSettingStore { FailingUserReads = 1 };
        store.Tenant["Display.Language"] = "en-US";
        store.User["Display.Language"] = "ja-JP";
        var provider = Build(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetOrNullAsync("Display.Language"));

        // 同一个 Provider 实例重试：这次应当完整加载并解析出用户级的值
        Assert.Equal("ja-JP", await provider.GetOrNullAsync("Display.Language"));
    }

    // 同一作用域里切换租户（模拟登录、后台逐租户处理）要读到目标租户的值：存储按调用时的租户读，缓存键也得带租户
    [Fact]
    public async Task Switching_tenants_in_one_scope_reads_each_tenant_once()
    {
        var tenantA = Guid.NewGuid();
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        var store = new TenantAwareStore(currentTenant);
        store.Rows[(null, SettingScopes.Tenant, null)] = new() { ["Display.Language"] = "en-US" };
        store.Rows[(tenantA, SettingScopes.Tenant, null)] = new() { ["Display.Language"] = "ja-JP" };
        store.Rows[(null, SettingScopes.Host, null)] = new() { ["Logging.MinimumLevel"] = "Debug" };
        var provider = new DefaultSettingProvider(
            new SettingDefinitionManager([new TestSettingDefinitionProvider()]),
            store,
            new FakeCurrentUser(UserId),
            currentTenant: currentTenant);

        Assert.Equal("en-US", await provider.GetOrNullAsync("Display.Language"));
        using (currentTenant.Change(tenantA))
        {
            Assert.Equal("ja-JP", await provider.GetOrNullAsync("Display.Language"));
            Assert.Equal("ja-JP", await provider.GetOrNullAsync("Display.Language"));
            // 宿主层在租户键下不可达，不复用宿主那份快照
            await Assert.ThrowsAsync<HostScopeUnavailableException>(
                () => provider.GetOrNullAsync("Logging.MinimumLevel"));
        }

        Assert.Equal("en-US", await provider.GetOrNullAsync("Display.Language"));
        Assert.Equal("Debug", await provider.GetOrNullAsync("Logging.MinimumLevel"));
        Assert.Equal(1, store.Reads[(null, SettingScopes.Tenant, null)]);
        Assert.Equal(1, store.Reads[(tenantA, SettingScopes.Tenant, null)]);
    }

    // 两个已登录用户各有覆盖：A → B → A 各读到自己的值，且回到 A 时复用 A 的快照
    [Fact]
    public async Task Switching_users_in_one_scope_keeps_each_users_overrides_apart()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        var store = new TenantAwareStore(currentTenant);
        store.Rows[(null, SettingScopes.User, userA.ToString())] = new() { ["Display.Language"] = "ja-JP" };
        store.Rows[(null, SettingScopes.User, userB.ToString())] = new() { ["Display.Language"] = "fr-FR" };
        var currentUser = new SwitchableCurrentUser(userA);
        var provider = new DefaultSettingProvider(
            new SettingDefinitionManager([new TestSettingDefinitionProvider()]), store, currentUser, currentTenant: currentTenant);

        Assert.Equal("ja-JP", await provider.GetOrNullAsync("Display.Language"));
        currentUser.Id = userB;
        Assert.Equal("fr-FR", await provider.GetOrNullAsync("Display.Language"));
        currentUser.Id = userA;
        Assert.Equal("ja-JP", await provider.GetOrNullAsync("Display.Language"));
        Assert.Equal(1, store.Reads[(null, SettingScopes.User, userA.ToString())]);
    }

    // 写入作废全部快照：先后加载过宿主与租户两份，写后回到任一上下文都重新读库
    [Fact]
    public async Task A_write_invalidates_every_snapshot_in_the_scope()
    {
        var tenantA = Guid.NewGuid();
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        var store = new TenantAwareStore(currentTenant);
        var definitions = new SettingDefinitionManager([new TestSettingDefinitionProvider()]);
        var provider = new DefaultSettingProvider(definitions, store, new FakeCurrentUser(UserId), currentTenant: currentTenant);
        var manager = new DefaultSettingManager(definitions, store, provider, [], WriteDependencies.Lock(), new RecordingUnitOfWorkManager());

        await provider.GetOrNullAsync("Display.Language");
        using (currentTenant.Change(tenantA))
        {
            await provider.GetOrNullAsync("Display.Language");
        }

        await manager.SetAsync("Display.Language", "en-US", SettingScopes.Tenant);
        await provider.GetOrNullAsync("Display.Language");
        using (currentTenant.Change(tenantA))
        {
            await provider.GetOrNullAsync("Display.Language");
        }

        Assert.Equal(2, store.Reads[(null, SettingScopes.Tenant, null)]);
        Assert.Equal(2, store.Reads[(tenantA, SettingScopes.Tenant, null)]);
    }

    // 替别人判断时按当前租户读目标用户：切到另一租户后读到的是该租户下的覆盖
    [Fact]
    public async Task Reading_for_another_user_follows_the_current_tenant()
    {
        var tenantA = Guid.NewGuid();
        var recipient = Guid.NewGuid().ToString();
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);
        var store = new TenantAwareStore(currentTenant);
        store.Rows[(null, SettingScopes.User, recipient)] = new() { ["Display.Language"] = "ja-JP" };
        store.Rows[(tenantA, SettingScopes.User, recipient)] = new() { ["Display.Language"] = "fr-FR" };
        var provider = new DefaultSettingProvider(
            new SettingDefinitionManager([new TestSettingDefinitionProvider()]), store, new FakeCurrentUser(UserId), currentTenant: currentTenant);

        Assert.Equal("ja-JP", await provider.GetOrNullForUserAsync("Display.Language", recipient));
        using (currentTenant.Change(tenantA))
        {
            Assert.Equal("fr-FR", await provider.GetOrNullForUserAsync("Display.Language", recipient));
        }
    }

    private static ISettingProvider Build(ISettingStore store, FakeCurrentUser? currentUser = null)
        => new DefaultSettingProvider(
            new SettingDefinitionManager([new TestSettingDefinitionProvider()]),
            store,
            currentUser ?? new FakeCurrentUser(UserId));

    // 按当前租户分开存放的存储，记录每个租户每层的读取次数
    private sealed class TenantAwareStore(ICurrentTenant currentTenant) : ISettingStore
    {
        public Dictionary<(Guid?, SettingScopes, string?), Dictionary<string, string>> Rows { get; } = [];

        public Dictionary<(Guid?, SettingScopes, string?), int> Reads { get; } = [];

        public bool CanAccessHostScope => !currentTenant.IsAvailable;

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(
            SettingScopes scope, string? userId, CancellationToken cancellationToken = default)
        {
            if (scope == SettingScopes.Host && !CanAccessHostScope)
            {
                return Task.FromException<IReadOnlyDictionary<string, string>>(new HostScopeUnavailableException());
            }

            var key = (currentTenant.Id, scope, scope == SettingScopes.User ? userId : null);
            Reads[key] = Reads.GetValueOrDefault(key) + 1;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                Rows.TryGetValue(key, out var values) ? values : new Dictionary<string, string>());
        }

        public Task RemoveAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetAsync(
            string name, string? value, SettingScopes scope, string? userId, CancellationToken cancellationToken = default)
        {
            var key = (currentTenant.Id, scope, scope == SettingScopes.User ? userId : null);
            if (!Rows.TryGetValue(key, out var values))
            {
                Rows[key] = values = new Dictionary<string, string>();
            }

            if (value is null)
            {
                values.Remove(name);
            }
            else
            {
                values[name] = value;
            }

            return Task.CompletedTask;
        }
    }

    // 同一作用域里换主体（如结束模拟）；其余成员转给固定的 FakeCurrentUser
    private sealed class SwitchableCurrentUser(Guid? id) : ICurrentUser
    {
        public Guid? Id { get; set; } = id;

        private FakeCurrentUser Inner => new(Id);

        public bool IsAuthenticated => Inner.IsAuthenticated;

        public string? SubjectId => Inner.SubjectId;

        public Guid? TenantId => Inner.TenantId;

        public string? Username => Inner.Username;

        public string? Name => Inner.Name;

        public string? Email => Inner.Email;

        public System.Security.Claims.Claim? FindClaim(string claimType) => Inner.FindClaim(claimType);

        public IReadOnlyList<System.Security.Claims.Claim> FindClaims(string claimType) => Inner.FindClaims(claimType);

        public bool IsInRole(string role) => Inner.IsInRole(role);
    }

    private sealed class TestSettingDefinitionProvider : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Display.Language", "zh-CN", SettingScopes.All).IsVisibleToClients = true;
            context.Add("Display.PageSize", "20", SettingScopes.All).IsVisibleToClients = true;
            context.Add("Export.MaxRowsPerFile", "Acme", SettingScopes.Tenant);
            context.Add("Logging.MinimumLevel", "Information", SettingScopes.Host).IsVisibleToClients = true;
        }
    }
}
