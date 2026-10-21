using Leistd.Localization.Options;
using Leistd.OperationRecords.Errors;
using Leistd.OperationRecords.Options;
using Leistd.OperationRecords.Queries;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.OperationRecords.Tests.Core;

/// <summary><c>AddOperationRecords</c> 与 <c>AddOperationRecordQueries</c> 的注册面。</summary>
/// <remarks>
/// Leistd 靠宿主显式调用 <c>AddXxx()</c> 组合，注册结果就是公共契约的一部分：
/// 生命周期写错、重复注册、组件之间意外互相覆盖，编译期一个都发现不了。
/// </remarks>
public sealed class OperationRecordRegistrationTests
{
    [Fact]
    public void AddOperationRecords_registers_the_recorder_as_transient()
    {
        var services = new ServiceCollection();

        services.AddOperationRecords();

        services.AssertSingle<IOperationRecorder>(ServiceLifetime.Transient);
        services.AssertImplementedBy<IOperationRecorder, OperationRecorder>();
    }

    [Fact]
    public void AddOperationRecords_is_idempotent()
    {
        // 宿主重复调用是常态（组合根拆分、EF 包内部也会调一次）。
        // 不幂等的表现是 IOperationRecorder 被解析成 IEnumerable 时出现重复项，同一件事记两遍。
        ServiceCollectionAssertions.AssertIdempotent(services => services.AddOperationRecords());
    }

    [Fact]
    public void AddOperationRecords_with_a_delegate_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(
            services => services.AddOperationRecords(options => options.ImpersonatorIdClaimType = "act_sub"));

    /// <summary>自产失败码的默认译文随注册登记一次，重复调用不重复登记。</summary>
    [Fact]
    public void AddOperationRecords_registers_the_component_resources_once()
    {
        using var provider = new ServiceCollection()
            .AddOperationRecords()
            .AddOperationRecords(options => options.ImpersonatorIdClaimType = "act_sub")
            .BuildServiceProvider();

        var assemblies = provider.GetRequiredService<IOptions<JsonLocalizationOptions>>().Value.ResourceAssemblies;

        Assert.Single(assemblies, assembly => assembly == typeof(OperationFailureCodes).Assembly);
    }

    /// <summary>宿主不配置时选项也解析得出，值为默认 claim 名。</summary>
    /// <remarks>
    /// 漏了这一步的症状很隐蔽：记录器在解析 <c>IOptions&lt;T&gt;</c> 时才失败，
    /// 而那是第一次真的有人被拒之后——审计恰好在最需要它的时候不可用。
    /// </remarks>
    [Fact]
    public void The_options_resolve_with_defaults_without_any_configuration()
    {
        using var provider = new ServiceCollection().AddOperationRecords().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OperationRecordOptions>>().Value;

        Assert.Equal("impersonator_id", options.ImpersonatorIdClaimType);
        Assert.Equal("impersonator_name", options.ImpersonatorNameClaimType);
    }

    /// <summary>宿主签发的 claim 换了名字时从选项改，组件不写死；多次传入的委托依次叠加。</summary>
    [Fact]
    public void The_configure_delegate_overrides_the_claim_types()
    {
        using var provider = new ServiceCollection()
            .AddOperationRecords(options => options.ImpersonatorIdClaimType = "act_sub")
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OperationRecordOptions>>().Value;

        Assert.Equal("act_sub", options.ImpersonatorIdClaimType);
        // 只改了一项，另一项必须保持默认，而不是被整体覆盖成空
        Assert.Equal("impersonator_name", options.ImpersonatorNameClaimType);
    }

    [Fact]
    public void Delegates_from_repeated_calls_are_applied_in_order()
    {
        using var provider = new ServiceCollection()
            .AddOperationRecords(options => options.ImpersonatorIdClaimType = "first")
            .AddOperationRecords(options => options.ImpersonatorNameClaimType = "act_name")
            .AddOperationRecords(options => options.ImpersonatorIdClaimType = "act_sub")
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OperationRecordOptions>>().Value;

        Assert.Equal(("act_sub", "act_name"), (options.ImpersonatorIdClaimType, options.ImpersonatorNameClaimType));
    }

    /// <summary>Core 不替宿主注册持久化：缺存储时解析记录器直接失败，而不是静默什么都不记。</summary>
    [Fact]
    public void AddOperationRecords_does_not_register_a_store()
    {
        var services = new ServiceCollection();

        services.AddOperationRecords();

        services.AssertNotRegistered<IOperationRecordWriter>();
        services.AssertNotRegistered<IOperationRecordReader>();
        services.AssertNotRegistered<IOperationRecordQueryService>();
    }

    // ---------- AddOperationRecordQueries ----------

    // 查询用例与记录器一起登记：读页面的同时还要给导出本身记一条
    [Fact]
    public void AddOperationRecordQueries_registers_the_query_service_as_transient_with_the_recorder()
    {
        var services = new ServiceCollection();

        services.AddOperationRecordQueries();

        services.AssertSingle<IOperationRecordQueryService>(ServiceLifetime.Transient);
        services.AssertImplementedBy<IOperationRecordQueryService, OperationRecordQueryService>();
        services.AssertSingle<IOperationRecorder>(ServiceLifetime.Transient);
    }

    // EF 包会调它，宿主也可能再调一次
    [Fact]
    public void AddOperationRecordQueries_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddOperationRecordQueries());

    // 默认用例按 TryAdd 登记：宿主先换成自己的实现时保留宿主那条
    [Fact]
    public void AddOperationRecordQueries_keeps_a_query_service_registered_by_the_host()
    {
        var services = new ServiceCollection();
        services.AddScoped<IOperationRecordQueryService>(_ => throw new NotSupportedException());

        services.AddOperationRecordQueries();

        var descriptor = services.AssertSingle<IOperationRecordQueryService>(ServiceLifetime.Scoped);
        Assert.NotNull(descriptor.ImplementationFactory);
    }
}
