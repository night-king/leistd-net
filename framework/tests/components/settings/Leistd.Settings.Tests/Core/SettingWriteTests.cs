using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Exceptions;
using Leistd.TestBase.Doubles;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;

namespace Leistd.Settings.Tests.Core;

// 写入前对照定义校验：未定义的名称与不允许的层级都要在写入点拒绝。
public class SettingWriteTests
{
    [Fact]
    public async Task Writing_an_undefined_setting_fails()
    {
        var (manager, store) = Build();

        await Assert.ThrowsAsync<UndefinedSettingException>(
            () => manager.SetAsync("Nope", "x", SettingScopes.Tenant));

        Assert.Empty(store.Writes);
    }

    // 允许写进未声明的层级，等于让回落顺序失去意义。
    [Fact]
    public async Task Writing_to_a_scope_the_definition_forbids_fails()
    {
        var (manager, store) = Build();

        var error = await Assert.ThrowsAsync<SettingScopeNotAllowedException>(
            () => manager.SetAsync("Export.MaxRowsPerFile", "x", SettingScopes.User, "user-1"));

        Assert.Equal(SettingScopes.Tenant, error.Allowed);
        Assert.Empty(store.Writes);
    }

    /// <summary>进程级设置只能写宿主那一层，租户级与用户级都要被拒。</summary>
    /// <remarks>
    /// 不拒的话，租户各写一份自己的日志级别，界面上显示得好好的、实际一个都不生效——
    /// 这类"看着改了其实没改"最难被发现。
    /// </remarks>
    [Fact]
    public async Task A_host_scoped_setting_rejects_tenant_and_user_writes()
    {
        var (manager, store) = Build();

        await manager.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.Host);

        var write = Assert.Single(store.Writes);
        Assert.Equal(SettingScopes.Host, write.Scope);
        Assert.Null(write.UserId);

        await Assert.ThrowsAsync<SettingScopeNotAllowedException>(
            () => manager.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.Tenant));
        await Assert.ThrowsAsync<SettingScopeNotAllowedException>(
            () => manager.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.User, "u1"));
    }

    // 反过来同样要拦：可分层覆盖的设置不该被当成进程级来写，否则它会落到宿主行上，
    // 而读取仍按租户层回落——写进去的那个值谁也读不到。
    [Fact]
    public async Task A_layered_setting_rejects_host_writes()
    {
        var (manager, _) = Build();

        await Assert.ThrowsAsync<SettingScopeNotAllowedException>(
            () => manager.SetAsync("Display.Language", "zh-CN", SettingScopes.Host));
    }

    [Fact]
    public async Task Writing_a_user_scope_value_requires_a_user_id()
    {
        var (manager, _) = Build();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => manager.SetAsync("Display.Language", "en-US", SettingScopes.User));
    }

    [Fact]
    public async Task A_tenant_write_never_carries_a_user_id()
    {
        var (manager, store) = Build();

        await manager.SetAsync("Display.Language", "en-US", SettingScopes.Tenant, "user-1");

        Assert.Null(Assert.Single(store.Writes).UserId);
    }

    // null 表示清除该层级的值，使读取回落到下一层。
    [Fact]
    public async Task A_null_value_clears_the_level()
    {
        var (manager, store) = Build();

        await manager.SetAsync("Display.Language", null, SettingScopes.User, "user-1");

        Assert.Null(Assert.Single(store.Writes).Value);
    }

    private static (ISettingManager Manager, FakeSettingStore Store) Build()
    {
        var store = new FakeSettingStore();
        var definitions = new SettingDefinitionManager([new WriteTestDefinitionProvider()]);
        var manager = new DefaultSettingManager(
            definitions,
            store,
            new DefaultSettingProvider(definitions, store, new FakeCurrentUser(Guid.NewGuid())),
            [],
            WriteDependencies.Lock(),
            new RecordingUnitOfWorkManager());
        return (manager, store);
    }

    private sealed class WriteTestDefinitionProvider : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Display.Language", "zh-CN", SettingScopes.All);
            context.Add("Export.MaxRowsPerFile", "Acme", SettingScopes.Tenant);
            context.Add("Logging.MinimumLevel", "Information", SettingScopes.Host);
        }
    }
}
