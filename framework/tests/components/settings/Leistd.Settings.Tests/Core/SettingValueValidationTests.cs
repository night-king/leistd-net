using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;
using Leistd.ExceptionHandling;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Events;
using Leistd.Settings.Validation;
using Leistd.TestBase.Doubles;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
using Leistd.Settings.Tests.TestDoubles;

namespace Leistd.Settings.Tests.Core;

/// <summary>写入端按定义上的值元数据与业务校验器把关，写入成功后发布变更事件。</summary>
/// <remarks>值域只靠界面约束不住脚本、旧版客户端与迁移数据；非法值一旦落库，之后每个消费方都得自己防御。</remarks>
public class SettingValueValidationTests
{
    [Theory]
    [InlineData("Security.RequireTwoFactor", "yes", SettingErrorCodes.BooleanRequired)]
    [InlineData("Security.RequireTwoFactor", "True", SettingErrorCodes.BooleanRequired)]
    [InlineData("Security.LockoutDurationMinutes", "abc", SettingErrorCodes.IntegerRequired)]
    [InlineData("Security.LockoutDurationMinutes", "0", SettingErrorCodes.ValueOutOfRange)]
    [InlineData("Security.LockoutDurationMinutes", "1441", SettingErrorCodes.ValueOutOfRange)]
    [InlineData("Logging.MinimumLevel", "debug", SettingErrorCodes.ValueNotAllowed)]
    [InlineData("Display.TimeZone", "", SettingErrorCodes.EmptyValueRejected)]
    public async Task A_value_outside_the_declared_domain_is_rejected_with_a_code(string name, string value, string code)
    {
        var (manager, store, _) = Build();

        var error = await Assert.ThrowsAsync<BusinessException>(() => manager.SetAsync(name, value, Scope(name)));

        Assert.Equal(code, error.Code);
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData("Security.RequireTwoFactor", "true")]
    [InlineData("Security.LockoutDurationMinutes", "1")]
    [InlineData("Security.LockoutDurationMinutes", "1440")]
    [InlineData("Logging.MinimumLevel", "Debug")]
    public async Task A_value_inside_the_declared_domain_is_written(string name, string value)
    {
        var (manager, store, _) = Build();

        await manager.SetAsync(name, value, Scope(name));

        Assert.Equal(value, Assert.Single(store.Writes).Value);
    }

    [Fact]
    public async Task Business_validators_run_after_the_metadata_checks()
    {
        var validator = new RejectingValidator("Display.TimeZone", "Mars/Olympus");
        var (manager, store, _) = Build(validator);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => manager.SetAsync("Display.TimeZone", "Mars/Olympus", SettingScopes.User, "u1"));

        Assert.Equal("AppSetting:TimeZoneInvalid", error.Code);
        Assert.Equal(SettingScopes.User, validator.Seen!.Scope);
        Assert.Equal("u1", validator.Seen.UserId);
        Assert.Empty(store.Writes);
    }

    // 清除以回落后的值经过业务校验器：跨设置的约束（如"未读保留不短于已读"）不能被一次清除绕过
    [Fact]
    public async Task Clearing_validates_the_value_that_takes_effect_afterwards()
    {
        var validator = new RejectingValidator("Security.LockoutDurationMinutes", "15");
        var (manager, store, _) = Build(validator);
        store.Tenant["Security.LockoutDurationMinutes"] = "30";

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => manager.SetAsync("Security.LockoutDurationMinutes", null, SettingScopes.Tenant));

        Assert.Equal("AppSetting:TimeZoneInvalid", error.Code);
        Assert.Equal("15", validator.Seen!.Value);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Clearing_a_user_override_validates_the_tenant_value_beneath_it()
    {
        var validator = new RejectingValidator("Display.TimeZone", "never");
        var (manager, store, _) = Build(validator);
        store.Tenant["Display.TimeZone"] = "Asia/Tokyo";
        store.User["Display.TimeZone"] = "Europe/Paris";

        await manager.SetAsync("Display.TimeZone", null, SettingScopes.User, "u1");

        // 被清除的用户覆盖本身不参与判定
        Assert.Equal("Asia/Tokyo", validator.Seen!.Value);
        Assert.Equal(SettingScopes.User, validator.Seen.Scope);
        Assert.Null(Assert.Single(store.Writes).Value);
    }

    [Fact]
    public async Task Clearing_a_user_override_ignores_tenant_rows_the_definition_does_not_allow()
    {
        var validator = new RejectingValidator("Display.Density", "never");
        var (manager, store, _) = Build(validator);
        store.Tenant["Display.Density"] = "tenant-row";

        await manager.SetAsync("Display.Density", null, SettingScopes.User, "u1");

        Assert.Equal("comfortable", validator.Seen!.Value);
    }

    [Fact]
    public async Task Clearing_hands_validators_the_decrypted_tenant_value()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        var validator = new RejectingValidator("Api.Token", "never");
        var (manager, store, _) = Build(dataProtection, validator);
        await manager.SetAsync("Api.Token", "tenant-secret", SettingScopes.Tenant);
        // 存储里落的是密文
        var cipher = Assert.Single(store.Writes).Value!;
        Assert.NotEqual("tenant-secret", cipher);
        store.Tenant["Api.Token"] = cipher;

        await manager.SetAsync("Api.Token", null, SettingScopes.User, "u1");

        Assert.Equal("tenant-secret", validator.Seen!.Value);
        Assert.Equal(SettingScopes.User, validator.Seen.Scope);
    }

    [Fact]
    public async Task Clearing_to_nothing_skips_the_validators()
    {
        var validator = new RejectingValidator("Display.TimeZone", "anything");
        var (manager, store, _) = Build(validator);

        await manager.SetAsync("Display.TimeZone", null, SettingScopes.User, "u1");

        Assert.Null(validator.Seen);
        Assert.Null(Assert.Single(store.Writes).Value);
    }

    [Fact]
    public async Task A_successful_write_publishes_a_change_event_without_the_value()
    {
        var (manager, _, events) = Build();

        await manager.SetAsync("Display.TimeZone", "Asia/Tokyo", SettingScopes.User, "u1");
        await manager.SetAsync("Logging.MinimumLevel", "Debug", SettingScopes.Host);

        Assert.Collection(
            events.Published.Cast<SettingChangedEvent>(),
            e => Assert.Equal(("Display.TimeZone", SettingScopes.User, "u1"), (e.Name, e.Scope, e.UserId)),
            e => Assert.Equal(("Logging.MinimumLevel", SettingScopes.Host, (string?)null), (e.Name, e.Scope, e.UserId)));
    }

    [Fact]
    public async Task A_rejected_write_publishes_nothing()
    {
        var (manager, _, events) = Build();

        await Assert.ThrowsAsync<BusinessException>(() => manager.SetAsync("Security.RequireTwoFactor", "yes", SettingScopes.Tenant));

        Assert.Empty(events.Published);
    }

    private static SettingScopes Scope(string name) => name switch
    {
        "Logging.MinimumLevel" => SettingScopes.Host,
        _ => SettingScopes.Tenant,
    };

    private static (ISettingManager Manager, FakeSettingStore Store, RecordingEventBus Events) Build(
        params ISettingValueValidator[] validators)
        => Build(dataProtection: null, validators);

    private static (ISettingManager Manager, FakeSettingStore Store, RecordingEventBus Events) Build(
        IDataProtectionProvider? dataProtection,
        params ISettingValueValidator[] validators)
    {
        var store = new FakeSettingStore();
        var events = new RecordingEventBus();
        var definitions = new SettingDefinitionManager([new Definitions()]);
        var manager = new DefaultSettingManager(
            definitions,
            store,
            new DefaultSettingProvider(definitions, store, new FakeCurrentUser(Guid.NewGuid()), dataProtection),
            validators,
            events,
            dataProtection);
        return (manager, store, events);
    }

    private sealed class Definitions : ISettingDefinitionProvider
    {
        public void Define(ISettingDefinitionContext context)
        {
            context.Add("Security.RequireTwoFactor", "false").AsBoolean();
            context.Add("Security.LockoutDurationMinutes", "15").AsInteger(1, 1440);
            context.Add("Logging.MinimumLevel", "Information", SettingScopes.Host).WithAllowedValues("Debug", "Information");
            context.Add("Display.TimeZone", scopes: SettingScopes.All);
            context.Add("Display.Density", "comfortable", SettingScopes.User);
            context.Add("Api.Token", scopes: SettingScopes.All).IsEncrypted = true;
        }
    }

    private sealed class RejectingValidator(string name, string value) : ISettingValueValidator
    {
        public SettingValueValidationContext? Seen { get; private set; }

        public Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            Seen = context;
            if (context.Definition.Name == name && context.Value == value)
            {
                throw new BusinessException("AppSetting:TimeZoneInvalid", $"'{value}' is not valid.");
            }

            return Task.CompletedTask;
        }
    }
}
