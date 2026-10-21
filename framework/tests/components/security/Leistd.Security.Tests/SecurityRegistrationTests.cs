using Leistd.AmbientContext;
using Leistd.Security.AspNetCore;
using Leistd.Security.AspNetCore.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Clients;
using Leistd.Security.Users;
using Leistd.TestBase.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Security.Tests;

/// <summary><c>AddAmbientContext</c> / <c>AddSecurity</c> 的注册面：生命周期、幂等与宿主替换。</summary>
/// <remarks>
/// 主体访问器是 Singleton（状态在 AsyncLocal 里），读主体的服务是 Transient：
/// 若被改成 Scoped，后台作业在根作用域解析时会被作用域校验拒绝，或在不校验时捕获一份过期主体。
/// </remarks>
public sealed class SecurityRegistrationTests
{
    [Fact]
    public void Ambient_context_registration_uses_the_documented_lifetimes()
    {
        var services = new ServiceCollection();

        services.AddAmbientContext();

        services.AssertSingle<ICurrentPrincipalAccessor>(ServiceLifetime.Singleton);
        services.AssertSingle<ICurrentUser>(ServiceLifetime.Transient);
        services.AssertSingle<ICurrentClient>(ServiceLifetime.Transient);
        services.AssertSingle<IAmbientContext>(ServiceLifetime.Transient);
    }

    [Fact]
    public void Ambient_context_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddAmbientContext());

    [Fact]
    public void Security_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddSecurity());

    [Fact]
    public void Security_registration_brings_the_http_context_accessor()
    {
        var services = new ServiceCollection();

        services.AddSecurity();

        services.AssertSingle<IHttpContextAccessor>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<ICurrentPrincipalAccessor, HttpContextCurrentPrincipalAccessor>();
    }

    // ICurrentUser 是替换口：宿主先注册的实现不被组件默认值盖掉，AddSecurity 也只换主体来源
    [Fact]
    public void Host_registered_current_user_is_kept()
    {
        var services = new ServiceCollection();
        services.AddTransient<ICurrentUser>(_ => throw new NotSupportedException());

        services.AddAmbientContext();
        services.AddSecurity();

        Assert.NotNull(services.AssertSingle<ICurrentUser>(ServiceLifetime.Transient).ImplementationFactory);
    }

    // AssertIdempotent 不计验证器：每次调用各登记一份时，配错的 claim 类型会按调用次数重复报错
    [Fact]
    public void Repeated_ambient_context_registration_reports_each_claim_type_failure_once()
    {
        var services = new ServiceCollection();
        services.AddAmbientContext().AddAmbientContext();
        services.Configure<ClaimTypeOptions>(options =>
        {
            options.UserIds = [];
            options.TenantId = "";
        });

        using var provider = services.BuildServiceProvider();
        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ClaimTypeOptions>>().Value);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<ClaimTypeOptions>));
        Assert.Equal(2, failure.Failures.Count());
        Assert.Contains(failure.Failures, message => message.StartsWith("ClaimTypeOptions.UserIds", StringComparison.Ordinal));
        Assert.Contains(failure.Failures, message => message.StartsWith("ClaimTypeOptions.TenantId", StringComparison.Ordinal));
    }
}
