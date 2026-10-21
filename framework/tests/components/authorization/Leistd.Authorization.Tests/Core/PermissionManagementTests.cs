using Leistd.TestBase.Doubles;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Errors;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Management;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Dtos;
using Leistd.Authorization.Events;
using Leistd.Authorization.Exceptions;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leistd.Authorization.Tests.Core;

/// <summary>权限管理用例：与检查器同一侧别判据、主体经宿主目录确认、替换带版本并发布事件；首次授予只写一次。</summary>
public sealed class PermissionManagementTests
{
    private const string HostOnly = "App.Tenants";
    private const string BothSides = "App.Orders";
    private const string BothSidesChild = "App.Orders.Read";
    private const string Disabled = "App.Reports";
    private const string RoleKey = "role-1";

    private readonly InMemoryGrants _grants = new();
    private readonly RecordingEventBus _events = new();

    [Fact]
    public async Task A_tenant_manager_never_sees_host_side_permissions()
    {
        var service = Build(tenantId: Guid.NewGuid());

        var definitions = await service.GetDefinitionsAsync();
        var grants = await service.GetGrantsAsync(PermissionGrantProviderNames.Role, RoleKey);

        var group = Assert.Single(definitions);
        Assert.Equal("App", group.Name);
        Assert.Equal([BothSides], group.Permissions.Select(p => p.Name));
        Assert.Equal([BothSides, BothSidesChild], grants.Grants.Select(g => g.Name));
    }

    // 整组都不可用时不下发空壳，停用的权限也不可勾选
    [Fact]
    public async Task Empty_groups_and_disabled_permissions_are_left_out()
    {
        var definitions = await Build(tenantId: Guid.NewGuid()).GetDefinitionsAsync();

        Assert.DoesNotContain(definitions, g => g.Name == "System");
        Assert.DoesNotContain(definitions.SelectMany(g => g.Permissions), p => p.Name == Disabled);
    }

    [Fact]
    public async Task Current_permissions_are_the_granted_names_available_on_this_side()
    {
        _grants.Set(PermissionGrantProviderNames.Role, RoleKey, [HostOnly, BothSides, "App.Removed"]);
        var service = Build(tenantId: Guid.NewGuid(), subject: new PermissionSubject("u1", [RoleKey], IsSuperAdmin: false));

        var current = await service.GetCurrentAsync();

        Assert.Equal([BothSides], current.Permissions);
        Assert.False(current.IsSuperAdmin);
    }

    [Fact]
    public async Task A_super_admin_gets_every_available_permission()
    {
        var current = await Build(subject: new PermissionSubject("u1", [], IsSuperAdmin: true)).GetCurrentAsync();

        Assert.Equal([HostOnly, BothSides, BothSidesChild], current.Permissions);
        Assert.True(current.IsSuperAdmin);
    }

    /// <summary>不是权限主体的调用方读自己的权限，拿到空集合，不是 401。</summary>
    /// <remarks>
    /// 端点挂着 RequireAuthorization，走到这里的调用方必然已认证，回 401 是在说假话；
    /// 客户端据此重新登录、再问、再 401，就是死循环。双 realm 部署（员工走 RBAC、
    /// 客户走另一套身份）会稳定踩中，因为客户令牌按设计就不在员工的主体空间里。
    /// 空集合意味着任何权限判定都不通过，拒绝效果与抛异常一致。
    /// </remarks>
    [Fact]
    public async Task A_caller_that_is_not_a_subject_reads_an_empty_permission_set()
    {
        var current = await Build(subject: null).GetCurrentAsync();

        Assert.Empty(current.Permissions);
        Assert.False(current.IsSuperAdmin);
        Assert.False(string.IsNullOrWhiteSpace(current.VersionToken));
    }

    [Fact]
    public async Task Grants_of_an_unknown_subject_are_not_found()
    {
        var service = Build();

        var read = await Assert.ThrowsAsync<BusinessException>(() => service.GetGrantsAsync(PermissionGrantProviderNames.Role, "ghost"));
        var write = await Assert.ThrowsAsync<BusinessException>(() => service.ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role, "ghost", new ReplacePermissionGrantsInputDto { PermissionNames = [BothSides] }));

        Assert.Equal(PermissionErrorCodes.SubjectNotFound, read.Code);
        Assert.Equal(PermissionErrorCodes.SubjectNotFound, write.Code);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Replacing_grants_returns_the_new_state_and_publishes_an_event()
    {
        var output = await Build().ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role,
            RoleKey,
            new ReplacePermissionGrantsInputDto { ExpectedVersion = 0, PermissionNames = [BothSides] });

        Assert.Equal(1, output.Version);
        Assert.True(Assert.Single(output.Grants, g => g.Name == BothSides).Granted);
        var replaced = Assert.IsType<PermissionGrantsReplacedEvent>(Assert.Single(_events.Published));
        Assert.Equal((PermissionGrantProviderNames.Role, RoleKey, "Administrators", 1L),
            (replaced.ProviderName, replaced.ProviderKey, replaced.SubjectDisplayName, replaced.Version));
    }

    [Fact]
    public async Task A_stale_version_is_a_coded_conflict()
    {
        _grants.Set(PermissionGrantProviderNames.Role, RoleKey, [BothSides]);

        var error = await Assert.ThrowsAsync<PermissionGrantConcurrencyException>(() => Build().ReplaceGrantsAsync(
            PermissionGrantProviderNames.Role, RoleKey, new ReplacePermissionGrantsInputDto { ExpectedVersion = 0 }));

        Assert.Equal(PermissionErrorCodes.ConcurrencyConflict, error.Code);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Too_many_permissions_in_one_replacement_are_rejected()
    {
        var input = new ReplacePermissionGrantsInputDto
        {
            PermissionNames = [.. Enumerable.Range(0, ReplacePermissionGrantsInputDto.MaximumPermissionCount + 1).Select(i => $"P{i}")]
        };

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => Build().ReplaceGrantsAsync(PermissionGrantProviderNames.Role, RoleKey, input));

        Assert.Contains(nameof(input.PermissionNames), error.ValidationResult.MemberNames);
    }

    [Fact]
    public async Task Seeding_grants_everything_available_on_the_side_only_once()
    {
        var seeder = Services().GetRequiredService<IPermissionGrantSeeder>();

        var first = await seeder.SeedAllAsync(PermissionGrantProviderNames.Role, RoleKey, MultiTenancySides.Tenant);
        _grants.Set(PermissionGrantProviderNames.Role, RoleKey, []);
        var second = await seeder.SeedAllAsync(PermissionGrantProviderNames.Role, RoleKey, MultiTenancySides.Tenant);

        Assert.Equal(2, first);
        Assert.Null(second);
    }

    [Fact]
    public void Undefined_permissions_carry_a_code_and_the_names()
    {
        var error = new UndefinedPermissionException(["App.A", "App.B"]);

        Assert.Equal(PermissionErrorCodes.UndefinedPermission, error.Code);
        Assert.Equal("App.A, App.B", error.LocalizationData["Names"]);
    }

    // 显示名按约定键查词条：权限 Permission:{名}、分组 PermissionGroup:{名}；定义里的 DisplayName 是默认文案
    [Fact]
    public async Task Display_names_are_translated_by_convention()
    {
        var service = Build(localized: new Dictionary<string, string>
        {
            ["PermissionGroup:App"] = "应用",
            ["Permission:App.Orders"] = "订单管理",
        });

        var group = Assert.Single(await service.GetDefinitionsAsync(), g => g.Name == "App");

        Assert.Equal("应用", group.DisplayName);
        var orders = Assert.Single(group.Permissions);
        Assert.Equal("订单管理", orders.DisplayName);
        // 缺词条：用定义里的默认文案，而不是把默认文案当键去查、查不到再露出技术名
        Assert.Equal("Read orders", Assert.Single(orders.Children).DisplayName);
    }

    // 不启用本地化的宿主看到的是默认文案；没写默认文案才回落到名称
    [Fact]
    public async Task Without_localization_the_default_text_is_shown()
    {
        var definitions = await Build().GetDefinitionsAsync();

        var group = Assert.Single(definitions, g => g.Name == "App");
        Assert.Equal("Application", group.DisplayName);
        Assert.Equal(BothSides, Assert.Single(group.Permissions).DisplayName);
        Assert.Equal("Read orders", Assert.Single(group.Permissions).Children.Single().DisplayName);
    }

    private IPermissionManagementService Build(
        Guid? tenantId = null,
        PermissionSubject? subject = null,
        Dictionary<string, string>? localized = null)
        => Services(tenantId, subject, localized).GetRequiredService<IPermissionManagementService>();

    private IServiceProvider Services(
        Guid? tenantId = null,
        PermissionSubject? subject = null,
        Dictionary<string, string>? localized = null)
        => new ServiceCollection()
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddPermissionAuthorizationCore(options =>
                options.LocalizationResource = localized is null ? null : typeof(PermissionManagementTests))
            .AddSingleton<IStringLocalizerFactory>(new DictionaryLocalizerFactory(localized ?? []))
            .AddSingleton<IPermissionDefinitionProvider, Definitions>()
            .AddSingleton<IPermissionGrantStore>(_grants)
            .AddSingleton<IPermissionGrantManager>(_grants)
            .AddSingleton<IPermissionSubjectProvider>(new FixedSubject(subject))
            .AddSingleton<IPermissionSubjectDirectory, Directory>()
            .AddSingleton<ICurrentTenant>(new FixedTenant(tenantId))
            .AddSingleton<ILocalEventBus>(_events)
            .BuildServiceProvider();

    private sealed class Definitions : IPermissionDefinitionProvider
    {
        public void Define(IPermissionDefinitionContext context)
        {
            context.GetOrAddGroup("System").AddPermission(HostOnly, MultiTenancySides.Host);
            var app = context.GetOrAddGroup("App", "Application");
            app.AddPermission(BothSides, MultiTenancySides.Both).AddChild(BothSidesChild, "Read orders");
            app.AddPermission(Disabled, MultiTenancySides.Both).IsEnabled = false;
        }
    }

    private sealed class Directory : IPermissionSubjectDirectory
    {
        public Task<PermissionSubjectInfo?> FindAsync(string providerName, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(providerName == PermissionGrantProviderNames.Role && providerKey == RoleKey
                ? new PermissionSubjectInfo("Administrators")
                : null);
    }

    private sealed class FixedSubject(PermissionSubject? subject) : IPermissionSubjectProvider
    {
        public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default) => Task.FromResult(subject);

        public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => Task.FromResult(subject);
    }

    private sealed class FixedTenant(Guid? id) : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; } = id;
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => throw new NotSupportedException();
    }

    // 按主体保存授予与版本；替换带期望版本，与 EF 实现的并发语义一致
    private sealed class InMemoryGrants : IPermissionGrantStore, IPermissionGrantManager
    {
        private readonly Dictionary<(string, string), PermissionGrantSet> _sets = [];

        public void Set(string providerName, string providerKey, IReadOnlyList<string> names)
        {
            var version = _sets.TryGetValue((providerName, providerKey), out var current) ? current.Version + 1 : 1;
            _sets[(providerName, providerKey)] = new PermissionGrantSet(providerName, providerKey, names, version);
        }

        public Task<PermissionGrantSet> GetGrantsAsync(string providerName, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_sets.GetValueOrDefault((providerName, providerKey)) ?? PermissionGrantSet.Empty(providerName, providerKey));

        public Task<IReadOnlyList<PermissionGrantSet>> GetGrantsAsync(string providerName, IReadOnlyCollection<string> providerKeys, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrantSet>>([.. providerKeys.Select(key => GetGrantsAsync(providerName, key).Result)]);

        public async Task<SubjectPermissionGrants> GetGrantsForSubjectAsync(string userId, IReadOnlyCollection<string> roleIds, CancellationToken cancellationToken = default)
            => new(await GetGrantsAsync(PermissionGrantProviderNames.User, userId, cancellationToken), await GetGrantsAsync(PermissionGrantProviderNames.Role, roleIds, cancellationToken));

        public Task<long> ReplaceGrantsAsync(string providerName, string providerKey, IReadOnlyCollection<string> permissionNames, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            var current = _sets.GetValueOrDefault((providerName, providerKey))?.Version ?? 0;
            if (expectedVersion is { } expected && expected != current)
            {
                throw new PermissionGrantConcurrencyException(providerName, providerKey, expected, current);
            }

            Set(providerName, providerKey, [.. permissionNames]);
            return Task.FromResult(current + 1);
        }

        public Task<int> RemoveProviderAsync(string providerName, string providerKey, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task GrantAsync(string permissionName, string providerName, string providerKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevokeAsync(string permissionName, string providerName, string providerKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
