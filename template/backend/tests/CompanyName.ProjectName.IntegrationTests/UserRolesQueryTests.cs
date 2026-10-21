#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Domain.Users.Constants;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>用户分页接口多角色查询的输入约束：规范化（去空白/去重）、数量上限、单项长度上限。</summary>
public sealed class UserRolesQueryTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Unknown_roles_should_return_an_empty_page_instead_of_matching_everyone()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var response = await admin.Client.GetAsync(
            "/api/v1/users?offset=0&limit=10&roles=__no_such_role__");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedUsers>();
        Assert.NotNull(page);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Roles_filter_should_normalize_whitespace_and_duplicate_values()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var role = Uri.EscapeDataString(AdminConstant.RoleName);
        var padded = Uri.EscapeDataString($"  {AdminConstant.RoleName}  ");

        var response = await admin.Client.GetAsync(
            $"/api/v1/users?offset=0&limit=10&roles={role}&roles={padded}&roles=%20%20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedUsers>();
        Assert.NotNull(page);
        Assert.True(page.TotalCount >= 1);
        Assert.All(page.Items, user => Assert.Contains(AdminConstant.RoleName, user.Roles.Select(role => role.Name)));
    }

    [Fact]
    public async Task Roles_filter_should_reject_more_than_twenty_items()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var query = string.Join("&", Enumerable.Range(0, 21).Select(i => $"roles=role-{i}"));

        var response = await admin.Client.GetAsync($"/api/v1/users?offset=0&limit=10&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Roles_filter_should_accept_a_role_name_at_the_domain_length_limit()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var atLimit = new string('r', 64);

        var response = await admin.Client.GetAsync(
            $"/api/v1/users?offset=0&limit=10&roles={atLimit}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Roles_filter_should_reject_a_role_name_over_the_domain_length_limit()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var overlong = new string('r', 65);

        var response = await admin.Client.GetAsync(
            $"/api/v1/users?offset=0&limit=10&roles={overlong}");

        // 入参校验失败：字段错误落在 roles 上（与查询参数、JSON 契约同名），不带业务码
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == "roles");
    }

    private sealed record PagedUsers(int TotalCount, List<PagedUserItem> Items);

    private sealed record PagedUserItem(string Username, List<UserRoleItem> Roles);

    /// <summary>角色以 Id + 名称的结构返回：Id 用于提交，名称仅用于展示与筛选。</summary>
    private sealed record UserRoleItem(Guid Id, string Name, string DisplayName);
}
#endif
