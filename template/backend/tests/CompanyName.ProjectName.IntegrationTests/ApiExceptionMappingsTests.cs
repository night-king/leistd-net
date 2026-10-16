using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Domain.Users.Errors;
#if (LocalIdentity)
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Tenants.Errors;
#endif
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.OpenApplications.Errors;
#endif
using Leistd.Authorization.Errors;
using Leistd.ExceptionHandling.Options;
using Leistd.MultiTenancy.Errors;
using Leistd.Settings.Errors;
#if (IncludeNotifications)
using Leistd.Notifications.Errors;
#endif
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CompanyName.ProjectName.Api.Hosting;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.ServiceClient;
using Leistd.ServiceClient.Exceptions;
using Leistd.ServiceClient.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Linq.Dynamic.Core.Exceptions;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>钉住业务错误码到 HTTP 状态的契约。</summary>
/// <remarks>
/// 读的是<b>组合之后</b>的选项，不是单独调用 <c>ApiExceptionMappings.Configure</c> 的结果：
/// 框架组件的默认状态由各组件在自己的 <c>AddXxx</c> 里登记，只测本项目那一份会漏掉它们，
/// 而漏掉的后果恰恰是静默的——未命中的业务码回落成 400。
/// </remarks>
public sealed class ApiExceptionMappingsTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private GlobalExceptionOptions ComposedOptions()
        => factory.Services.GetRequiredService<IOptions<GlobalExceptionOptions>>().Value;

    [Fact]
    public async Task Parse_failures_outside_sorting_are_not_labeled_as_sorting_validation()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services => services.AddGlobalExceptionHandler(ApiExceptionMappings.Configure))
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new ParseException("private configuration expression", 0));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(StatusCodes.Status500InternalServerError, (int)response.StatusCode);
        Assert.DoesNotContain("sorting", body);
        Assert.DoesNotContain("private configuration expression", body);
    }

    [Theory]
#if (LocalIdentity)
    [InlineData(AuthErrorCodes.InvalidCredentials, StatusCodes.Status401Unauthorized)]
    [InlineData(AuthErrorCodes.TwoFactorRequiredByPolicy, StatusCodes.Status403Forbidden)]
#endif
    [InlineData(SettingErrorCodes.IdentityCannotOperate, StatusCodes.Status403Forbidden)]
#if (IncludeNotifications)
    [InlineData(NotificationErrorCodes.IdentityCannotOperate, StatusCodes.Status403Forbidden)]
#endif
    [InlineData(PermissionErrorCodes.SubjectNotFound, StatusCodes.Status404NotFound)]
#if (IncludeMultiTenancy)
    [InlineData(MultiTenancyErrorCodes.NotFound, StatusCodes.Status404NotFound)]
#endif
#if (LocalIdentity)
    [InlineData(AuthErrorCodes.CannotRevokeCurrentSession, StatusCodes.Status409Conflict)]
#endif
    [InlineData(UserErrorCodes.EmailTaken, StatusCodes.Status409Conflict)]
#if (ExternalLogin)
    [InlineData(ExternalAuthErrorCodes.UsernameAllocationFailed, StatusCodes.Status409Conflict)]
    [InlineData(SecurityErrorCodes.PasswordAlreadySet, StatusCodes.Status409Conflict)]
#endif
#if (IncludeMultiTenancy)
    [InlineData(MultiTenancyErrorCodes.ConnectionChangeRequiresInactiveTenant, StatusCodes.Status409Conflict)]
#endif
#if (Email)
    [InlineData(AppSettingErrorCodes.EmailVerificationKeyMissing, StatusCodes.Status409Conflict)]
    [InlineData(AuthErrorCodes.EmailCodeSendTooFrequent, StatusCodes.Status429TooManyRequests)]
    [InlineData(AuthErrorCodes.EmailVerificationUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(AppSettingErrorCodes.TestEmailFailed, StatusCodes.Status503ServiceUnavailable)]
#endif
    public void Stable_error_codes_keep_their_declared_http_semantics(string code, int expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, ComposedOptions().CodeStatusMappings[code]);
    }

    [Fact]
    public void Explicit_mappings_only_cover_real_codes_with_non_default_statuses()
    {
        var options = ComposedOptions();

        var codeTypes = new[]
        {
            typeof(RoleErrorCodes),
            typeof(AppSettingErrorCodes), typeof(UserErrorCodes),
#if (LocalIdentity)
            typeof(AuthErrorCodes), typeof(SecurityErrorCodes),
#if (IncludeMultiTenancy)
            typeof(TenantErrorCodes),
#endif
#endif
#if (ExternalLogin)
            typeof(ExternalAuthErrorCodes),
#endif
#if (OpenIddictServer)
            typeof(OpenAppErrorCodes),
#endif
            typeof(PermissionErrorCodes), typeof(MultiTenancyErrorCodes), typeof(SettingErrorCodes),
#if (IncludeNotifications)
            typeof(NotificationErrorCodes),
#endif
        };
        var knownCodes = codeTypes
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(options.CodeStatusMappings, mapping =>
        {
            Assert.Contains(mapping.Key, knownCodes);
            Assert.NotEqual(StatusCodes.Status400BadRequest, mapping.Value);
        });
    }

    [Theory]
    [InlineData(ServiceClientFailureKind.Configuration, 500)]
    [InlineData(ServiceClientFailureKind.Unknown, 500)]
    [InlineData(ServiceClientFailureKind.InvalidResponse, 502)]
    [InlineData(ServiceClientFailureKind.RemoteFailure, 502)]
    [InlineData(ServiceClientFailureKind.Unavailable, 503)]
    [InlineData(ServiceClientFailureKind.Timeout, 504)]
    public async Task Service_client_failures_keep_diagnostics_private(
        ServiceClientFailureKind failureKind, int expectedStatus)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    // 上游故障的安全默认状态由组件在注册客户端时登记，所以这里要真的注册一个客户端；
                    // 只调 ApiExceptionMappings.Configure 测到的是本项目那一份，不是应用实际组合出来的
                    services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");
                    services.AddGlobalExceptionHandler(ApiExceptionMappings.Configure);
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new ServiceClientException(
                        "private upstream URL and response body", failureKind: failureKind));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        // 上游故障只有状态码语义：不合成错误码
        Assert.DoesNotContain("\"code\"", body);
        Assert.DoesNotContain("private upstream URL", body);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Remote_http_failures_use_safe_component_default_without_exposing_remote_details(int remoteStatus)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    // 上游故障的安全默认状态由组件在注册客户端时登记，所以这里要真的注册一个客户端；
                    // 只调 ApiExceptionMappings.Configure 测到的是本项目那一份，不是应用实际组合出来的
                    services.AddHttpClient("upstream").AddServiceClientPipeline<ServiceClientOptions>("upstream");
                    services.AddGlobalExceptionHandler(ApiExceptionMappings.Configure);
                })
                .Configure(app =>
                {
                    app.UseGlobalExceptionHandler();
                    app.Run(_ => throw new RemoteServiceException(
                        "private upstream URL and response body", remoteStatus,
                        errorCode: "Remote:Private", responseBody: "private payload"));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(StatusCodes.Status502BadGateway, (int)response.StatusCode);
        Assert.DoesNotContain("\"code\"", body);
        Assert.DoesNotContain("private upstream URL", body);
        Assert.DoesNotContain("Remote:Private", body);
        Assert.DoesNotContain("private payload", body);
    }
}
