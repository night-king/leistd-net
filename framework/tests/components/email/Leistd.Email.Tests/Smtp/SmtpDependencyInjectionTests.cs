using Leistd.Email.Abstractions;
using Leistd.Email.Smtp;
using Leistd.Email.Smtp.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Email.Tests.Smtp;

/// <summary>SMTP 发送器的注册面。</summary>
public sealed class SmtpDependencyInjectionTests
{
    [Fact]
    public void Registration_resolves_the_smtp_sender()
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(EmptyConfiguration);

        services.AddSmtpEmailSender(o =>
        {
            o.Host = "127.0.0.1";
            o.DefaultFromAddress = "noreply@example.com";
        });

        services.AssertResolvesTo<IEmailSender, SmtpEmailSender>();
    }

    // 实现类型注册一次、接口作别名转发：两次注册产生两个 SmtpEmailSender 时，
    // 每份各自持有自己的 SmtpClient 与日志，行为差异只在压测下才显形
    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services =>
        {
            services.AddLogging();
            services.AddSmtpEmailSender(o =>
            {
                o.Host = "127.0.0.1";
                o.DefaultFromAddress = "noreply@example.com";
            });
        });

    [Fact]
    public void The_interface_and_the_implementation_are_the_same_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(EmptyConfiguration);
        services.AddSmtpEmailSender(o =>
        {
            o.Host = "127.0.0.1";
            o.DefaultFromAddress = "noreply@example.com";
        });

        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<SmtpEmailSender>(), provider.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void Registration_binds_the_documented_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SmtpOptions.SectionName}:Host"] = "smtp.internal",
                [$"{SmtpOptions.SectionName}:Port"] = "2525",
                [$"{SmtpOptions.SectionName}:DefaultFromAddress"] = "noreply@internal",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddSmtpEmailSender();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;

        Assert.Equal("smtp.internal", options.Host);
        Assert.Equal(2525, options.Port);
        Assert.Equal("noreply@internal", options.DefaultFromAddress);
    }

    // ValidateOnStart 没接上时，非法配置会一路走到第一次发送才失败
    [Fact]
    public void Invalid_options_fail_the_host_at_startup()
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(EmptyConfiguration);
        services.AddSmtpEmailSender(o => o.Host = string.Empty);

        using var provider = services.BuildServiceProvider();

        var startupValidators = provider.GetServices<IStartupValidator>().ToList();
        Assert.NotEmpty(startupValidators);
        var failure = Assert.Throws<OptionsValidationException>(() => startupValidators[0].Validate());
        // 每条失败单独一项、各自带配置键：三项都配错时运维一次就能看全，而不是只看到第一条
        Assert.NotEmpty(failure.Failures);
        Assert.All(failure.Failures, message => Assert.StartsWith(SmtpOptions.SectionName, message, StringComparison.Ordinal));
    }

    // 发送路径不再复查口令，只配用户名必须在启动期失败，否则发信会带着残缺凭据去连服务器
    [Fact]
    public void Username_without_password_fails_the_host_at_startup()
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(EmptyConfiguration);
        services.AddSmtpEmailSender(o =>
        {
            o.Host = "127.0.0.1";
            o.DefaultFromAddress = "noreply@example.com";
            o.Username = "mailer";
        });

        using var provider = services.BuildServiceProvider();

        var startupValidators = provider.GetServices<IStartupValidator>().ToList();
        Assert.NotEmpty(startupValidators);
        var failure = Assert.Throws<OptionsValidationException>(() => startupValidators[0].Validate());
        var message = Assert.Single(failure.Failures);
        Assert.StartsWith($"{SmtpOptions.SectionName}:", message, StringComparison.Ordinal);
        Assert.Contains("Username and Password must be set together", message, StringComparison.Ordinal);
    }

    // 编程式配置在配置节之后应用：同一键两边都给时以代码为准
    [Fact]
    public void Programmatic_configuration_overrides_the_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SmtpOptions.SectionName}:Host"] = "smtp.internal",
                [$"{SmtpOptions.SectionName}:DefaultFromAddress"] = "noreply@internal",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddSmtpEmailSender(o => o.Host = "smtp.override");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;

        Assert.Equal("smtp.override", options.Host);
        Assert.Equal("noreply@internal", options.DefaultFromAddress);
    }

    // 改了配置节路径，报错里的键名也得跟着走：照着默认节名去补配置，启动仍会失败
    [Fact]
    public void Validation_failures_name_the_configured_section()
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(EmptyConfiguration);
        services.AddSmtpEmailSender(configSectionPath: "Mail");

        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);
        Assert.All(failure.Failures, message => Assert.StartsWith("Mail:", message, StringComparison.Ordinal));
    }

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();
}
