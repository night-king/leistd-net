using Leistd.BackgroundJobs.Recurring;
using Leistd.Notifications.EntityFrameworkCore;
using Leistd.Notifications.EntityFrameworkCore.Options;
using Leistd.TestBase.Assertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

/// <summary>保留期的注册面：配置节可换、校验消息按实际配置节报键、重复调用不叠加验证器与周期任务。</summary>
public sealed class NotificationRetentionRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection> register)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_custom_section_is_bound()
    {
        using var provider = Build(
            new() { ["Ops:NotificationRetention:ReadRetentionDays"] = "10" },
            services => services.AddNotificationRetention<DbContext>(configSectionPath: "Ops:NotificationRetention"));

        Assert.Equal(10, provider.GetRequiredService<IOptions<NotificationRetentionOptions>>().Value.ReadRetentionDays);
    }

    // 运维照着报错里的键去补配置：键名必须是实际绑定的那一节，默认节与自定义节都一样
    [Theory]
    [InlineData(null)]
    [InlineData("Ops:NotificationRetention")]
    public void Validation_failures_name_the_bound_section(string? section)
    {
        var path = section ?? NotificationRetentionOptions.SectionName;
        using var provider = Build(
            new() { [$"{path}:BatchSize"] = "1" },
            services => services.AddNotificationRetention<DbContext>(configSectionPath: path));

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<NotificationRetentionOptions>>().Value);

        Assert.All(failure.Failures, message => Assert.StartsWith($"{path}:", message, StringComparison.Ordinal));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddNotificationRetention<DbContext>());

    // 验证器不计入 AssertIdempotent：登记两份时每条失败报两遍；周期任务定义登记两份则同一时段跑两次
    [Fact]
    public void Repeated_registration_keeps_one_validator_and_one_recurring_job()
    {
        var services = new ServiceCollection();

        services.AddNotificationRetention<DbContext>().AddNotificationRetention<DbContext>();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<NotificationRetentionOptions>));
        Assert.Single(services.Select(d => d.ImplementationInstance).OfType<RecurringJobDefinition>());
    }

    // 选项只有一份：第二次换了配置节，报错键名与实际生效的值就对不上了
    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection().AddNotificationRetention<DbContext>();

        Assert.Throws<InvalidOperationException>(
            () => services.AddNotificationRetention<DbContext>(configSectionPath: "Ops:NotificationRetention"));
    }
}
