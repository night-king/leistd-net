using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
#if (IncludeOperationRecords)
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Authorization.Errors;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>验证后端错误消息随请求 <c>Accept-Language</c> 本地化（仅在启用多语言 + Identity 时生成）。</summary>
public sealed class LocalizationTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private static async Task<string?> PostBadLoginAndReadMessageAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/session-login",
            new { UsernameOrEmail = "no-such-user", Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("detail", out var m) ? m.GetString() : null;
    }

    [Fact]
    public async Task Business_error_message_is_english_by_default()
    {
        using var client = factory.CreateProjectClient();
        // 不带 Accept-Language → 默认语言英语
        var message = await PostBadLoginAndReadMessageAsync(client);
        Assert.NotNull(message);
        Assert.Equal("The username or password is incorrect.", message);
    }

    [Fact]
    public async Task Business_error_message_localizes_to_chinese()
    {
        using var client = factory.CreateProjectClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        var message = await PostBadLoginAndReadMessageAsync(client);
        Assert.NotNull(message);
        Assert.Equal("用户名或密码不正确。", message);
    }

    [Fact]
    public async Task Same_endpoint_returns_different_message_per_culture()
    {
        using var en = factory.CreateProjectClient();
        en.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        using var zh = factory.CreateProjectClient();
        zh.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        var enMessage = await PostBadLoginAndReadMessageAsync(en);
        var zhMessage = await PostBadLoginAndReadMessageAsync(zh);

        Assert.NotNull(enMessage);
        Assert.NotNull(zhMessage);
        Assert.NotEqual(enMessage, zhMessage);
    }

    /// <summary>自定义校验（<c>IValidatableObject</c>）的文案与特性文案一样随请求语言本地化，字段名与查询参数同名。</summary>
    [Fact]
    public async Task Custom_validation_message_localizes_to_chinese()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/users?offset=0&limit=10&roles={new string('r', 65)}");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN");

        var response = await admin.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("roles", error.GetProperty("field").GetString());
        Assert.Equal("每个角色名长度不能超过 64 个字符", error.GetProperty("detail").GetString());
    }

    /// <summary>业务校验的<b>具体原因</b>要传到客户端，不能被通用文案盖掉。</summary>
    /// <remarks>
    /// 异常处理器按错误码查词条，未命中时回落到抛出点的安全英文文案。
    /// 这里用"给日志级别写一个非法取值"这条真实场景钉住：400，
    /// 且中文消息里说的是这个取值本身的问题。
    /// <para>
    /// 业务失败的错误码由 <c>BusinessException</c> 构造函数强制；这条用例守的是
    /// 另一半——码到词条这条链真的接上了。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Business_validation_reason_reaches_the_client()
    {
        using var session = await factory.LoginAsync(
            "admin", ProjectWebApplicationFactory.TestAdminPassword);
        session.Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        var response = await session.Client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = "Logging.MinimumLevel", Value = "Chatty" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = body.RootElement.GetProperty("detail").GetString();

        Assert.NotNull(message);
        Assert.DoesNotContain("请求无效", message);
        // 非法取值与可选值都要出现在文案里，否则改不动
        Assert.Contains("Chatty", message);
        Assert.Contains("Information", message);
    }

    private static async Task<string> PostInvalidRegisterAndReadErrorsAsync(HttpClient client)
    {
        // 空用户名 + 非法邮箱 → 触发 [ApiController] 自动 400 校验；经 ConfigureApiValidation
        // 产出与显式业务验证一致的 errors 数组（每项 detail/field），校验消息在 detail 里。
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { Username = "", Email = "not-an-email", Password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // 返回整个 errors 数组文本，便于按语言断言其中的校验文案（本地化后的 detail）
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").GetRawText();
    }

    [Fact]
    public async Task Validation_message_is_english_by_default()
    {
        using var client = factory.CreateProjectClient();
        var errors = await PostInvalidRegisterAndReadErrorsAsync(client);
        // 英文校验句（DataAnnotations ErrorMessage 键即英文默认值）
        Assert.Contains("is required", errors);
    }

    [Fact]
    public async Task Validation_message_localizes_to_chinese()
    {
        using var client = factory.CreateProjectClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        var errors = await PostInvalidRegisterAndReadErrorsAsync(client);
        Assert.Contains("不能为空", errors);
    }

    [Fact]
    public async Task Validation_and_business_error_share_the_same_culture()
    {
        // 关键一致性：同一个 zh-CN 客户端，参数校验消息与业务异常消息都应为中文
        using var zh = factory.CreateProjectClient();
        zh.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN");

        var validationErrors = await PostInvalidRegisterAndReadErrorsAsync(zh);
        var businessMessage = await PostBadLoginAndReadMessageAsync(zh);

        Assert.Contains("不能为空", validationErrors); // DataAnnotations 中文
        Assert.NotNull(businessMessage);
        Assert.Equal("用户名或密码不正确。", businessMessage); // 业务异常中文
    }

#if (IncludeOperationRecords)
    /// <summary>被业务规则拒绝的操作记录，失败原因按读取请求的语言由后端渲染。</summary>
    /// <remarks>
    /// 记录里只存码；同一条记录换 <c>Accept-Language</c> 读，得到的是各自语言的句子，
    /// 与该码的错误响应同一条词条（这里是权限组件随包的译文）。
    /// </remarks>
    [Fact]
    public async Task A_rejected_operation_record_carries_the_failure_reason_in_the_reader_language()
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var roleName = "loc_" + Guid.NewGuid().ToString("N")[..10];
        var created = await session.Client.PostAsJsonAsync(
            "/api/v1/roles",
            new { name = roleName, displayName = roleName, sort = 0, isDefault = false });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var role = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var roleId = role.RootElement.GetProperty("id").GetGuid();

        foreach (var expected in new[] { HttpStatusCode.OK, HttpStatusCode.Conflict })
        {
            var saved = await session.Client.PutAsJsonAsync(
                $"/api/v1/permissions/grants/roles/{roleId}",
                new { expectedVersion = 0, permissionNames = new[] { PermissionConstant.Users.Default } });
            Assert.Equal(expected, saved.StatusCode);
        }

        async Task<(string? Code, string? Message)> ReadFailureAsync(string culture)
        {
            var item = await ReadFailedRecordAsync(
                session.Client, OperationRecordActions.PermissionGrantsReplaced, culture,
                item => item.GetProperty("targetId").GetString() == $"Role/{roleId}");
            return (item.GetProperty("failureCode").GetString(), item.GetProperty("failureMessage").GetString());
        }

        Assert.Equal(
            (PermissionErrorCodes.ConcurrencyConflict, "The permissions were changed by someone else. Reload and try again."),
            await ReadFailureAsync("en-US"));
        Assert.Equal(
            (PermissionErrorCodes.ConcurrencyConflict, "权限已被他人修改，请刷新后重试。"),
            await ReadFailureAsync("zh-CN"));
    }

    /// <summary>业务拒绝留痕带上异常的消息参数，失败原因里是被拒的那个用户名或邮箱。</summary>
    [Fact]
    public async Task A_rejected_duplicate_is_recorded_with_the_value_that_collided()
    {
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var taken = "loc_" + Guid.NewGuid().ToString("N")[..10];
        var takenEmail = $"{taken}@example.test";
        Assert.Equal(HttpStatusCode.OK, (await CreateUserAsync(session.Client, taken, takenEmail)).StatusCode);

        var other = "loc_" + Guid.NewGuid().ToString("N")[..10];
        using var otherCreated = await CreateUserAsync(session.Client, other, $"{other}@example.test");
        using var otherBody = JsonDocument.Parse(await otherCreated.Content.ReadAsStringAsync());
        var otherId = otherBody.RootElement.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Conflict, (await CreateUserAsync(session.Client, taken, $"x{takenEmail}")).StatusCode);
        using var emailClash = await session.Client.PutAsJsonAsync(
            $"/api/v1/users/{otherId}", new { Email = takenEmail, IsEmailVerified = false });
        Assert.Equal(HttpStatusCode.Conflict, emailClash.StatusCode);

        async Task<string?> UsernameReasonAsync(string culture) => (await ReadFailedRecordAsync(
            session.Client, OperationRecordActions.UserCreated, culture,
            item => item.GetProperty("failureData").GetString()?.Contains(taken, StringComparison.Ordinal) == true))
            .GetProperty("failureMessage").GetString();
        async Task<string?> EmailReasonAsync(string culture) => (await ReadFailedRecordAsync(
            session.Client, OperationRecordActions.UserUpdated, culture,
            item => item.GetProperty("targetId").GetString() == otherId))
            .GetProperty("failureMessage").GetString();

        Assert.Equal(
            ($"Username '{taken}' already exists.", $"用户名 '{taken}' 已存在。"),
            (await UsernameReasonAsync("en-US"), await UsernameReasonAsync("zh-CN")));
        Assert.Equal(
            ($"Email '{takenEmail}' is already in use.", $"邮箱 '{takenEmail}' 已被使用。"),
            (await EmailReasonAsync("en-US"), await EmailReasonAsync("zh-CN")));
    }

    /// <summary>登录失败与锁定的记录用审计专用词条（<c>{码}:Record</c>），带上次数与时长。</summary>
    /// <remarks>
    /// 这两条记录的参数（窗口内失败次数、锁定阈值与时长）接口报错里没有，措辞只能另备；
    /// 键名与记录写入的参数逐字一致，填不上会留下 <c>{attempts}</c> 这类原样占位符。
    /// </remarks>
    [Fact]
    public async Task Sign_in_failure_and_lockout_records_carry_the_counts()
    {
        const string password = "LocTests!Passw0rd";
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var username = "loc_" + Guid.NewGuid().ToString("N")[..10];
        Assert.Equal(HttpStatusCode.OK, (await CreateUserAsync(session.Client, username, $"{username}@example.test", password)).StatusCode);

        using var anonymous = factory.CreateProjectClient();
        for (var i = 0; i < SettingConstant.Security.DefaultLockoutMaxFailedAttempts; i++)
        {
            using var failed = await anonymous.PostAsJsonAsync(
                "/api/v1/auth/session-login", new { UsernameOrEmail = username, Password = password + "x" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // 失败登录按阈值落记录：第 1 次必记；第 5 次触发锁定，记的是锁定而不是又一次失败
        async Task<string?> FailedReasonAsync(string culture) => (await ReadFailedRecordAsync(
            session.Client, OperationRecordActions.AuthLoginFailed, culture,
            item => item.GetProperty("targetId").GetString() == username))
            .GetProperty("failureMessage").GetString();
        async Task<string?> LockedReasonAsync(string culture) => (await ReadFailedRecordAsync(
            session.Client, OperationRecordActions.AuthLockedOut, culture,
            item => item.GetProperty("targetName").GetString() == username))
            .GetProperty("failureMessage").GetString();

        Assert.Equal(
            ("Incorrect username or password (failed attempts in the last 5 minutes: 1)", "用户名或密码不正确（5 分钟内已失败 1 次）"),
            (await FailedReasonAsync("en-US"), await FailedReasonAsync("zh-CN")));
        Assert.Equal(
            ("Locked out for 15 minutes after 5 failed sign-in attempts", "连续登录失败 5 次，账号已锁定 15 分钟"),
            (await LockedReasonAsync("en-US"), await LockedReasonAsync("zh-CN")));
    }

#endif

    private static Task<HttpResponseMessage> CreateUserAsync(
        HttpClient client, string username, string email, string password = "LocTests!Passw0rd")
        => client.PostAsJsonAsync("/api/v1/users", new { Username = username, Email = email, Password = password, IsActive = true });

#if (IncludeOperationRecords)
    // 按动作筛失败记录，以指定语言读取，返回唯一匹配的那一条
    private static async Task<JsonElement> ReadFailedRecordAsync(
        HttpClient client, string action, string culture, Predicate<JsonElement> match)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/operation-records?offset=0&limit=100&actions={action}&outcome=failed");
        request.Headers.AcceptLanguage.ParseAdd(culture);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.Single(body.RootElement.GetProperty("items").EnumerateArray(), match).Clone();
    }
#endif
}
