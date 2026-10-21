using System.Diagnostics;
using Leistd.Disposables;
using Leistd.Tracing.Abstractions;

namespace Leistd.Tracing.Services;

/// <summary>从显式作用域或当前 <see cref="Activity"/> 提供关联标识。</summary>
/// <remarks>
/// 显式 <see cref="Change"/> 的值优先；否则返回当前 Activity 的 32 位十六进制 TraceId。
/// </remarks>
public class CorrelationIdProvider : ICorrelationIdProvider
{
    private readonly AsyncLocal<string?> _currentCorrelationId = new();

    /// <inheritdoc />
    public string? Get()
    {
        var explicitValue = _currentCorrelationId.Value;
        if (!string.IsNullOrEmpty(explicitValue))
        {
            return explicitValue;
        }

        var activity = Activity.Current;
        return activity is null ? null : activity.TraceId.ToHexString();
    }

    /// <inheritdoc />
    public IDisposable Change(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var parent = _currentCorrelationId.Value;
        _currentCorrelationId.Value = correlationId;

        return new DisposeAction(() => _currentCorrelationId.Value = parent);
    }
}
