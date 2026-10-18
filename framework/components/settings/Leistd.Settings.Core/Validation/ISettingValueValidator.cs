using Leistd.Settings.Definitions;
using Leistd.Settings.Management;

namespace Leistd.Settings.Validation;

/// <summary>业务取值校验：定义上的值元数据表达不了的规则（时区标识、邮箱地址、开启前提）。</summary>
/// <remarks>
/// <para>在 <see cref="ISettingManager"/> 写入前、通过类型、区间与候选校验之后依次调用，每个校验器自己按设置名判断是否适用。</para>
/// <para>清除（值为 <see langword="null"/>）也调用它，<see cref="SettingValueValidationContext.Value"/> 是清除后会生效的明文值
/// （用户层回落到租户层值，否则为代码默认值），跨设置的约束因此不会被一次清除绕过；清除后没有任何值时不调用。</para>
/// <para>不合法时抛带码的 <c>BusinessException</c>，写入随之中止。</para>
/// <para>默认写入入口在按租户取得的锁与本次写入的事务内调用校验器：经 <see cref="Resolution.ISettingProvider"/> 读另一项设置，
/// 读到的是锁内最新已提交的值，跨设置的约束不会被两次并发写入各自用旧值绕过（范围见 <see cref="ISettingManager.SetAsync"/>）。</para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class TimeZoneSettingValidator : ISettingValueValidator
/// {
///     public Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
///     {
///         if (context.Definition.Name == "Display.TimeZone"
///             &amp;&amp; !(TimeZoneInfo.TryFindSystemTimeZoneById(context.Value, out var zone) &amp;&amp; zone.HasIanaId))
///             throw new BusinessException("AppSetting:TimeZoneInvalid", $"'{context.Value}' is not an IANA time zone id.");
///         return Task.CompletedTask;
///     }
/// }
///
/// builder.Services.AddTransient&lt;ISettingValueValidator, TimeZoneSettingValidator&gt;();
/// </code>
/// </example>
public interface ISettingValueValidator
{
    /// <summary>校验一次写入；不合法时抛出。</summary>
    /// <param name="context">写入内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default);
}

/// <summary>一次待校验的写入。</summary>
/// <param name="Definition">设置定义。</param>
/// <param name="Value">要写入的值，非空；清除时为清除后会生效的值。</param>
/// <param name="Scope">写入层级。</param>
/// <param name="UserId">用户级时的用户标识。</param>
public sealed record SettingValueValidationContext(
    ISettingDefinition Definition,
    string Value,
    SettingScopes Scope,
    string? UserId);
