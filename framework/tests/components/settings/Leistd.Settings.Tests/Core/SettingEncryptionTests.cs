using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.TestBase.Doubles;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;

namespace Leistd.Settings.Tests.Core;

// 机密设置：经宿主的 Data Protection 加密落库、读出解密，永不经"客户端可见"下发。
public class SettingEncryptionTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task An_encrypted_setting_is_stored_as_cipher_text_and_read_back_as_plain_text()
    {
        var store = new FakeSettingStore();

        await Manager(store, _dataProtection).SetAsync("Email.Password", "s3cret", SettingScopes.Host);

        var stored = Assert.Single(store.Writes).Value!;
        Assert.NotEqual("s3cret", stored);
        store.Host["Email.Password"] = stored;
        Assert.Equal("s3cret", await Provider(store, _dataProtection).GetOrNullAsync("Email.Password"));
    }

    // 清除不是写入一个值，没有东西可加密
    [Fact]
    public async Task Clearing_an_encrypted_setting_writes_null()
    {
        var store = new FakeSettingStore();

        await Manager(store, _dataProtection).SetAsync("Email.Password", null, SettingScopes.Host);

        Assert.Null(Assert.Single(store.Writes).Value);
    }

    // 默认值按约定不加密，原样返回；拿去解密会因为"不是密文"而失败
    [Fact]
    public async Task The_default_value_of_an_encrypted_setting_is_not_decrypted()
    {
        Assert.Equal("fallback", await Provider(new FakeSettingStore(), _dataProtection).GetOrNullAsync("Api.Token"));
    }

    // 用途带设置名：一个设置的密文挪到别的设置名下解不开，不能靠挪行换值
    [Fact]
    public async Task Cipher_text_is_bound_to_the_setting_name()
    {
        var store = new FakeSettingStore();
        await Manager(store, _dataProtection).SetAsync("Email.Password", "s3cret", SettingScopes.Host);
        store.Tenant["Api.Token"] = Assert.Single(store.Writes).Value!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(store, _dataProtection).GetOrNullAsync("Api.Token"));
    }

    // 没配 Data Protection：机密设置宁可失败也不存明文；普通设置不受影响
    [Fact]
    public async Task Without_data_protection_only_encrypted_settings_fail()
    {
        var store = new FakeSettingStore();
        var manager = Manager(store, dataProtection: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetAsync("Email.Password", "s3cret", SettingScopes.Host));
        Assert.Empty(store.Writes);

        await manager.SetAsync("Email.Host", "smtp.example.com", SettingScopes.Host);
        Assert.Equal("smtp.example.com", Assert.Single(store.Writes).Value);
    }

    // 可见标记只表示界面上有这一项可写；按"客户端可见"批量读取时它不能出现，也不去解密
    [Fact]
    public async Task Encrypted_settings_are_never_returned_as_visible_to_clients()
    {
        var store = new FakeSettingStore();
        await Manager(store, _dataProtection).SetAsync("Email.Password", "s3cret", SettingScopes.Host);
        store.Host["Email.Password"] = Assert.Single(store.Writes).Value!;

        // 没有 Data Protection 也能批量读：机密设置不在其中，就不会去解密
        var visible = await Provider(store, dataProtection: null).GetAllAsync(visibleToClientsOnly: true);
        var all = await Provider(store, _dataProtection).GetAllAsync();

        Assert.False(visible.ContainsKey("Email.Password"));
        Assert.Equal("s3cret", all["Email.Password"]);
    }

    // 同一作用域先写后读：写入入口作废读取器的记忆化结果，读到的不是写入前的快照
    [Fact]
    public async Task A_write_invalidates_the_same_scope_reader()
    {
        var store = new FakeSettingStore();
        var provider = Provider(store, _dataProtection);
        var manager = new DefaultSettingManager(Definitions(), store, provider, [], WriteDependencies.Lock(), new RecordingUnitOfWorkManager(), dataProtectionProvider: _dataProtection);

        Assert.Null(await provider.GetOrNullAsync("Email.Host"));
        Assert.Equal(1, store.HostReads);

        await manager.SetAsync("Email.Host", "smtp.example.com", SettingScopes.Host);
        store.Host["Email.Host"] = "smtp.example.com";

        Assert.Equal("smtp.example.com", await provider.GetOrNullAsync("Email.Host"));
        Assert.Equal(2, store.HostReads);
    }

    private static ISettingManager Manager(FakeSettingStore store, IDataProtectionProvider? dataProtection)
        => new DefaultSettingManager(Definitions(), store, Provider(store, dataProtection), [], WriteDependencies.Lock(), new RecordingUnitOfWorkManager(), dataProtectionProvider: dataProtection);

    private static DefaultSettingProvider Provider(FakeSettingStore store, IDataProtectionProvider? dataProtection)
        => new(Definitions(), store, new FakeCurrentUser(UserId), dataProtection);

    private static SettingDefinitionManager Definitions() => new([new EncryptionTestDefinitionProvider()]);

    private sealed class EncryptionTestDefinitionProvider : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            var password = context.Add("Email.Password", scopes: SettingScopes.Host);
            password.IsEncrypted = true;
            password.IsVisibleToClients = true;

            context.Add("Api.Token", "fallback", SettingScopes.Tenant).IsEncrypted = true;
            context.Add("Email.Host", scopes: SettingScopes.Host).IsVisibleToClients = true;
        }
    }
}
