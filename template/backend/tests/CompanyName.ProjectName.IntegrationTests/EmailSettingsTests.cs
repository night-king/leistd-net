#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Email.Smtp.Options;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Leistd.OperationRecords.Errors;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.Settings.EntityFrameworkCore.Entities;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>发信参数由系统设置维护：口令加密落库、只写不读，设置经配置源覆盖配置文件、发信端从 <c>IOptionsMonitor</c> 取值，测试邮件如实报告失败。</summary>
/// <remarks>用例在同一个夹具里顺序执行，结束时清掉覆盖值，回到配置文件里的基线。</remarks>
public sealed class EmailSettingsTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Smtp_password_is_stored_encrypted_and_never_returned()
    {
        using var admin = await LoginAdminAsync();
        await WriteAsync(admin.Client, SettingConstant.Email.SmtpPassword, "smtp-secret-value");
        try
        {
            var password = await ReadSettingAsync(admin.Client, SettingConstant.Email.SmtpPassword);
            Assert.True(password.GetProperty("isSecret").GetBoolean());
            Assert.True(password.GetProperty("hasSecretValue").GetBoolean());
            Assert.False(password.TryGetProperty("tenantValue", out var value) && value.ValueKind == JsonValueKind.String);

            // 库里是密文，不是明文
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var stored = await db.Set<SettingRecord>()
                .IgnoreQueryFilters()
                .Where(r => r.Name == SettingConstant.Email.SmtpPassword)
                .Select(r => r.Value)
                .SingleAsync();
            Assert.DoesNotContain("smtp-secret-value", stored);
        }
        finally
        {
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpPassword, null);
        }
    }

    [Fact]
    public async Task Sender_uses_setting_values_and_falls_back_to_configuration()
    {
        using var admin = await LoginAdminAsync();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<SmtpOptions>>();
        var baselineHost = monitor.CurrentValue.Host;
        var baselineUsername = monitor.CurrentValue.Username;
        await WriteAsync(admin.Client, SettingConstant.Email.SmtpHost, "smtp.runtime.example.test");
        await WriteAsync(admin.Client, SettingConstant.Email.SmtpUsername, "mailer");

        // 账号与口令要成对设置：只设了账号时这一组校验不过，整组不生效、沿用上一组，发信端取值不抛
        Assert.Equal("smtp.runtime.example.test", monitor.CurrentValue.Host);
        Assert.Equal(baselineUsername, monitor.CurrentValue.Username);

        await WriteAsync(admin.Client, SettingConstant.Email.SmtpPassword, "runtime-secret");
        try
        {
            var options = monitor.CurrentValue;

            Assert.Equal("smtp.runtime.example.test", options.Host);
            Assert.Equal("mailer", options.Username);
            // 解密后交给发信端
            Assert.Equal("runtime-secret", options.Password);
            // 没设过的项是配置文件里的基线
            Assert.Equal("noreply@companyname-projectname.local", options.DefaultFromAddress);
            // 默认值是部署基线，不是覆盖后的当前值——否则「重置」回不到配置文件。
            // 覆盖正生效时重新取一次基线，验的是取值跳过了宿主设置配置源，而不是碰巧在它加载之前取的
            var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("smtp.runtime.example.test", configuration[$"{SmtpOptions.SectionName}:{nameof(SmtpOptions.Host)}"]);
            Assert.Equal(baselineHost, (await ReadSettingAsync(admin.Client, SettingConstant.Email.SmtpHost)).GetProperty("defaultValue").GetString());
        }
        finally
        {
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpHost, null);
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpUsername, null);
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpPassword, null);
        }

        // 清除覆盖值即回到配置文件
        Assert.Equal(baselineHost, monitor.CurrentValue.Host);
    }

    [Fact]
    public async Task Failed_test_email_returns_the_reason()
    {
        using var admin = await LoginAdminAsync();
        // 保留给 IANA 的丢弃端口，本机上不会有服务监听
        await WriteAsync(admin.Client, SettingConstant.Email.SmtpHost, "127.0.0.1");
        await WriteAsync(admin.Client, SettingConstant.Email.SmtpPort, "9");
        try
        {
            using var response = await admin.Client.PostAsJsonAsync("/api/v1/settings/email/test", new { To = "someone@example.test" });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(AppSettingErrorCodes.TestEmailFailed, body.RootElement.GetProperty("code").GetString());

            // 授权已通过的业务失败同样留痕，依据是端点上的权限策略
            var failures = await OperationRecordQueries.GetFailuresAsync(
                factory, admin.Client, OperationRecordActions.SettingTestEmailSent, NoTarget);
            Assert.Contains((AppSettingErrorCodes.TestEmailFailed, PermissionConstant.Settings.Email), failures);
            Assert.Equal(0, await OperationRecordQueries.CountSucceededAsync(
                factory, admin.Client, OperationRecordActions.SettingTestEmailSent, NoTarget));
        }
        finally
        {
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpHost, null);
            await WriteAsync(admin.Client, SettingConstant.Email.SmtpPort, null);
        }
    }

    /// <summary>
    /// 没有设置管理权限时由授权管道拒绝：标准 403、不带业务码，请求到不了应用服务；
    /// 被拒的尝试由授权结果处理器补记，且只记一条。
    /// </summary>
    [Fact]
    public async Task Test_email_without_the_settings_permission_is_rejected_by_the_pipeline_and_recorded_once()
    {
        using var admin = await LoginAdminAsync();
        var username = $"mailer_{Guid.NewGuid():N}"[..24];
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password = MemberPassword,
            IsActive = true
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        }

        bool IsDenial((string? FailureCode, string? AuthorizationBasis) failure) =>
            failure == (OperationFailureCodes.Forbidden, PermissionConstant.Settings.Email);
        var deniedBefore = (await OperationRecordQueries.GetFailuresAsync(
            factory, admin.Client, OperationRecordActions.SettingTestEmailSent, NoTarget)).Count(IsDenial);

        using var member = await ProjectWebApplicationFactory.LoginAsync(factory, username, MemberPassword);
        using var response = await member.Client.PostAsJsonAsync("/api/v1/settings/email/test", new { To = "someone@example.test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("\"code\"", await response.Content.ReadAsStringAsync());
        var deniedAfter = (await OperationRecordQueries.GetFailuresAsync(
            factory, admin.Client, OperationRecordActions.SettingTestEmailSent, NoTarget)).Count(IsDenial);
        Assert.Equal(deniedBefore + 1, deniedAfter);
    }
#if (IncludeMultiTenancy)

    /// <summary>邮件发送设置的子权限只在宿主侧：租户管理员即使拿到系统设置入口，也被授权管道拒绝。</summary>
    [Fact]
    public async Task Test_email_from_a_tenant_is_rejected_by_the_host_side_permission()
    {
        using var admin = await LoginAdminAsync();
        var name = $"mail-{Guid.NewGuid():N}"[..16];
        Guid tenantId;
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = TenantAdminPassword
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            tenantId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        using var tenantAdmin = ProjectWebApplicationFactory.CreateProjectClient(factory);
        tenantAdmin.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using (var login = await tenantAdmin.PostAsJsonAsync(
                   "/api/v1/auth/session-login",
                   new { UsernameOrEmail = "admin", Password = TenantAdminPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            tenantAdmin.DefaultRequestHeaders.Add("Cookie", string.Join("; ", login.Headers.GetValues("Set-Cookie")
                .Select(value => value.Split(';', 2)[0])));
        }

        using var response = await tenantAdmin.PostAsJsonAsync("/api/v1/settings/email/test", new { To = "someone@example.test" });

        // 该分组的子权限侧别是 Host：租户上下文下权限检查本身就拒绝，请求到不了用例
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
#endif

    /// <summary>拒绝时日志只记错误码，不记填写的地址：联系方式不进日志。</summary>
    [Fact]
    public async Task Sender_address_must_be_a_bare_mailbox()
    {
        const string address = "Acme <noreply@acme.test>";
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // 宿主用 Serilog 接管了日志工厂，不换回标准工厂，"日志里没有"就恒成立、证伪不了
            services.RemoveAll<ILoggerFactory>();
            services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Warning)
                // 操作记录写日志时，启动期检查要求该类别在 Information 可用
                .AddFilter("Leistd.OperationRecords", LogLevel.Information));
        }));
        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var rejected = await admin.Client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Email.DefaultFromAddress, Value = address });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(AppSettingErrorCodes.EmailAddressInvalid, body.RootElement.GetProperty("code").GetString());
        var logs = host.Services.GetFakeLogCollector().GetSnapshot();
        Assert.Contains(logs, record => record.Message.Contains(AppSettingErrorCodes.EmailAddressInvalid, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, record => record.Message.Contains("noreply@acme.test", StringComparison.Ordinal));
    }

    private const string MemberPassword = "EmailSettingsTests!Pw1";
#if (IncludeMultiTenancy)
    private const string TenantAdminPassword = "Tenant@123456";
#endif

    // 测试邮件不带目标：收件人是任意填写的地址，不进审计表
    private const string NoTarget = "-";

    private Task<AuthenticatedSession> LoginAdminAsync() =>
        ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);

    private static async Task<JsonElement> ReadSettingAsync(HttpClient client, string name)
    {
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        return listed.EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);
    }

    private static async Task WriteAsync(HttpClient client, string name, string? value)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/settings/current-tenant", new { Name = name, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
#endif
