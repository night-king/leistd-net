using Leistd.Settings.Definitions;
using Xunit;

namespace Leistd.Settings.Tests.Core;

public class SettingDefinitionTests
{
    [Fact]
    public void Definitions_from_all_providers_are_merged()
    {
        var manager = Build(
            new DelegateProvider(c => c.Add("Display.Language", "zh-CN", SettingScopes.All)),
            new DelegateProvider(c => c.Add("Export.MaxRowsPerFile")));

        Assert.Equal(["Display.Language", "Export.MaxRowsPerFile"], manager.GetAll().Select(d => d.Name).Order());
    }

    /// <summary>分组标识随定义原样保留，未指定则为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 框架只搬运这个标识、不翻译也不给回落值：分组的展示文案要请求 culture，
    /// 而定义是一次性加载并缓存的。未分组返回 null，由宿主决定摆到哪里
    /// （模板的做法是归入"其他"，而不是让它从界面上消失）。
    /// </remarks>
    [Fact]
    public void A_group_is_carried_through_as_declared()
    {
        var manager = Build(
            new DelegateProvider(c => c.Add("Display.TimeZone", group: "Display")),
            new DelegateProvider(c => c.Add("Export.MaxRowsPerFile")));

        var definitions = manager.GetAll().ToDictionary(d => d.Name);

        Assert.Equal("Display", definitions["Display.TimeZone"].Group);
        Assert.Null(definitions["Export.MaxRowsPerFile"].Group);
    }

    /// <summary>进程级与可分层覆盖互斥：组合层级在定义阶段就被拒绝。</summary>
    /// <remarks>
    /// <c>Host | User</c> 没有一致的读取解释——要么按宿主那一行、要么按用户覆盖。
    /// 允许注册的话，写入端按"允许用户级"放行，而解析端按进程级读宿主行，
    /// 于是用户改完看得见、实际读到的是另一个值。
    /// </remarks>
    [Fact]
    public void The_host_scope_cannot_be_combined_with_other_scopes()
    {
        var manager = Build(new DelegateProvider(
            c => c.Add("Logging.MinimumLevel", "Information", SettingScopes.Host | SettingScopes.User)));

        var error = Assert.Throws<ArgumentException>(() => manager.GetAll());
        Assert.Contains("host scope is exclusive", error.Message, StringComparison.Ordinal);
    }

    // 重名会让后注册的那份定义静默失效，读取时表现为"默认值不对"，所以就地失败。
    // 定义是惰性汇总的，容器不会主动实例化管理器，因此这个失败发生在第一次访问定义时，
    // 而不是宿主启动时——测试也只能这样触发它。
    [Fact]
    public void A_duplicate_name_fails_when_definitions_are_first_accessed()
    {
        var manager = Build(
            new DelegateProvider(c => c.Add("Display.Language")),
            new DelegateProvider(c => c.Add("Display.Language")));

        var error = Assert.Throws<InvalidOperationException>(() => manager.GetAll());

        Assert.Contains("globally unique", error.Message);
    }

    [Fact]
    public void Undefined_names_resolve_to_null()
    {
        var manager = Build(new DelegateProvider(c => c.Add("Display.Language")));

        Assert.Null(manager.GetOrNull("Nope"));
    }

    // 提供者没有注册顺序约定：调整别人声明的定义只能放在 PostDefine，排在它前面注册也要生效
    [Fact]
    public void PostDefine_runs_after_every_provider_has_defined()
    {
        var manager = Build(
            new PostProvider(c => c.GetOrNull("Logging.MinimumLevel")!.DefaultValue = "Warning"),
            new DelegateProvider(c => c.Add("Logging.MinimumLevel", "Information", SettingScopes.Host)));

        Assert.Equal("Warning", manager.GetOrNull("Logging.MinimumLevel")!.DefaultValue);
    }

    [Fact]
    public void Value_metadata_is_declared_fluently()
    {
        var manager = Build(new DelegateProvider(c =>
        {
            c.Add("Security.RequireTwoFactor", "false").AsBoolean();
            c.Add("Security.LockoutDurationMinutes", "15").AsInteger(1, 1440);
            c.Add("Logging.MinimumLevel", "Information", SettingScopes.Host).WithAllowedValues("Debug", "Information");
        }));

        Assert.Equal(SettingValueType.Boolean, manager.GetOrNull("Security.RequireTwoFactor")!.ValueType);
        var duration = manager.GetOrNull("Security.LockoutDurationMinutes")!;
        Assert.Equal((SettingValueType.Integer, 1, 1440), (duration.ValueType, duration.Minimum!.Value, duration.Maximum!.Value));
        Assert.Equal(["Debug", "Information"], manager.GetOrNull("Logging.MinimumLevel")!.AllowedValues!);
    }

    private static ISettingDefinitionManager Build(params ISettingDefinitionProvider[] providers)
        => new SettingDefinitionManager(providers);

    private sealed class DelegateProvider(Action<ISettingDefinitionContext> define) : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context) => define(context);
    }

    private sealed class PostProvider(Action<ISettingDefinitionContext> postDefine) : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
        }

        public void PostDefine(ISettingDefinitionContext context) => postDefine(context);
    }
}
