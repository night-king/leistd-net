#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Auth.Errors;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>
/// 租户可以关闭自助注册：注册与注册验证码都被拒（带码的 403），登录页读到的注册摘要随之变化。
/// 界面隐藏入口不代替这里的拒绝——先打开注册页、之后才关闭，提交照样被挡住。
/// </summary>
public sealed class RegistrationSwitchTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task Closing_self_sign_up_rejects_registration_and_tells_the_sign_in_page()
    {
        using var anonymous = ProjectWebApplicationFactory.CreateProjectClient(factory);
        Assert.True(await RegistrationEnabledAsync(anonymous));

        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        await SetAsync(admin.Client, "false");
        try
        {
            Assert.False(await RegistrationEnabledAsync(anonymous));

            using var register = await anonymous.PostAsJsonAsync("/api/v1/auth/register", new
            {
                Username = "closed_sign_up",
                Email = "closed@example.test",
                Password = "ClosedSignUp!Pw1",
                CaptchaToken = "any",
                CaptchaCode = "any"
            });
            await AssertRejectedAsync(register);
#if (Email)

            using var sendCode = await anonymous.PostAsJsonAsync("/api/v1/auth/send-email-code", new
            {
                Email = "closed@example.test",
                CaptchaToken = "any",
                CaptchaCode = "any"
            });
            await AssertRejectedAsync(sendCode);
#endif
        }
        finally
        {
            await SetAsync(admin.Client, null);
        }

        Assert.True(await RegistrationEnabledAsync(anonymous));
    }

#if (IncludeMultiTenancy)
    // 开关按租户：宿主关闭不影响租户，匿名访客按租户提示读到的是该租户的那一份
    [Fact]
    public async Task Closing_self_sign_up_on_the_host_leaves_tenants_open()
    {
        using var admin = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var name = $"reg-{Guid.NewGuid():N}"[..16];
        Guid tenantId;
        using (var create = await admin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = name,
            DisplayName = name,
            AdminEmail = $"admin@{name}.example.test",
            AdminPassword = "Tenant@123456"
        }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            tenantId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        using var tenantVisitor = ProjectWebApplicationFactory.CreateProjectClient(factory);
        tenantVisitor.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
        using var hostVisitor = ProjectWebApplicationFactory.CreateProjectClient(factory);

        await SetAsync(admin.Client, "false");
        try
        {
            Assert.False(await RegistrationEnabledAsync(hostVisitor));
            Assert.True(await RegistrationEnabledAsync(tenantVisitor));
        }
        finally
        {
            await SetAsync(admin.Client, null);
        }
    }

#endif
    private static async Task<bool> RegistrationEnabledAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/security-config"))
            .GetProperty("registrationEnabled").GetBoolean();

    private static async Task AssertRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AuthErrorCodes.RegistrationDisabled, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task SetAsync(HttpClient client, string? value)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings/current-tenant",
            new { Name = SettingConstant.Registration.Enabled, Value = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
#endif
