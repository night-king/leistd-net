#if (ExternalLogin)
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AspNet.Security.OAuth.GitHub;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Application.Shared;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>协议用例只替换 Backchannel，保持真实 OAuth 处理器和关联 Cookie。</summary>
internal sealed class ExternalOAuthBackchannel : HttpMessageHandler
{
    public ExternalUserInfo User { get; set; } = new() { ProviderId = "provider-id", ProviderAccountLabel = "provider-user", SuggestedUsername = "provider-user" };
    public string? CodeVerifier { get; private set; }
    /// <summary>最近一次换 token 时提交的客户端凭据：验证运行期换了凭据后，处理器确实用的是新值。</summary>
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }
    public bool EmailsUnavailable { get; set; }
    /// <summary>GitHub 资料里不公开邮箱（返回 null），只能经 /user/emails 取得。</summary>
    public bool PublicEmailHidden { get; set; }
    /// <summary>GitHub 的主邮箱与公开邮箱不同时使用；为空则与 <see cref="ExternalUserInfo.Email"/> 相同。</summary>
    public string? PrimaryEmail { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("token", StringComparison.Ordinal))
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            CodeVerifier = form["code_verifier"].ToString();
            ClientId = form["client_id"].ToString();
            ClientSecret = form["client_secret"].ToString();
            return new HttpResponseMessage(string.IsNullOrEmpty(CodeVerifier) ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(string.IsNullOrEmpty(CodeVerifier)
                    ? new { error = "invalid_grant" } as object
                    : new { access_token = "provider-access-token", token_type = "Bearer", expires_in = 300 })
            };
        }
        if (request.RequestUri.AbsolutePath.EndsWith("emails", StringComparison.Ordinal))
            return new HttpResponseMessage(EmailsUnavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { new { email = PrimaryEmail ?? User.Email, primary = true, verified = User.EmailVerified } })
            };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = User.ProviderId, login = User.SuggestedUsername ?? User.ProviderAccountLabel,
                email = PublicEmailHidden ? null : User.Email, name = User.DisplayName, avatar_url = User.AvatarUrl,
                sub = User.ProviderId, email_verified = User.EmailVerified, picture = User.AvatarUrl
            })
        };
    }

    /// <param name="deployed">两个提供方是否由部署配置启用；为 false 时只装好协议替身，开关与凭据由用例经系统设置写入。</param>
    public WebApplicationFactory<Program> CreateHost(ProjectWebApplicationFactory factory, bool businessData = false, bool deployed = true) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var provider in deployed ? new[] { "Github", "Google" } : [])
            {
                builder.UseSetting($"ExternalAuth:{provider}:Enabled", "true");
                builder.UseSetting($"ExternalAuth:{provider}:ClientId", "protocol-test-client");
                builder.UseSetting($"ExternalAuth:{provider}:ClientSecret", "protocol-test-secret");
            }
            builder.ConfigureTestServices(services =>
            {
                services.Configure<GitHubAuthenticationOptions>(AuthenticationSchemeNames.ExternalProviderPrefix + "github", Configure);
                services.Configure<GoogleOptions>(AuthenticationSchemeNames.ExternalProviderPrefix + "google", Configure);
                void Configure(OAuthOptions options)
                {
                        options.BackchannelHttpHandler = this;
                        if (businessData)
                            options.Events.OnCreatingTicket = context =>
                            {
                                // 业务用例直接给规范化资料，协议字段映射另由官方处理器用例验证。
                                context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, User.ProviderId));
                                context.Properties.Items["external.user"] = JsonSerializer.Serialize(User);
                                return Task.CompletedTask;
                            };
                }
            });
        });

    public static string Cookies(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values)
        ? string.Join("; ", values.Where(value => !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Split(';', 2)[0])) : "";

    public static async Task<(string State, string Cookie)> StartAsync(HttpClient client, string provider = "github", string intent = "login", string? returnUrl = null)
    {
        var path = intent == "link" ? "link/challenge" : "challenge";
        using var challenge = await client.GetAsync($"/api/v1/external-auth/{provider}/{path}" +
            (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var parameters = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("S256", parameters["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(parameters["code_challenge"]));
        var state = parameters["state"].ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/external-auth/{provider}/signin?code={Guid.NewGuid():N}&state={Uri.EscapeDataString(state)}");
        var original = client.DefaultRequestHeaders.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : "";
        request.Headers.Add("Cookie", string.Join("; ", new[] { original, Cookies(challenge) }.Where(value => value.Length > 0)));
        using var callback = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.DoesNotContain("code=", callback.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("twoFactorToken", callback.Headers.Location.ToString(), StringComparison.OrdinalIgnoreCase);
        return (state, Cookies(callback));
    }
}
#endif
