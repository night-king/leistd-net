using Leistd.BackgroundJobs.Recurring;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Options;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary>保留期的注册面：配置节可换、校验消息按实际配置节报键、重复调用不叠加验证器与周期任务。</summary>
public sealed class OperationRecordRetentionRegistrationTests
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
            new() { ["Ops:AuditRetention:RetentionDays"] = "40" },
            services => services.AddOperationRecordRetention<TestDbContext>(configSectionPath: "Ops:AuditRetention"));

        Assert.Equal(40, provider.GetRequiredService<IOptions<OperationRecordRetentionOptions>>().Value.RetentionDays);
    }

    // 运维照着报错里的键去补配置：键名必须是实际绑定的那一节，默认节与自定义节都一样
    [Theory]
    [InlineData(null)]
    [InlineData("Ops:AuditRetention")]
    public void Validation_failures_name_the_bound_section(string? section)
    {
        var path = section ?? OperationRecordRetentionOptions.SectionName;
        using var provider = Build(
            new() { [$"{path}:BatchSize"] = "1" },
            services => services.AddOperationRecordRetention<TestDbContext>(configSectionPath: path));

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OperationRecordRetentionOptions>>().Value);

        Assert.All(failure.Failures, message => Assert.StartsWith($"{path}:", message, StringComparison.Ordinal));
    }

    /// <summary>保留天数没有默认值：启用时必填，关闭时可以不填，填了照样校验区间。</summary>
    /// <remarks>保留期受法律与合同约束，组件给不出一个替部署方负责的天数。</remarks>
    [Theory]
    [InlineData("true", null, "RetentionDays is required when Enabled is true.")]
    [InlineData("true", "29", "RetentionDays must be between 30 and 3650.")]
    [InlineData("false", "3651", "RetentionDays must be between 30 and 3650.")]
    [InlineData("true", "30", null)]
    [InlineData("true", "3650", null)]
    [InlineData("false", null, null)]
    public void Retention_days_are_required_only_when_enabled(string enabled, string? retentionDays, string? expectedFailure)
    {
        const string path = "Ops:AuditRetention";
        using var provider = Build(
            new() { [$"{path}:Enabled"] = enabled, [$"{path}:RetentionDays"] = retentionDays },
            services => services.AddOperationRecordRetention<TestDbContext>(configSectionPath: path));
        var validator = provider.GetRequiredService<IStartupValidator>();

        if (expectedFailure is null)
        {
            validator.Validate();
            Assert.Equal(
                retentionDays is null ? null : int.Parse(retentionDays, System.Globalization.CultureInfo.InvariantCulture),
                provider.GetRequiredService<IOptions<OperationRecordRetentionOptions>>().Value.RetentionDays);
            return;
        }

        var failure = Assert.Throws<OptionsValidationException>(validator.Validate);
        Assert.Equal($"{path}:{expectedFailure}", Assert.Single(failure.Failures));
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddOperationRecordRetention<TestDbContext>());

    // 验证器不计入 AssertIdempotent：登记两份时每条失败报两遍；周期任务定义登记两份则同一时段跑两次
    [Fact]
    public void Repeated_registration_keeps_one_validator_and_one_recurring_job()
    {
        var services = new ServiceCollection();

        services.AddOperationRecordRetention<TestDbContext>().AddOperationRecordRetention<TestDbContext>();

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<OperationRecordRetentionOptions>));
        Assert.Single(services.Select(d => d.ImplementationInstance).OfType<RecurringJobDefinition>());
    }

    // 选项只有一份：第二次换了配置节，报错键名与实际生效的值就对不上了
    [Fact]
    public void Repeated_registration_with_another_section_is_rejected()
    {
        var services = new ServiceCollection().AddOperationRecordRetention<TestDbContext>();

        Assert.Throws<InvalidOperationException>(
            () => services.AddOperationRecordRetention<TestDbContext>(configSectionPath: "Ops:AuditRetention"));
    }
}
