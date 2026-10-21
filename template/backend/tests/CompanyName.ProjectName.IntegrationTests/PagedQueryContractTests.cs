#if (LocalIdentity)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.Data.Paging;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>列表的分页、Dynamic LINQ 排序及凭据限制。</summary>
public sealed class PagedQueryContractTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string InvalidSortingMessage = "The sorting expression is not valid.";

    [Theory]
    [InlineData("offset=-1&limit=10")]
    [InlineData("offset=0&limit=0")]
    public async Task Out_of_range_paging_is_rejected_with_400(string query)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var response = await superAdmin.Client.GetAsync($"/api/v1/users?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>上限存在且生效：越过 <see cref="PageRequest.MaximumLimit"/> 即 400</summary>
    /// <remarks>
    /// 上限本身不是业务分页大小，只是把"一个拥有列表权限的调用方单次能要走多少"封顶。
    /// 因此用例断言的是边界两侧，而不是某个具体数字。
    /// </remarks>
    [Fact]
    public async Task Limit_is_capped()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        Assert.Equal(
            HttpStatusCode.OK,
            (await superAdmin.Client.GetAsync(
                $"/api/v1/users?offset=0&limit={PageRequest.MaximumLimit}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await superAdmin.Client.GetAsync(
                $"/api/v1/users?offset=0&limit={PageRequest.MaximumLimit + 1}")).StatusCode);
    }

    /// <summary>可查询属性直接排序；凭据由业务规则拒绝。</summary>
    [Theory]
    [InlineData("/api/v1/users", "username desc", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "email asc", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "creationTime desc", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "passwordHash asc", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/users", "isSuperAdmin desc", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "DisplayName asc, Username desc", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "lastLogin.Time desc, USERNAME ASC", HttpStatusCode.OK)]
    [InlineData("/api/v1/users", "securityStamp", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/users", "username, TwoFactor.Secret", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/users", "twofactor.recoverycodes", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/users", "PasswordHash.Substring(0,1)", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/roles", "displayName desc", HttpStatusCode.OK)]
    [InlineData("/api/v1/roles", "sort asc", HttpStatusCode.OK)]
    [InlineData("/api/v1/roles", "name asc", HttpStatusCode.OK)]
    [InlineData("/api/v1/roles", "sort descending, name ascending", HttpStatusCode.OK)]
#if (OpenIddictServer)
    [InlineData("/api/v1/open-applications", "clientType desc, ClientId asc", HttpStatusCode.OK)]
#endif
    public async Task Sorting_uses_query_properties_and_rejects_sensitive_expressions(
        string path, string sorting, HttpStatusCode expectedStatus)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var response = await superAdmin.Client.GetAsync(
            $"{path}?offset=0&limit=10&sorting={Uri.EscapeDataString(sorting)}");

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus == HttpStatusCode.BadRequest)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("User:SortingCredentialsForbidden", body.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>解析不了的排序是调用方输入错误：sorting 字段的 400，固定文案，不回显表达式。</summary>
    /// <remarks>读凭据的 <c>iif(...)</c> 与开放应用的非公开元数据 <c>Application</c> 在解析阶段就失败，同样是字段错误而不是业务拒绝。</remarks>
    [Theory]
    [InlineData("/api/v1/users", "Username,")]
    [InlineData("/api/v1/users", "nonsense asc")]
    [InlineData("/api/v1/users", "username sideways")]
    [InlineData("/api/v1/users", "iif(PasswordHash > \"$2b\", 0, 1)")]
    [InlineData("/api/v1/roles", "sort,")]
#if (OpenIddictServer)
    [InlineData("/api/v1/open-applications", "Application")]
    [InlineData("/api/v1/open-applications", "Application.ClientSecret")]
#endif
    public async Task Unparseable_sorting_is_a_sorting_field_error(string path, string sorting)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var response = await superAdmin.Client.GetAsync(
            $"{path}?offset=0&limit=10&sorting={Uri.EscapeDataString(sorting)}");

        await AssertSortingFieldErrorAsync(response, sorting);
    }

    /// <summary>排序改变返回条目的顺序。</summary>
    [Fact]
    public async Task Accepted_sorting_actually_orders_the_result()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        // 至少两行才谈得上顺序
        await CreateUserAsync(superAdmin.Client);

        var ascending = await ReadUsernamesAsync(superAdmin.Client, "username asc");
        var descending = await ReadUsernamesAsync(superAdmin.Client, "username desc");

        Assert.True(ascending.Count > 1);
        Assert.Equal(ascending.OrderBy(name => name, StringComparer.Ordinal), ascending);
        Assert.Equal(ascending.AsEnumerable().Reverse(), descending);
    }

    /// <summary>省略排序与显式默认排序的并列记录次序一致。</summary>
    [Fact]
    public async Task Omitted_sorting_matches_the_explicit_default()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        foreach (var name in new[] { $"zeta_{suffix}", $"alpha_{suffix}" })
        {
            var created = await superAdmin.Client.PostAsJsonAsync("/api/v1/roles", new
            {
                name,
                displayName = name,
                sort = 4242,
                isDefault = false
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        var omitted = await ReadRoleNamesAsync(superAdmin.Client, sorting: null);
        var explicitDefault = await ReadRoleNamesAsync(superAdmin.Client, "sort asc");

        Assert.Equal(omitted, explicitDefault);

        // 并列排序号内按名称升序（与 GetAllAsync 同口径），因此 alpha 在 zeta 之前
        var tied = omitted.Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).ToList();
        Assert.Equal([$"alpha_{suffix}", $"zeta_{suffix}"], tied);
    }

    [Theory]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/roles")]
#if (OpenIddictServer)
    [InlineData("/api/v1/open-applications")]
#endif
    public async Task Unknown_sorting_is_rejected_even_when_no_rows_match(string path)
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var response = await admin.Client.GetAsync($"{path}?keyword={Guid.NewGuid():N}&sorting=unknownProperty");

        await AssertSortingFieldErrorAsync(response, "unknownProperty");
    }

    /// <summary>排序解析失败是预期的输入错误：全类别日志里没有表达式原文，也没有附带异常的条目。</summary>
    [Fact]
    public async Task Unparseable_sorting_leaves_neither_the_expression_nor_an_exception_in_logs()
    {
        var capture = new LogCapture();
        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogEventSink>(capture)));
        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        var cases = new List<(string Path, string Sorting)>
        {
            ("/api/v1/users", "nonsenseUserField asc"),
            ("/api/v1/users", "iif(PasswordHash > \"$2b\", 0, 1)"),
            ("/api/v1/roles", "nonsenseRoleField"),
#if (OpenIddictServer)
            ("/api/v1/open-applications", "Application.ClientSecret"),
#endif
        };
        capture.Clear();

        foreach (var (path, sorting) in cases)
        {
            var response = await admin.Client.GetAsync(
                $"{path}?keyword={Guid.NewGuid():N}&sorting={Uri.EscapeDataString(sorting)}");
            await AssertSortingFieldErrorAsync(response, sorting);
        }

        var events = capture.Events;
        // 处理器的预期失败日志确实被收到，"日志里没有"才证伪得了
        Assert.Equal(cases.Count, events.Count(e =>
            e.Properties.TryGetValue("ExceptionType", out var type) && type is ScalarValue { Value: "ValidationException" }));
        Assert.All(events, e =>
        {
            Assert.Null(e.Exception);
            var rendered = e.RenderMessage() + string.Join("|", e.Properties.Values);
            foreach (var (_, sorting) in cases)
                Assert.DoesNotContain(sorting, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("No property or field", rendered, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Missing_roles_do_not_bypass_sorting_validation()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var response = await admin.Client.GetAsync($"/api/v1/users?roles={Guid.NewGuid():N}&sorting=passwordHash");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_translation_failures_are_not_reported_as_sorting_parse_errors()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var response = await admin.Client.GetAsync("/api/v1/users?sorting=HasLocalPassword");

        // 可解析但无法翻译：服务端异常，不是排序字段错误
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Explicit_secondary_sort_takes_precedence_over_default_tie_breakers()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        foreach (var name in new[] { $"alpha_{suffix}", $"zeta_{suffix}" })
        {
            var response = await admin.Client.PostAsJsonAsync("/api/v1/roles", new
            {
                name, displayName = name, sort = 4343, isDefault = false
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var names = await ReadRoleNamesAsync(admin.Client, "sort asc, name desc");
        Assert.Equal([$"zeta_{suffix}", $"alpha_{suffix}"],
            names.Where(name => name.EndsWith(suffix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Equal_primary_keys_use_unique_ids_across_page_boundaries()
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        await CreateUserAsync(admin.Client);

        async Task<List<Guid>> ReadIds(int offset, int limit)
        {
            var response = await admin.Client.GetAsync(
                $"/api/v1/users?offset={offset}&limit={limit}&sorting=isActive");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()).ToList();
        }

        var all = await ReadIds(0, PageRequest.MaximumLimit);
        var first = await ReadIds(0, 1);
        var second = await ReadIds(1, 1);
        Assert.True(all.Count > 1);
        Assert.Equal(all.Take(2), first.Concat(second));
        Assert.NotEqual(first[0], second[0]);
    }

    [Theory]
    [InlineData("/api/v1/users")]
#if (OpenIddictServer)
    [InlineData("/api/v1/open-applications")]
#endif
    public async Task Dto_default_sorting_matches_blank_and_explicit_default(string path)
    {
        using var admin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        async Task<List<string>> ReadIds(string query)
        {
            var response = await admin.Client.GetAsync($"{path}?limit=100&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToList();
        }

        var omitted = await ReadIds("");
        Assert.Equal(omitted, await ReadIds("sorting="));
        Assert.Equal(omitted, await ReadIds("sorting=%20%20"));
        Assert.Equal(omitted, await ReadIds("sorting=CreationTime%20desc"));
    }

    private static async Task AssertSortingFieldErrorAsync(HttpResponseMessage response, string sorting)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(content);
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("sorting", error.GetProperty("field").GetString());
        Assert.Equal(InvalidSortingMessage, error.GetProperty("detail").GetString());
        Assert.DoesNotContain(sorting, content, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ReadRoleNamesAsync(HttpClient client, string? sorting)
    {
        var query = sorting is null
            ? "offset=0&limit=50"
            : $"offset=0&limit=50&sorting={Uri.EscapeDataString(sorting)}";

        var response = await client.GetAsync($"/api/v1/roles?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToList();
    }

    private static async Task<List<string>> ReadUsernamesAsync(HttpClient client, string sorting)
    {
        var response = await client.GetAsync(
            $"/api/v1/users?offset=0&limit=50&sorting={Uri.EscapeDataString(sorting)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("username").GetString()!)
            .ToList();
    }

    // 收集宿主 Serilog 管道的全部事件（各类别、生产配置的级别），不替换日志工厂
    private sealed class LogCapture : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> events = new();

        public IReadOnlyCollection<LogEvent> Events => events.ToArray();

        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);

        public void Clear() => events.Clear();
    }
}
#endif
