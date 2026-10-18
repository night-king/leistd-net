using System.Globalization;
using Leistd.ExceptionHandling;
using Leistd.Settings.Definitions;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Tests.TestDoubles;
using Leistd.Settings.Validation;
using Leistd.TestBase.Doubles;
using Xunit;

namespace Leistd.Settings.Tests.Core;

/// <summary>写入入口两次作废同一作用域读取器的记忆化：校验前读到最新已提交的值，写入后读到新的生效值。</summary>
/// <remarks>
/// 读取器按作用域记忆化快照。校验前不作废，校验器会拿本作用域早先缓存的另一项旧值做判断；
/// 写入后不作废，校验器读取时缓存的快照（含本项写入前的值）会一直留到作用域结束。
/// </remarks>
public sealed class SettingWriteFreshReadTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>校验器读过本项之后写入或清除，同一读取器立刻读到新的生效值（清除后回落到租户层）。</summary>
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData(null, "fr-FR")]
    public async Task The_same_reader_sees_the_new_effective_value_after_a_validator_read_it(string? value, string expected)
    {
        var store = new InMemorySettingStore();
        await store.SetAsync("Display.Language", "fr-FR", SettingScopes.Tenant, userId: null);
        await store.SetAsync("Display.Language", "ja-JP", SettingScopes.User, UserId.ToString());
        var (manager, provider) = Build(store, reader => new ReadingValidator(reader));

        await manager.SetAsync("Display.Language", value, SettingScopes.User, UserId.ToString());

        Assert.Equal(expected, await provider.GetOrNullAsync("Display.Language"));
    }

    /// <summary>本作用域先读过另一项，随后别的请求提交了新值：校验器按新值判断，而不是本作用域缓存的旧值。</summary>
    [Fact]
    public async Task Validators_read_values_committed_after_the_scope_first_read_them()
    {
        var store = new InMemorySettingStore();
        var (manager, provider) = Build(store, reader => new RetentionOrderValidator(reader));
        Assert.Equal(365, await provider.GetAsync<int>("Retention.UnreadDays"));

        // 另一个请求已经提交了更短的未读保留期
        await store.SetAsync("Retention.UnreadDays", "200", SettingScopes.Tenant, userId: null);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => manager.SetAsync("Retention.ReadDays", "300", SettingScopes.Tenant));
        Assert.Equal("Test:RetentionOrder", error.Code);
        Assert.Null(store.Get("Retention.ReadDays"));
    }

    private static (ISettingManager Manager, ISettingProvider Provider) Build(
        InMemorySettingStore store,
        Func<ISettingProvider, ISettingValueValidator> validator)
    {
        var definitions = new SettingDefinitionManager([new Definitions()]);
        var provider = new DefaultSettingProvider(definitions, store, new FakeCurrentUser(UserId));
        var manager = new DefaultSettingManager(
            definitions,
            store,
            provider,
            [validator(provider)],
            WriteDependencies.Lock(),
            new RecordingUnitOfWorkManager());
        return (manager, provider);
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Display.Language", "zh-CN", SettingScopes.All);
            context.Add("Retention.ReadDays", "90", SettingScopes.Tenant).AsInteger(1, 3650);
            context.Add("Retention.UnreadDays", "365", SettingScopes.Tenant).AsInteger(1, 3650);
        }
    }

    // 读本项的生效值：读取器因此缓存了写入前的快照
    private sealed class ReadingValidator(ISettingProvider settings) : ISettingValueValidator
    {
        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
            => await settings.GetOrNullAsync(context.Definition.Name, cancellationToken);
    }

    private sealed class RetentionOrderValidator(ISettingProvider settings) : ISettingValueValidator
    {
        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            if (context.Definition.Name != "Retention.ReadDays")
            {
                return;
            }

            var read = int.Parse(context.Value, CultureInfo.InvariantCulture);
            var unread = await settings.GetAsync<int>("Retention.UnreadDays", cancellationToken);
            if (unread < read)
            {
                throw new BusinessException("Test:RetentionOrder", $"Unread retention {unread} is shorter than read retention {read}.");
            }
        }
    }
}
