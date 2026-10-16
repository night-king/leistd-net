using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using Leistd.MultiTenancy.Context;
using Leistd.Security.Users;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Stores;
using Leistd.Settings.Exceptions;
using Microsoft.AspNetCore.DataProtection;

namespace Leistd.Settings.Resolution;

/// <summary>按 用户级 → 租户级 → 代码默认值 的顺序解析设置。</summary>
/// <remarks>
/// 按作用域缓存已读取的设置，同一作用域内保持一致；跨作用域重新读取，不使用分布式缓存。
/// 缓存按当前租户与当前用户分开：同一作用域里切换租户（模拟登录、后台逐租户处理）读到的是目标租户的值。
/// 经 <see cref="ISettingManager"/> 写入后，同一作用域的缓存即作废。
/// <para>宿主层的可达性由 <see cref="ISettingStore.CanAccessHostScope"/> 判断，读取语义见 <see cref="ISettingProvider"/>。</para>
/// </remarks>
/// <param name="definitionManager">设置定义。</param>
/// <param name="store">设置值存储。</param>
/// <param name="currentUser">当前用户；匿名时只回落到租户级。</param>
/// <param name="dataProtectionProvider">机密设置的解密用宿主的 Data Protection；为 <see langword="null"/> 时只有读取落过库的机密设置会失败。</param>
/// <param name="currentTenant">缓存按它分键；未注册多租户时为 <see langword="null"/>，全部读取共用宿主一份。</param>
public sealed class DefaultSettingProvider(
    ISettingDefinitionManager definitionManager,
    ISettingStore store,
    ICurrentUser currentUser,
    IDataProtectionProvider? dataProtectionProvider = null,
    ICurrentTenant? currentTenant = null) : ISettingProvider
{
    // 机密设置的用途字符串，固定不变（改动会让已存密文不可解）；每个设置再以设置名作子用途，密文不能挪到别的设置名下。
    internal const string ProtectionPurpose = "Leistd.Settings.EncryptedValue.v1";

    // 按官方用法在构造时创建一次、之后复用（IDataProtector 线程安全）
    private readonly IDataProtector? _protector = dataProtectionProvider?.CreateProtector(ProtectionPurpose);

    // 存储按调用时的租户隐式读取，缓存键因此必须带租户；用户层再按用户分开
    private readonly Dictionary<(Guid? TenantId, string? UserId), Snapshot> _snapshots = [];

    /// <inheritdoc />
    public async Task<string?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
    {
        var definition = definitionManager.GetOrNull(name) ?? throw new UndefinedSettingException(name);

        var snapshot = await LoadAsync(cancellationToken);

        return Resolve(definition, snapshot, snapshot.UserValues);
    }

    /// <inheritdoc />
    public async Task<string?> GetOrNullForUserAsync(
        string name,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var definition = definitionManager.GetOrNull(name) ?? throw new UndefinedSettingException(name);

        var snapshot = await LoadAsync(cancellationToken);

        // 当前用户就是目标用户时复用已加载的那份；否则只为这次读取查一次，不进记忆化
        var userValues = userId == currentUser.Id?.ToString() || !definition.Scopes.HasFlag(SettingScopes.User)
            ? snapshot.UserValues
            : await store.GetAllAsync(SettingScopes.User, userId, cancellationToken);

        return Resolve(definition, snapshot, userValues);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        var value = await GetOrNullAsync(name, cancellationToken);
        return string.IsNullOrEmpty(value) ? default : Convert<T>(value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(
        bool visibleToClientsOnly = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);

        return definitionManager.GetAll()
            // 机密设置永不下发客户端，可见标记只表示界面上有这一项可写
            .Where(definition => !visibleToClientsOnly || (definition.IsVisibleToClients && !definition.IsEncrypted))
            .Where(definition => snapshot.HostValues is not null || !definition.Scopes.HasFlag(SettingScopes.Host))
            .ToDictionary(
                definition => definition.Name,
                definition => Resolve(definition, snapshot, snapshot.UserValues),
                StringComparer.Ordinal);
    }

    private string? Resolve(ISettingDefinition definition, Snapshot snapshot, IReadOnlyDictionary<string, string> userValues)
    {
        var (value, stored) = ResolveRaw(definition, snapshot, userValues);
        if (!definition.IsEncrypted || !stored || value is null)
        {
            return value;
        }

        // 只解密落过库的值：代码默认值按约定不加密（见 ISettingDefinition.IsEncrypted）
        return Decrypt(_protector, definition.Name, value);
    }

    // 解密一条落过库的机密值；写入入口算清除后的回落值时也用它
    internal static string Decrypt(IDataProtector? protector, string name, string value)
    {
        try
        {
            return (protector ?? throw MissingDataProtection(name))
                .CreateProtector(name)
                .Unprotect(value);
        }
        catch (CryptographicException exception)
        {
            // 密钥丢失、密钥环未共享或密文被篡改；拒绝而不是回退到默认值。消息只带设置名，不回显密文
            throw new InvalidOperationException(
                $"Setting '{name}' could not be decrypted with the current Data Protection key ring. " +
                "Check that this process shares the key ring and application name of the process that wrote it.",
                exception);
        }
    }

    // 只有真正读写机密设置时才要求 Data Protection：不定义机密设置的宿主不必配置它
    internal static InvalidOperationException MissingDataProtection(string name) =>
        new($"Setting '{name}' is encrypted, but no {nameof(IDataProtectionProvider)} is registered. " +
            "Call AddDataProtection() with a persistent key ring shared by every process that reads or writes settings.");

    // 作废本作用域的记忆化结果，下次读取重新查存储；由写入入口在写入后调用
    internal void Invalidate() => _snapshots.Clear();

    // 返回解析出的原始值，以及它是否来自存储（而不是代码默认值）
    private static (string? Value, bool Stored) ResolveRaw(
        ISettingDefinition definition,
        Snapshot snapshot,
        IReadOnlyDictionary<string, string> userValues)
    {
        // 进程级设置不与其它层级组合，是独立分支，不接在用户级→租户级的回落链上
        if (definition.Scopes.HasFlag(SettingScopes.Host))
        {
            if (snapshot.HostValues is null)
            {
                throw new HostScopeUnavailableException(definition.Name);
            }

            return snapshot.HostValues.TryGetValue(definition.Name, out var hostValue)
                ? (hostValue, true)
                : (definition.DefaultValue, false);
        }

        if (definition.Scopes.HasFlag(SettingScopes.User)
            && userValues.TryGetValue(definition.Name, out var userValue))
        {
            return (userValue, true);
        }

        if (definition.Scopes.HasFlag(SettingScopes.Tenant)
            && snapshot.TenantValues.TryGetValue(definition.Name, out var tenantValue))
        {
            return (tenantValue, true);
        }

        return (definition.DefaultValue, false);
    }

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.Id?.ToString();
        var key = (currentTenant?.Id, userId);
        if (_snapshots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 几次查询全部成功后再一起发布缓存，避免失败重试读到半初始化状态。
        var tenantValues = await store.GetAllAsync(SettingScopes.Tenant, userId: null, cancellationToken);

        var userValues = userId is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await store.GetAllAsync(SettingScopes.User, userId, cancellationToken);

        // 只有定义了进程级设置且宿主层可达时才查；不可达时 HostValues 保持 null。
        // 单独查一次而不复用租户级结果：两者同一行只是 EfCoreSettingStore 的实现细节。
        var hostValues = store.CanAccessHostScope
                         && definitionManager.GetAll().Any(d => d.Scopes.HasFlag(SettingScopes.Host))
            ? await store.GetAllAsync(SettingScopes.Host, userId: null, cancellationToken)
            : null;

        var snapshot = new Snapshot(tenantValues, userValues, hostValues);
        _snapshots[key] = snapshot;
        return snapshot;
    }

    // 一个租户、一个用户下三层的原始值；宿主层不可达时 HostValues 为 null
    private sealed record Snapshot(
        IReadOnlyDictionary<string, string> TenantValues,
        IReadOnlyDictionary<string, string> UserValues,
        IReadOnlyDictionary<string, string>? HostValues);

    private static T Convert<T>(string value)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (target.IsEnum)
        {
            return (T)Enum.Parse(target, value, ignoreCase: true);
        }

        // TypeDescriptor 覆盖基元类型与带 TypeConverter 的类型；固定用不变文化，结果不随节点区域设置变化
        return (T)TypeDescriptor.GetConverter(target).ConvertFromString(null, CultureInfo.InvariantCulture, value)!;
    }
}
