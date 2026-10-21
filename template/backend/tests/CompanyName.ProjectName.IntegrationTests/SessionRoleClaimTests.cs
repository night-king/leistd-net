#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Shared;
#if (ExternalLogin)
using System.Security.Claims;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Domain.Repositories;
#endif
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>会话 Cookie 里的角色能被官方 <c>IsInRole</c> / <c>RequireRole</c> 认出。</summary>
/// <remarks>
/// 角色写成 <c>role</c> claim，身份的 RoleClaimType 必须同名；否则官方判定按默认的
/// <c>ClaimTypes.Role</c> 去找，静默判为不在角色中。探针走真实链路：登录发出 Cookie，
/// 再按会话方案认证（票据反序列化）后判定，而不是检查登录时手里的那个对象。
/// </remarks>
public sealed class SessionRoleClaimTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string ProbePath = "/__role-probe";

    [Fact]
    public async Task Official_role_checks_recognise_the_roles_in_the_session_cookie()
    {
        var suffix = Guid.CreateVersion7().ToString("N")[..8];
        var roleName = $"auditor_{suffix}";
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var roleId = await CreateRoleAsync(superAdmin.Client, roleName);
        var user = await CreateUserAsync(superAdmin.Client, [roleId]);

        using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RoleProbe())));
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, user.Username, TestPassword);

        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"{ProbePath}?role={roleName}")).StatusCode);
        // 反向：没有的角色必须判否，证明探针不是恒为真
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.GetAsync($"{ProbePath}?role=other_{suffix}")).StatusCode);
    }

    /// <summary>注册、首次外部登录、管理员不指定角色建用户，三条路径都得到默认角色，签发的会话带对应角色声明。</summary>
    /// <remarks>
    /// 默认角色由本用例新建并标为默认：只看种子里的默认角色的话，"没分配"与"恰好断言了别的角色"分不开。
    /// 反向：非默认角色不得出现在任何一条路径上。
    /// </remarks>
    [Fact]
    public async Task Every_new_user_path_assigns_the_default_roles_and_the_session_carries_them()
    {
        var suffix = Guid.CreateVersion7().ToString("N")[..8];
        var defaultRole = $"dflt_{suffix}";
        var otherRole = $"other_{suffix}";
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        await CreateRoleAsync(superAdmin.Client, defaultRole, isDefault: true);
        await CreateRoleAsync(superAdmin.Client, otherRole);

        using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("UserRegistration:EnableEmailVerification", "false");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new RoleProbe());
                services.RemoveAll<ICaptchaVerifier>();
                services.AddTransient<ICaptchaVerifier, AcceptingCaptcha>();
            });
        });

        // 自助注册
        var registeredName = $"reg_{suffix}";
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var registered = await scope.ServiceProvider.GetRequiredService<IAuthAppService>().RegisterAsync(new RegisterInputDto
            {
                Username = registeredName,
                Email = $"{registeredName}@example.test",
                Password = TestPassword,
                CaptchaToken = "accepted",
                CaptchaCode = "accepted"
            });
            Assert.Contains(defaultRole, registered.Roles);
            Assert.DoesNotContain(otherRole, registered.Roles);
        }

        await AssertSessionRolesAsync(host, registeredName, defaultRole, otherRole);

        // 管理员建用户、不指定角色
        var created = await CreateUserAsync(superAdmin.Client);
        Assert.Contains(defaultRole, created.Roles.Select(role => role.Name));
        Assert.DoesNotContain(otherRole, created.Roles.Select(role => role.Name));
        await AssertSessionRolesAsync(host, created.Username, defaultRole, otherRole);
#if (ExternalLogin)

        // 首次外部登录：关联行与新用户同在未提交的边界内，主体上的角色只能来自本次分配
        var external = new ExternalUserInfo
        {
            ProviderId = $"ext-{suffix}",
            ProviderAccountLabel = $"ext-{suffix}",
            SuggestedUsername = $"ext_{suffix}"
        };
        Guid externalUserId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IExternalAuthAppService>()
                .AuthenticateExternalUserAsync("github", external);
            var principal = Assert.IsType<ClaimsPrincipal>(result.Principal);
            Assert.True(principal.IsInRole(defaultRole));
            Assert.False(principal.IsInRole(otherRole));
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            externalUserId = (await users.GetListAsync(user => user.Username == external.SuggestedUsername)).Single().Id;
        }

        // 再次外部登录不再分配：关联行数不变
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var again = await scope.ServiceProvider.GetRequiredService<IExternalAuthAppService>()
                .AuthenticateExternalUserAsync("github", external);
            Assert.True(again.Principal!.IsInRole(defaultRole));
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var defaults = await scope.ServiceProvider.GetRequiredService<IRepository<Role, Guid>>()
                .CountAsync(role => role.IsDefault);
            var assigned = (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
                .GetWithRolesAsync(externalUserId))!.Roles.Count;
            Assert.Equal(defaults, assigned);
        }
#endif
    }

    private static async Task AssertSessionRolesAsync(
        WebApplicationFactory<Program> host,
        string username,
        string expectedRole,
        string absentRole)
    {
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"{ProbePath}?role={expectedRole}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.GetAsync($"{ProbePath}?role={absentRole}")).StatusCode);
    }

    private static async Task<Guid> CreateRoleAsync(HttpClient client, string name, bool isDefault = false)
    {
        var response = await client.PostAsJsonAsync("/api/v1/roles", new
        {
            name,
            displayName = name,
            sort = 0,
            isDefault
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private sealed class AcceptingCaptcha : ICaptchaVerifier
    {
        public Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    // 挂在管道最前：自己按会话方案认证，不依赖后面的认证中间件是否已跑
    private sealed class RoleProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(ProbePath, probe => probe.Run(async context =>
            {
                var role = context.Request.Query["role"].ToString();
                var result = await context.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
                var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
                var policy = new AuthorizationPolicyBuilder().RequireRole(role).Build();

                var granted = result.Principal is { } principal
                    && principal.IsInRole(role)
                    && (await authorization.AuthorizeAsync(principal, policy)).Succeeded;
                context.Response.StatusCode = granted ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden;
            }));
            next(app);
        };
    }
}
#endif
