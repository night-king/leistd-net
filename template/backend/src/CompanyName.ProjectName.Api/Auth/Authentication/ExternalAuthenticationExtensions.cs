#if (ExternalLogin)
using Leistd.Security.AspNetCore;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AspNet.Security.OAuth.GitHub;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Infrastructure.Auth.OAuth.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Auth.Authentication;

internal static class ExternalAuthenticationExtensions
{
    internal const string UserInfoKey = "external.user";

    /// <summary>提供方的回调路径。认证注册与系统设置的状态端点共用：后者要给管理员显示回调地址，
    /// 不能为此去取（可能不可用的）OAuth 命名选项——一取就会触发官方的必填校验。</summary>
    internal static string CallbackPath(string provider) => $"/api/v1/external-auth/{provider}/signin";

    public static void AddExternalAuthentication(this IServiceCollection services)
    {
        var authentication = services.AddAuthentication().AddCookie(AuthenticationSchemeNames.ExternalCookie, cookie =>
        {
            cookie.Cookie.Name = "__Host-CompanyName.ProjectName.External";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.IsEssential = true;
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            cookie.SlidingExpiration = false;
        });
        services.AddDistributedTicketStore(AuthenticationSchemeNames.ExternalCookie);
        services.AddOptions<CookieAuthenticationOptions>(AuthenticationSchemeNames.ExternalCookie)
            .Configure<IOptions<SessionCookieOptions>>((cookie, session) =>
            {
                cookie.Cookie.SameSite = session.Value.SameSite ?? SameSiteMode.Lax;
            });
        // 两个提供方始终登记（处理器、官方 PostConfigure 与选项校验都由官方扩展装好），是否对外提供由
        // ExternalSchemeSynchronizer 按开关与凭据增删方案；凭据每次重建选项时从监视器读，运行期改了即生效
        var google = AuthenticationSchemeNames.ExternalProviderPrefix + ExternalSchemeSynchronizer.Google;
        authentication.AddGoogle(google, options =>
        {
            options.SignInScheme = AuthenticationSchemeNames.ExternalCookie;
            options.Events.OnRemoteFailure = RejectRemoteFailureAsync;
            options.Events.OnAccessDenied = ReturnAccessDeniedAsync;
            options.CallbackPath = CallbackPath(ExternalSchemeSynchronizer.Google);
            options.Events.OnCreatingTicket = context =>
            {
                var user = context.User;
                var id = Text(user, "sub") ?? throw new InvalidOperationException("Google user-info contains no sub.");
                context.Properties.Items[UserInfoKey] = JsonSerializer.Serialize(new ExternalUserInfo
                {
                    ProviderId = id, ProviderAccountLabel = Text(user, "email") ?? id,
                    Email = Text(user, "email"), EmailVerified = Flag(user, "email_verified"),
                    DisplayName = Text(user, "name"), AvatarUrl = Text(user, "picture"),
                    ProviderDisplayName = context.Scheme.DisplayName
                });
                return Task.CompletedTask;
            };
        });
        services.AddOptions<GoogleOptions>(google)
            .Configure<IOptionsMonitor<ExternalAuthOptions>>((options, monitor) => UseCredentials(options, monitor.CurrentValue.Google));
        var github = AuthenticationSchemeNames.ExternalProviderPrefix + ExternalSchemeSynchronizer.Github;
        authentication.AddGitHub(github, options =>
        {
            options.SignInScheme = AuthenticationSchemeNames.ExternalCookie;
            options.Events.OnRemoteFailure = RejectRemoteFailureAsync;
            options.Events.OnAccessDenied = ReturnAccessDeniedAsync;
            options.CallbackPath = CallbackPath(ExternalSchemeSynchronizer.Github);
            options.UsePkce = true;
            options.Scope.Add("user:email");
            // 包内的邮箱补取只给地址、不给 verified，且失败即中断登录；改由 CreatingTicket 自取并降级。
            options.UserEmailsEndpoint = string.Empty;
            options.Events.OnCreatingTicket = CreateGitHubTicketAsync;
        });
        services.AddOptions<GitHubAuthenticationOptions>(github)
            .Configure<IOptionsMonitor<ExternalAuthOptions>>((options, monitor) => UseCredentials(options, monitor.CurrentValue.Github));
        services.AddSingleton<ExternalSchemeSynchronizer>();
        services.AddHostedService(provider => provider.GetRequiredService<ExternalSchemeSynchronizer>());
    }

    // 只有可用（凭据齐全）的方案才会被登记、才会取到这里的选项，因此空值不会进入官方的必填校验
    private static void UseCredentials(OAuthOptions options, ExternalAuthOptions.ProviderOptions provider)
    {
        options.ClientId = provider.ClientId ?? string.Empty;
        options.ClientSecret = provider.ClientSecret ?? string.Empty;
    }

    // 回调是浏览器整页导航：失败时回到前端回调页并给出原因码，不签发外部票据、不把问题详情 JSON 直接呈现给用户。
    private static Task RejectRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        context.Response.Redirect(FailureRedirect(context.Scheme.Name, context.Properties, "failed"));
        return Task.CompletedTask;
    }

    // 用户在提供商授权页取消（error=access_denied）由官方 AccessDenied 事件接管，不再作为协议失败处理。
    private static Task ReturnAccessDeniedAsync(AccessDeniedContext context)
    {
        context.HandleResponse();
        context.Response.Redirect(FailureRedirect(context.Scheme.Name, context.Properties, "cancelled"));
        return Task.CompletedTask;
    }

    private static string FailureRedirect(string scheme, AuthenticationProperties? properties, string reason)
    {
        // state 无法解开时没有受保护的意图，按登录处理；provider 取自已登记的 scheme 名而不是请求参数。
        var intent = properties?.Items.TryGetValue("external.intent", out var value) == true && value == "link" ? "link" : "login";
        var provider = scheme[AuthenticationSchemeNames.ExternalProviderPrefix.Length..];
        return $"/auth/external-callback/{provider}?intent={intent}&error={reason}";
    }

    private static async Task CreateGitHubTicketAsync(OAuthCreatingTicketContext context)
    {
        // 资料与 NameIdentifier 已由处理器取回并映射；这里只补"主邮箱是否已验证"。
        var user = context.User;
        string? email = Text(user, "email");
        var verified = false;
        try
        {
            using var response = await GetGitHubAsync(context, GitHubAuthenticationDefaults.UserEmailsEndpoint);
            if (response.IsSuccessStatusCode)
            {
                using var emails = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                var primary = emails.RootElement.EnumerateArray().FirstOrDefault(item => Flag(item, "primary"));
                if (primary.ValueKind == JsonValueKind.Object)
                {
                    email = Text(primary, "email");
                    verified = Flag(primary, "verified");
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 邮箱资料不可用时不按邮箱关联；已有外部账号仍可登录。
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ExternalAuthentication").LogWarning(exception, "GitHub email verification is unavailable.");
        }
        var id = context.Identity!.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("GitHub user profile contains no id.");
        var login = Text(user, "login") ?? id;
        context.Properties.Items[UserInfoKey] = JsonSerializer.Serialize(new ExternalUserInfo
        {
            ProviderId = id, ProviderAccountLabel = login, SuggestedUsername = login,
            Email = email, EmailVerified = verified, DisplayName = Text(user, "name"), AvatarUrl = Text(user, "avatar_url"),
            ProviderDisplayName = context.Scheme.DisplayName
        });
    }

    private static async Task<HttpResponseMessage> GetGitHubAsync(OAuthCreatingTicketContext context, string uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.UserAgent.ParseAdd("CompanyName.ProjectName");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
    }

    private static string? Text(JsonElement user, string name) => user.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement user, string name) => user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
#endif
