using System.Net;
using System.Net.Http.Json;
#if (LocalIdentity)
using System.Text;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
#endif
using CompanyName.ProjectName.Application.Permissions.Provider;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Roles.Dtos;
#endif
using CompanyName.ProjectName.Application.Users.Dtos;
using CompanyName.ProjectName.Domain.Users.Errors;
#if (LocalIdentity)
using CompanyName.ProjectName.Infrastructure.Persistence;
#endif
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
#if (!LocalIdentity)
using Leistd.MultiTenancy.Context;
#endif
#if (LocalIdentity)
using Microsoft.EntityFrameworkCore;
#endif
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

#if (LocalIdentity)
/// <summary>用户批量操作：与单个操作同权限、同规则、同留痕，逐条提交、部分失败不回滚。</summary>
public sealed class UserBatchOperationTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    /// <summary>启停与解锁要用户更新权限，追加角色要角色分配权限，与单个操作一致。</summary>
    [Fact]
    public async Task Batch_operations_require_the_same_permissions_as_the_single_ones()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var target = await CreateUserAsync(superAdmin.Client);
        var role = await CreateRoleAsync(superAdmin.Client);
        await ReplaceGrantsAsync(role.Id, PermissionConstant.Users.Default);
        var operatorUser = await CreateUserAsync(superAdmin.Client, [role.Id]);
        using var acting = await Factory.LoginAsync(operatorUser.Username, TestPassword);
        var ids = new { Ids = new[] { target.Id } };
        var assign = new { Ids = new[] { target.Id }, RoleIds = new[] { role.Id } };

        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/enable", ids)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/disable", ids)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/unlock", ids)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/assign-roles", assign)).StatusCode);

        await ReplaceGrantsAsync(role.Id, PermissionConstant.Users.Default, PermissionConstant.Users.Update);
        Assert.Equal(HttpStatusCode.OK, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/enable", ids)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/unlock", ids)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/assign-roles", assign)).StatusCode);

        await ReplaceGrantsAsync(role.Id, PermissionConstant.Users.Default, PermissionConstant.Users.ManageRoles);
        Assert.Equal(HttpStatusCode.OK, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/assign-roles", assign)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.Client.PostAsJsonAsync("/api/v1/users/batch/enable", ids)).StatusCode);
    }

    /// <summary>一次 1 至 100 个用户；为空、缺键或超过上限都是对应字段的 400，一条都不执行。</summary>
    [Theory]
    [InlineData("batch/disable", """{"ids":[]}""", "ids")]
    [InlineData("batch/disable", "{}", "ids")]
    [InlineData("batch/disable", null, "ids")]
    [InlineData("batch/assign-roles", null, "ids")]
    [InlineData("batch/assign-roles", """{"ids":["0198f2a1-0000-7000-8000-000000000001"],"roleIds":[]}""", "roleIds")]
    [InlineData("batch/assign-roles", """{"ids":["0198f2a1-0000-7000-8000-000000000001"]}""", "roleIds")]
    public async Task Batch_size_is_validated_per_field(string action, string? json, string field)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        // null 代表超过上限：101 个 Id
        json ??= JsonSerializer.Serialize(new
        {
            ids = Enumerable.Range(0, BatchUsersInputDto.MaxCount + 1).Select(_ => Guid.NewGuid()),
            roleIds = new[] { Guid.NewGuid() }
        });

        using var response = await superAdmin.Client.PostAsync(
            $"/api/v1/users/{action}", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == field);
    }

    /// <summary>
    /// 逐条执行：停用自己、停用内置超级管理员、目标不存在都以单个操作的错误码被拒，其余照常提交；
    /// 重复的 Id 只处理一次。每条成功与被拒都留下以目标为单位的记录，说明与单个操作的 <c>detail</c> 一致。
    /// </summary>
    [Fact]
    public async Task Rejected_items_carry_the_single_operation_codes_and_do_not_roll_back_the_others()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var first = await CreateUserAsync(superAdmin.Client);
        var second = await CreateUserAsync(superAdmin.Client);
        var role = await CreateRoleAsync(superAdmin.Client);
        await ReplaceGrantsAsync(role.Id, PermissionConstant.Users.Default, PermissionConstant.Users.Update);
        var operatorUser = await CreateUserAsync(superAdmin.Client, [role.Id]);
        using var acting = await Factory.LoginAsync(operatorUser.Username, TestPassword);
        var superAdminId = await GetSuperAdminIdAsync();
        var missing = Guid.NewGuid();

        var result = await PostBatchAsync(acting.Client, "batch/disable",
            new { Ids = new[] { first.Id, operatorUser.Id, superAdminId, missing, first.Id, second.Id } });

        Assert.Equal(new[] { first.Id, operatorUser.Id, superAdminId, missing, second.Id }, result.Items.Select(item => item.Id));
        AssertSucceeded(result, first.Id);
        AssertSucceeded(result, second.Id);
        AssertFailed(result, operatorUser.Id, UserErrorCodes.SelfDisableForbidden);
        AssertFailed(result, superAdminId, UserErrorCodes.SuperAdminDisableForbidden);
        AssertFailed(result, missing, UserErrorCodes.NotFound);

        Assert.False((await GetUserAsync(superAdmin.Client, first.Id)).IsActive);
        Assert.False((await GetUserAsync(superAdmin.Client, second.Id)).IsActive);
        Assert.True((await GetUserAsync(superAdmin.Client, operatorUser.Id)).IsActive);
        // 被拒的停用没有撤销操作者自己的会话
        Assert.Equal(HttpStatusCode.OK, (await acting.Client.GetAsync("/api/v1/users?offset=0&limit=1")).StatusCode);

        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserDisabled, first.Id.ToString()));
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserDisabled, second.Id.ToString()));
        foreach (var (id, code) in new[]
        {
            (operatorUser.Id, UserErrorCodes.SelfDisableForbidden),
            (superAdminId, UserErrorCodes.SuperAdminDisableForbidden),
            (missing, UserErrorCodes.NotFound)
        })
        {
            Assert.Equal(
                new List<(string?, string?)> { (code, PermissionConstant.Users.Update) },
                await OperationRecordQueries.GetFailuresAsync(Factory, superAdmin.Client, OperationRecordActions.UserDisabled, id.ToString()));
        }

        // 说明与单个操作被拒时的 detail 同一口径
        using var single = await acting.Client.PatchAsync($"/api/v1/users/{operatorUser.Id}/disable", null);
        using var problem = JsonDocument.Parse(await single.Content.ReadAsStringAsync());
        Assert.Equal(
            problem.RootElement.GetProperty("detail").GetString(),
            result.Items.Single(item => item.Id == operatorUser.Id).Message);
    }

    /// <summary>已是目标状态的用户静默成功、不留记录，与单个启用、解锁一致。</summary>
    [Fact]
    public async Task Users_already_in_the_target_state_succeed_without_a_record()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var locked = await CreateUserAsync(superAdmin.Client);
        var unlocked = await CreateUserAsync(superAdmin.Client);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            (await db.Users.SingleAsync(user => user.Id == locked.Id)).Lock();
            await db.SaveChangesAsync();
        }

        var enabled = await PostBatchAsync(superAdmin.Client, "batch/enable", new { Ids = new[] { locked.Id, unlocked.Id } });
        var unlockedResult = await PostBatchAsync(superAdmin.Client, "batch/unlock", new { Ids = new[] { locked.Id, unlocked.Id } });

        Assert.All(enabled.Items.Concat(unlockedResult.Items), item => Assert.True(item.Succeeded));
        Assert.False((await GetUserAsync(superAdmin.Client, locked.Id)).IsLockedOut);
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserEnabled, locked.Id.ToString()));
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserUnlocked, locked.Id.ToString()));
        Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserUnlocked, unlocked.Id.ToString()));
    }

    /// <summary>
    /// 追加角色只加不撤：已有角色保留，已持有全部角色时不再留记录；内置超级管理员受保护；
    /// 有不存在的角色时整个请求被拒，一条都不执行。
    /// </summary>
    [Fact]
    public async Task Assigning_roles_only_appends()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var existing = await CreateRoleAsync(superAdmin.Client);
        var appended = await CreateRoleAsync(superAdmin.Client);
        var target = await CreateUserAsync(superAdmin.Client, [existing.Id]);
        var role = await CreateRoleAsync(superAdmin.Client);
        await ReplaceGrantsAsync(role.Id, PermissionConstant.Users.Default, PermissionConstant.Users.ManageRoles);
        var operatorUser = await CreateUserAsync(superAdmin.Client, [role.Id]);
        using var acting = await Factory.LoginAsync(operatorUser.Username, TestPassword);
        var superAdminId = await GetSuperAdminIdAsync();

        var result = await PostBatchAsync(acting.Client, "batch/assign-roles",
            new { Ids = new[] { target.Id, superAdminId }, RoleIds = new[] { appended.Id } });

        AssertSucceeded(result, target.Id);
        AssertFailed(result, superAdminId, UserErrorCodes.SuperAdminUpdateForbidden);
        Assert.Equal(
            new[] { existing.Id, appended.Id }.Order(),
            (await GetRolesAsync(superAdmin.Client, target.Id)).Select(item => item.Id).Order());
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserRolesReplaced, target.Id.ToString()));

        var again = await PostBatchAsync(acting.Client, "batch/assign-roles",
            new { Ids = new[] { target.Id }, RoleIds = new[] { appended.Id, existing.Id } });
        AssertSucceeded(again, target.Id);
        Assert.Equal(1, await OperationRecordQueries.CountSucceededAsync(Factory, superAdmin.Client, OperationRecordActions.UserRolesReplaced, target.Id.ToString()));

        using var unknownRole = await acting.Client.PostAsJsonAsync("/api/v1/users/batch/assign-roles",
            new { Ids = new[] { target.Id }, RoleIds = new[] { role.Id, Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
        using var problem = JsonDocument.Parse(await unknownRole.Content.ReadAsStringAsync());
        Assert.Equal(UserErrorCodes.RolesNotFound, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(2, (await GetRolesAsync(superAdmin.Client, target.Id)).Count);
    }

    private static void AssertSucceeded(BatchOperationOutputDto result, Guid id)
    {
        var item = result.Items.Single(item => item.Id == id);
        Assert.True(item.Succeeded);
        Assert.Null(item.ErrorCode);
        Assert.Null(item.Message);
    }

    private static void AssertFailed(BatchOperationOutputDto result, Guid id, string code)
    {
        var item = result.Items.Single(item => item.Id == id);
        Assert.False(item.Succeeded);
        Assert.Equal(code, item.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(item.Message));
    }

    private static async Task<BatchOperationOutputDto> PostBatchAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/users/{action}", body);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Batch {action} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<BatchOperationOutputDto>())!;
    }

    private static async Task<UserManagementOutputDto> GetUserAsync(HttpClient client, Guid id)
        => (await client.GetFromJsonAsync<UserManagementOutputDto>($"/api/v1/users/{id}"))!;

    private static async Task<List<RoleBriefOutputDto>> GetRolesAsync(HttpClient client, Guid id)
        => (await client.GetFromJsonAsync<List<RoleBriefOutputDto>>($"/api/v1/users/{id}/roles"))!;

    private async Task<Guid> GetSuperAdminIdAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Users
            .Where(user => user.IsSuperAdmin)
            .Select(user => user.Id)
            .SingleAsync();
    }

    private async Task ReplaceGrantsAsync(Guid roleId, params string[] permissions)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>()
            .ReplaceGrantsAsync(PermissionGrantProviderNames.Role, roleId.ToString(), permissions);
    }

    private static async Task<RoleOutputDto> CreateRoleAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        using var response = await client.PostAsJsonAsync(
            "/api/v1/roles",
            new CreateRoleInputDto { Name = $"role_{suffix}", DisplayName = $"Batch role {suffix}" });
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Create role failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RoleOutputDto>())!;
    }
}
#else
/// <summary>资源服务形态的批量启停：与单个启停同一规则，成员不能停用自己，其他条目照常提交。</summary>
public sealed class UserBatchOperationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task A_member_cannot_disable_themselves_in_a_batch_while_the_others_are_disabled()
    {
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        var operatorId = Guid.CreateVersion7();
        var otherId = Guid.CreateVersion7();
        using var acting = factory.CreateResourceSession(operatorId, tenantId);
        using (var other = factory.CreateResourceSession(otherId, tenantId))
        {
            // 首次请求把主体投影成本地用户行
            Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await acting.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            await scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(PermissionConstant.Users.Update, PermissionGrantProviderNames.User, operatorId.ToString());
        }

        using var response = await acting.Client.PostAsJsonAsync(
            "/api/v1/users/batch/disable", new { Ids = new[] { operatorId, otherId } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BatchOperationOutputDto>())!;

        var self = result.Items.Single(item => item.Id == operatorId);
        Assert.False(self.Succeeded);
        Assert.Equal(UserErrorCodes.SelfDisableForbidden, self.ErrorCode);
        Assert.True(result.Items.Single(item => item.Id == otherId).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await acting.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
#endif
