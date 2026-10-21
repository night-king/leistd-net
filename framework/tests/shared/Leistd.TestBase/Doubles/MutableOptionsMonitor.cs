using Microsoft.Extensions.Options;

namespace Leistd.TestBase.Doubles;

/// <summary>当前值可写的 <see cref="IOptionsMonitor{TOptions}"/>：用于验证"选项改了之后行为跟着改"。</summary>
/// <remarks>
/// <see cref="OnChange"/> 返回 <c>null</c>（不触发变更通知）。被测代码若依赖变更回调而非
/// 每次读取 <see cref="CurrentValue"/>，用本类会得到假阳性——那种场景需要真实的
/// <c>OptionsMonitor</c> 加 <c>IOptionsChangeTokenSource</c>。
/// </remarks>
public sealed class MutableOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
{
    /// <inheritdoc />
    public TOptions CurrentValue { get; private set; } = currentValue;

    /// <inheritdoc />
    public TOptions Get(string? name) => CurrentValue;

    /// <inheritdoc />
    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;

    /// <summary>替换当前值。</summary>
    public void Set(TOptions value) => CurrentValue = value;
}
