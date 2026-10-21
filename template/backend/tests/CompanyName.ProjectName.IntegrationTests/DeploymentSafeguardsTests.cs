using Microsoft.AspNetCore.Hosting;
#if (SpaFrontend)
using Microsoft.AspNetCore.TestHost;
using CompanyName.ProjectName.Application.Shared;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Options;
#endif
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
#endif
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Api.Options;
#endif
#if (SpaFrontend || RemoteTokenAuth)
using Microsoft.Extensions.Options;
#endif
#if (OpenIddictServer)
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
// 证书目录夹具的属性名就是 Directory，类内引用该类型用别名
using FileSystemDirectory = System.IO.Directory;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>部署安全：开发环境以外，只在单机上成立的回落必须缺配即失败，而不是带着隐患照常启动。</summary>
/// <remarks>
/// 测试宿主跑在 Testing 环境（非开发环境），与预发、生产走同一条校验路径。
/// 每条用例只撤掉一项配置，断言启动失败且错误指明缺的是哪个键。
/// </remarks>
public sealed class DeploymentSafeguardsTests
{
#if (RemoteTokenAuth)
    // 签发方、受众（与浏览器会话的机密客户端）经 Options 启动期校验：组合期不读这些值，
    // 缺失或格式错误在启动时以选项校验失败报出键名，而不是等到第一个请求才在令牌校验器里失败
    [Theory]
    [InlineData("Authentication:Issuer", "", "Authentication:Issuer")]
    [InlineData("Authentication:Issuer", "identity.test/", "Authentication:Issuer")]
    [InlineData("Authentication:Audience", " ", "Authentication:Audience")]
#if (ResourceBrowserSession)
    [InlineData("Authentication:ClientId", "", "Authentication:ClientId")]
    [InlineData("Authentication:ClientSecret", "", "Authentication:ClientSecret")]
#endif
    public void Remote_identity_configuration_is_validated_at_startup(string key, string value, string expected)
    {
        using var factory = new ProjectWebApplicationFactory();

        var exception = StartupFailure(factory, builder => builder.UseSetting(key, value));

        var failure = Assert.IsType<OptionsValidationException>(exception);
        Assert.Equal(typeof(RemoteIdentityOptions), failure.OptionsType);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

#endif
#if (SpaFrontend)
    // 本地密钥目录随容器重建而消失、多副本之间不共享：登录 Cookie、租户连接串、机密设置随之无法解密
    [Fact]
    public void Data_protection_keys_must_be_persisted_outside_development()
    {
        using var factory = new ProjectWebApplicationFactory();

        var exception = StartupFailure(factory, builder => builder.UseSetting("DataProtection:KeysPath", ""));

        Assert.Contains("DataProtection:KeysPath", exception.ToString(), StringComparison.Ordinal);
    }

    // 会话时长不足 1 天时 Cookie 一签发即过期、服务端会话一建即判空闲：启动期校验拒绝，并指明键名
    [Fact]
    public void Session_lifetime_below_one_day_fails_at_startup()
    {
        using var factory = new ProjectWebApplicationFactory();

        var exception = StartupFailure(factory, builder => builder.UseSetting("SessionCookie:ExpireDays", "0"));

        Assert.Contains("SessionCookie:ExpireDays", exception.ToString(), StringComparison.Ordinal);
    }

    // 会话 Cookie 的滑动过期与服务端会话的空闲时限从同一个设置派生：两者不一致时，
    // Cookie 还有效会话却已判过期，或设备列表里留着早已失效的会话
    [Fact]
    public void Cookie_lifetime_and_server_idle_timeout_follow_one_session_setting()
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("SessionCookie:ExpireDays", "3"));

        var cookie = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthenticationSchemeNames.SessionCookie);
        Assert.Equal(TimeSpan.FromDays(3), cookie.ExpireTimeSpan);
        Assert.True(cookie.SlidingExpiration);
#if (LocalIdentity)
        Assert.Equal(TimeSpan.FromDays(3), host.Services.GetRequiredService<IOptions<UserSessionOptions>>().Value.IdleTimeout);
#endif
    }
#endif
#if (OpenIddictServer)

    // 开发证书默认关闭：每台机器各一份，多副本互不认、重建容器后令牌全部失效。未打开时必须给证书文件
    [Fact]
    public void Token_certificates_are_required_unless_development_certificates_are_enabled()
    {
        using var factory = new ProjectWebApplicationFactory();

        var exception = StartupFailure(factory, builder => builder.UseSetting("OAuth:UseDevelopmentCertificates", "false"));

        Assert.Contains("OAuth:SigningCertificates", exception.ToString(), StringComparison.Ordinal);
    }

    // 重叠轮换时集合里每一项都要可用：坏掉的一项若被跳过，JWKS 少一个 kid 或旧令牌解不开，要等请求失败才暴露
    [Theory]
    [InlineData("OAuth:EncryptionCertificates:1:Path", "missing.pfx", "OAuth:EncryptionCertificates:1:Path could not be loaded")]
    public void Every_token_certificate_entry_must_load(string key, string path, string expected)
    {
        using var certificates = new CertificateFiles();
        using var factory = new ProjectWebApplicationFactory();

        var exception = StartupFailure(factory, builder =>
        {
            certificates.Configure(builder);
            builder.UseSetting(key, path.Length == 0 ? "" : Path.Combine(certificates.Directory, path));
        });

        Assert.Contains(expected, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_signing_certificates_are_all_published()
    {
        using var certificates = new CertificateFiles();
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(certificates.Configure);
        using var client = ProjectWebApplicationFactory.CreateProjectClient(host);
        client.BaseAddress = new Uri("https://localhost");

        using var keys = JsonDocument.Parse(await client.GetStringAsync("/.well-known/jwks"));

        var published = keys.RootElement.GetProperty("keys").EnumerateArray()
            .Where(key => key.GetProperty("use").GetString() == "sig").Select(key => key.GetProperty("kid").GetString()).ToHashSet();
        Assert.Equal(2, published.Count);
    }

    // 两张签名证书（当前一张、下一张）与两张加密证书，写成临时 PKCS#12 文件
    private sealed class CertificateFiles : IDisposable
    {
        public string Directory { get; } = FileSystemDirectory.CreateTempSubdirectory("token-certificates-").FullName;

        public CertificateFiles()
        {
            foreach (var name in new[] { "signing-0", "signing-1", "encryption-0", "encryption-1" })
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                File.WriteAllBytes(Path.Combine(Directory, name + ".pfx"), certificate.Export(X509ContentType.Pkcs12, "test-password"));
            }
        }

        public void Configure(IWebHostBuilder builder)
        {
            builder.UseSetting("OAuth:UseDevelopmentCertificates", "false");
            foreach (var (section, prefix) in new[] { ("SigningCertificates", "signing"), ("EncryptionCertificates", "encryption") })
                for (var index = 0; index < 2; index++)
                {
                    builder.UseSetting($"OAuth:{section}:{index}:Path", Path.Combine(Directory, $"{prefix}-{index}.pfx"));
                    builder.UseSetting($"OAuth:{section}:{index}:Password", "test-password");
                }
        }

        public void Dispose() => FileSystemDirectory.Delete(Directory, recursive: true);
    }
#endif

#if (SpaFrontend)
    // 缺 Redis 不阻止启动（单实例合法），但非开发环境必须在启动日志里留下降级说明
    [Theory]
    [InlineData("Testing", true)]
    [InlineData("Development", false)]
    public void A_missing_redis_connection_is_reported_at_startup_outside_development(string environment, bool expected)
    {
        using var factory = new ProjectWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment(environment)
            .UseSetting("ConnectionStrings:Redis", "")
            .ConfigureTestServices(services =>
            {
                // 宿主用 Serilog 接管了日志工厂，换回标准工厂才收得到（只影响这个派生宿主）
                services.RemoveAll<ILoggerFactory>();
                services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Warning)
                // 操作记录写日志时，启动期检查要求该类别在 Information 可用
                .AddFilter("Leistd.OperationRecords", LogLevel.Information));
            }));

        using var client = host.CreateClient();

        Assert.Equal(expected, host.Services.GetFakeLogCollector().GetSnapshot()
            .Any(record => record.Message.Contains("ConnectionStrings:Redis is not configured", StringComparison.Ordinal)));
    }
#endif

    private static Exception StartupFailure(ProjectWebApplicationFactory factory, Action<IWebHostBuilder> configure)
    {
        using var host = factory.WithWebHostBuilder(configure);
        return Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }
}
