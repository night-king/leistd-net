using System.Data;
using Leistd.UnitOfWork.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.UnitOfWork.Tests.Core;

/// <summary><c>AddUnitOfWork</c> 绑定 <c>Leistd:UnitOfWork</c>，委托在其后应用；默认选项启动期校验，单次选项在 <c>Begin</c> 时按同一判据校验。</summary>
/// <remarks>
/// 命令超时以整秒计且 0 表示不限时：不拒绝亚秒值的话，配置的 0.5 秒会变成"永不超时"，没有任何报错。
/// </remarks>
public class UnitOfWorkOptionsBindingTests
{
    private const string MaximumTimeoutText = "24855.03:14:07";
    private const string BeyondMaximumTimeoutText = "24855.03:14:08";

    // 只传委托的宿主同样要拿到配置文件里的超时
    [Fact]
    public void The_section_is_bound_and_the_delegate_overrides_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{UnitOfWorkOptions.SectionName}:Timeout"] = "00:00:30",
                [$"{UnitOfWorkOptions.SectionName}:IsTransactional"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUnitOfWork(options => options.IsTransactional = true);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<UnitOfWorkOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.True(options.IsTransactional);
    }

    [Fact]
    public void The_default_configuration_passes_startup_validation()
    {
        using var provider = Build(new Dictionary<string, string?>());

        provider.GetRequiredService<IStartupValidator>().Validate();

        var options = provider.GetRequiredService<IOptions<UnitOfWorkOptions>>().Value;
        Assert.Null(options.Timeout);
        Assert.Null(options.IsolationLevel);
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData(MaximumTimeoutText)]
    public void Boundary_timeouts_pass_startup_validation(string timeout)
    {
        using var provider = Build(new Dictionary<string, string?> { [$"{UnitOfWorkOptions.SectionName}:Timeout"] = timeout });

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.Equal(TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture),
            provider.GetRequiredService<IOptions<UnitOfWorkOptions>>().Value.Timeout);
    }

    [Theory]
    [InlineData(UnitOfWorkOptions.SectionName, "Timeout", "00:00:00.500")]
    [InlineData(UnitOfWorkOptions.SectionName, "Timeout", "00:00:00")]
    [InlineData(UnitOfWorkOptions.SectionName, "Timeout", "-00:00:05")]
    [InlineData(UnitOfWorkOptions.SectionName, "Timeout", BeyondMaximumTimeoutText)]
    [InlineData(UnitOfWorkOptions.SectionName, "IsolationLevel", "12345")]
    [InlineData("Ops:Transactions", "Timeout", "00:00:00.500")]
    [InlineData("Ops:Transactions", "IsolationLevel", "12345")]
    public void Invalid_configuration_fails_at_startup_keyed_by_the_actual_section(string section, string key, string value)
    {
        using var provider = Build(new Dictionary<string, string?> { [$"{section}:{key}"] = value }, section);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.StartsWith($"{section}:{key} ", Assert.Single(exception.Failures), StringComparison.Ordinal);
    }

    // 委托覆盖之后再校验：代码里写错的值与配置文件写错的值同样在启动期失败
    [Fact]
    public void Invalid_values_from_the_delegate_fail_at_startup_with_every_failure_listed()
    {
        using var provider = Build(new Dictionary<string, string?>(), configure: options =>
        {
            options.Timeout = TimeSpan.FromMilliseconds(500);
            options.IsolationLevel = (IsolationLevel)12345;
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(
            [$"{UnitOfWorkOptions.SectionName}:Timeout", $"{UnitOfWorkOptions.SectionName}:IsolationLevel"],
            exception.Failures.Select(failure => failure[..failure.IndexOf(' ', StringComparison.Ordinal)]));
    }

    public static TheoryData<TimeSpan?, IsolationLevel?> InvalidPerCallOptions => new()
    {
        { TimeSpan.FromMilliseconds(500), null },
        { TimeSpan.Zero, null },
        { TimeSpan.FromSeconds(-5), null },
        { TimeSpan.FromSeconds(int.MaxValue) + TimeSpan.FromSeconds(1), null },
        { null, (IsolationLevel)12345 },
    };

    /// <summary>单次选项是方法参数，按参数错误报出；拒绝发生在创建工作单元之前，环境里不留下任何工作单元。</summary>
    [Theory]
    [MemberData(nameof(InvalidPerCallOptions))]
    public void Invalid_per_call_options_are_rejected_before_a_unit_of_work_is_created(TimeSpan? timeout, IsolationLevel? isolationLevel)
    {
        using var provider = Build(new Dictionary<string, string?>());
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => manager.Begin(
            new UnitOfWorkOptions { Timeout = timeout, IsolationLevel = isolationLevel }, requiresNew: true));

        Assert.Equal("options", exception.ParamName);
        Assert.StartsWith(nameof(UnitOfWorkOptions) + ".", exception.Message, StringComparison.Ordinal);
        Assert.Null(manager.Current);
    }

    // 并入外层时单次选项虽不生效，传错的值同样是调用方的编码错误，照样拒绝，外层不受影响
    [Fact]
    public void Invalid_per_call_options_are_rejected_when_joining_an_outer_unit_of_work()
    {
        using var provider = Build(new Dictionary<string, string?>());
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var outer = manager.Begin();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => manager.Begin(new UnitOfWorkOptions { Timeout = TimeSpan.FromMilliseconds(500) }));

        Assert.Same(outer, manager.Current);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Boundary_per_call_timeouts_create_the_unit_of_work(int seconds)
    {
        using var provider = Build(new Dictionary<string, string?>());
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var uow = manager.Begin(new UnitOfWorkOptions { Timeout = TimeSpan.FromSeconds(seconds) }, requiresNew: true);

        Assert.Equal(TimeSpan.FromSeconds(seconds), uow.Options.Timeout);
        Assert.Same(uow, manager.Current);
    }

    private static ServiceProvider Build(
        Dictionary<string, string?> settings,
        string configSectionPath = UnitOfWorkOptions.SectionName,
        Action<UnitOfWorkOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddUnitOfWork(configure, configSectionPath);
        return services.BuildServiceProvider();
    }
}
