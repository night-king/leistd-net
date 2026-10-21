#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>用户名、邮箱与角色名的可用性判定必须和数据库实际允许的插入一致。</summary>
/// <remarks>
/// 三者的唯一索引都没有排除 <c>IsDeleted</c>，被删的用户或角色仍然占着名字和邮箱，
/// 而仓储默认把软删除行过滤掉。可用性判定不关掉这层过滤就会答"可用"，随后落库撞唯一索引：
/// 用户看到 500，而不是"已被占用"。
/// </remarks>
public sealed class UserUniquenessTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "UniquenessTests!Passw0rd";

    [Fact]
    public async Task A_username_and_email_held_by_a_deleted_user_are_reported_as_taken()
    {
        var username = $"uniq_{Guid.NewGuid():N}"[..24];
        var email = $"{username}@example.test";

        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var id = await CreateAsync(admin, username, email);
        using (var delete = await admin.Client.DeleteAsync($"/api/v1/users/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        // 同名：数据库仍然占着，必须是业务错误而不是 500
        using (var sameUsername = await PostAsync(admin, username, $"other_{username}@example.test"))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameUsername.StatusCode);
            Assert.Equal("User:UsernameTaken", await ErrorCodeAsync(sameUsername));
        }

        // 同邮箱：同理
        using (var sameEmail = await PostAsync(admin, $"other_{username}"[..24], email))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
            Assert.Equal("User:EmailTaken", await ErrorCodeAsync(sameEmail));
        }
    }

    /// <summary>改资料走的是排除自身的那两个重载，同样要看见软删除行。</summary>
    [Fact]
    public async Task Updating_a_profile_to_a_deleted_users_name_or_email_is_reported_as_taken()
    {
        var taken = $"uniq_{Guid.NewGuid():N}"[..24];
        var takenEmail = $"{taken}@example.test";
        var mine = $"uniq_{Guid.NewGuid():N}"[..24];

        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var id = await CreateAsync(admin, taken, takenEmail);
        using (var delete = await admin.Client.DeleteAsync($"/api/v1/users/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        await CreateAsync(admin, mine, $"{mine}@example.test");
        using var session = await ProjectWebApplicationFactory.LoginAsync(factory, mine, Password);

        using (var sameUsername = await session.Client.PutAsJsonAsync("/api/v1/auth/me", new
        {
            Username = taken,
            Email = $"{mine}@example.test"
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameUsername.StatusCode);
            Assert.Equal("User:UsernameTaken", await ErrorCodeAsync(sameUsername));
        }

        using (var sameEmail = await session.Client.PutAsJsonAsync("/api/v1/auth/me", new
        {
            Username = mine,
            Email = takenEmail
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
            Assert.Equal("User:EmailTaken", await ErrorCodeAsync(sameEmail));
        }
    }

    /// <summary>管理员改邮箱与建号、改本人资料同一判定、同一个码：看得见软删除行，按唯一索引的原样比较。</summary>
    [Fact]
    public async Task Admin_email_change_reports_the_same_conflict_as_the_other_entries()
    {
        var taken = $"uniq_{Guid.NewGuid():N}"[..24];
        var takenEmail = $"{taken}@example.test";
        var other = $"uniq_{Guid.NewGuid():N}"[..24];

        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var deletedId = await CreateAsync(admin, taken, takenEmail);
        using (var delete = await admin.Client.DeleteAsync($"/api/v1/users/{deletedId}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        var otherId = await CreateAsync(admin, other, $"{other}@example.test");

        using (var sameEmail = await PutUserEmailAsync(admin, otherId, takenEmail))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
            Assert.Equal("User:EmailTaken", await ErrorCodeAsync(sameEmail));
        }

        // 被拒的那次没有改动目标用户
        Assert.Equal($"{other}@example.test", await GetUserEmailAsync(admin, otherId));

        // 唯一索引区分大小写，只差大小写的地址是另一个地址：各入口都放行
        using (var caseVariant = await PutUserEmailAsync(admin, otherId, takenEmail.ToUpperInvariant()))
        {
            Assert.Equal(HttpStatusCode.OK, caseVariant.StatusCode);
        }
    }

    /// <summary>删掉的角色仍占着角色名：重建同名角色必须是业务冲突，而不是撞唯一索引的 500。</summary>
    [Fact]
    public async Task Recreating_a_role_with_a_deleted_roles_name_is_reported_as_taken()
    {
        var name = $"role_{Guid.NewGuid():N}"[..24];

        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var id = await CreateRoleAsync(admin.Client, name);
        using (var delete = await admin.Client.DeleteAsync($"/api/v1/roles/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        using (var recreate = await PostRoleAsync(admin.Client, name))
        {
            Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
            Assert.Equal("Role:NameAlreadyUsed", await ErrorCodeAsync(recreate));
        }

        // 被拒的那次没有留下第二个同名角色
        Assert.DoesNotContain(await GetRoleNamesAsync(admin.Client), roleName => roleName == name);
    }
#if (IncludeMultiTenancy)

    /// <summary>查重只关软删除过滤，不关租户过滤：唯一索引按租户分开，宿主删掉的名字租户照样能用。</summary>
    [Fact]
    public async Task A_role_name_deleted_on_the_host_stays_available_to_a_tenant()
    {
        var name = $"role_{Guid.NewGuid():N}"[..24];

        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var hostRoleId = await CreateRoleAsync(admin.Client, name);
        using (var delete = await admin.Client.DeleteAsync($"/api/v1/roles/{hostRoleId}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        using var tenantAdmin = await LoginTenantAdminAsync(await CreateTenantAsync(admin.Client));
        var tenantRoleId = await CreateRoleAsync(tenantAdmin, name);

        Assert.NotEqual(hostRoleId, tenantRoleId);
        Assert.Contains(await GetRoleNamesAsync(tenantAdmin), roleName => roleName == name);
    }

    private const string TenantAdminPassword = "Tenant@123456";

    private static async Task<Guid> CreateTenantAsync(HttpClient hostAdmin)
    {
        var name = $"roles-{Guid.NewGuid():N}"[..16];
        using var response = await hostAdmin.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.com",
            AdminPassword = TenantAdminPassword
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> LoginTenantAdminAsync(Guid tenantId)
    {
        var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        client.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "admin", Password = TenantAdminPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])));
        return client;
    }
#endif

    private static async Task<Guid> CreateRoleAsync(HttpClient client, string name)
    {
        using var create = await PostRoleAsync(client, name);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostRoleAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/api/v1/roles", new { Name = name, DisplayName = name });

    private static async Task<List<string>> GetRoleNamesAsync(HttpClient client)
    {
        var roles = await client.GetFromJsonAsync<JsonElement>("/api/v1/roles/options");
        return [.. roles.EnumerateArray().Select(role => role.GetProperty("name").GetString()!)];
    }

    private async Task<Guid> CreateAsync(AuthenticatedSession admin, string username, string email)
    {
        using var create = await PostAsync(admin, username, email);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PutUserEmailAsync(AuthenticatedSession admin, Guid id, string email) =>
        admin.Client.PutAsJsonAsync($"/api/v1/users/{id}", new { Email = email });

    private static async Task<string?> GetUserEmailAsync(AuthenticatedSession admin, Guid id)
    {
        var user = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{id}");
        return user.GetProperty("email").GetString();
    }

    private static Task<HttpResponseMessage> PostAsync(AuthenticatedSession admin, string username, string email) =>
        admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = email,
            Password,
            IsActive = true
        });

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
#endif
