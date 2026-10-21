namespace Leistd.Ddd.Domain.Values;

/// <summary>按值比较、没有标识的值对象基类。</summary>
/// <remarks>
/// 派生类实现 <see cref="GetAtomicValues"/> 返回参与相等判定的分量，基类据此给出
/// <see cref="Equals(object?)"/>、<see cref="GetHashCode"/> 与 <c>==</c>/<c>!=</c>。
/// 类型必须严格相等：派生类型不同的两个值对象永不相等。
/// 何时用它、何时直接用 <c>record</c>，见 ddd-struct 文档。
/// </remarks>
/// <example>
/// <code><![CDATA[
/// public sealed class Money(decimal amount, string currency) : ValueObject
/// {
///     public decimal Amount { get; } = amount;
///     public string Currency { get; } = currency.ToUpperInvariant();
///
///     protected override IEnumerable<object?> GetAtomicValues()
///     {
///         yield return Amount;
///         yield return Currency;
///     }
/// }
/// ]]></code>
/// </example>
public abstract class ValueObject
{
    /// <summary>返回按稳定顺序参与相等性判定的分量。</summary>
    /// <remarks>顺序同时决定相等性与哈希，变更顺序会让已缓存的哈希失效。</remarks>
    protected abstract IEnumerable<object?> GetAtomicValues();

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
        {
            return false;
        }

        return GetAtomicValues().SequenceEqual(((ValueObject)obj).GetAtomicValues());
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());

        foreach (var value in GetAtomicValues())
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <summary>判断两个值对象是否相等。</summary>
    public static bool operator ==(ValueObject? left, ValueObject? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>判断两个值对象是否不相等。</summary>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
