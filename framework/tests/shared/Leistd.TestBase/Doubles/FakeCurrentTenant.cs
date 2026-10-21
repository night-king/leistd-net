using Leistd.MultiTenancy.Context;

namespace Leistd.TestBase.Doubles;

/// <summary>固定租户上下文：<see cref="ICurrentTenant.Id"/> 恒为构造时给定的值，不支持切换。</summary>
/// <param name="id">固定的租户标识；<see langword="null"/> 表示宿主上下文。</param>
public sealed class FakeCurrentTenant(Guid? id) : ICurrentTenant
{
    /// <inheritdoc/>
    public bool IsAvailable => Id.HasValue;

    /// <inheritdoc/>
    public Guid? Id { get; } = id;

    /// <inheritdoc/>
    public string? Name => null;

    /// <inheritdoc/>
    public IDisposable Change(Guid? id, string? name = null) => throw new NotSupportedException();
}
