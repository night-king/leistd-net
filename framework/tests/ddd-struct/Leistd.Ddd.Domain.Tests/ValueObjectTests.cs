using Leistd.Ddd.Domain.Values;
using Xunit;

namespace Leistd.Ddd.Domain.Tests;

/// <summary>值对象的相等性契约。基座里少一条断言，下游是 N 个项目各踩一次。</summary>
public class ValueObjectTests
{
    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency.ToUpperInvariant();

        protected override IEnumerable<object?> GetAtomicValues()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    /// <summary>分量完全一致但类型不同：必须不相等。</summary>
    private sealed class Weight(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency.ToUpperInvariant();

        protected override IEnumerable<object?> GetAtomicValues()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    private sealed class Nullable(string? left, string? right) : ValueObject
    {
        protected override IEnumerable<object?> GetAtomicValues()
        {
            yield return left;
            yield return right;
        }
    }

    [Fact]
    public void Same_components_are_equal_and_share_a_hash_code()
    {
        var a = new Money(9.9m, "cny");
        var b = new Money(9.9m, "CNY");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_components_are_not_equal()
    {
        Assert.NotEqual(new Money(1m, "CNY"), new Money(2m, "CNY"));
        Assert.NotEqual(new Money(1m, "CNY"), new Money(1m, "USD"));
    }

    // 类型参与哈希与相等判定：两个分量相同但语义不同的值对象不能互相冒充，
    // 否则把它们放进同一个字典或集合会静默互相覆盖。
    [Fact]
    public void Different_types_with_identical_components_are_never_equal()
    {
        var money = new Money(1m, "CNY");
        var weight = new Weight(1m, "CNY");

        Assert.False(money.Equals(weight));
        Assert.NotEqual(money.GetHashCode(), weight.GetHashCode());
    }

    [Fact]
    public void Null_and_other_types_are_not_equal()
    {
        var money = new Money(1m, "CNY");

        Assert.False(money.Equals(null));
        Assert.False(money.Equals("1 CNY"));
    }

    // 运算符必须能处理 null 两侧，否则 `if (x == null)` 这种常见写法会抛。
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Operators_handle_null_on_either_side(bool leftNull, bool rightNull, bool expected)
    {
        ValueObject? left = leftNull ? null : new Money(1m, "CNY");
        ValueObject? right = rightNull ? null : new Money(1m, "CNY");

        Assert.Equal(expected, left == right);
        Assert.Equal(!expected, left != right);
    }

    // 分量含 null 时不能抛，且 null 参与相等判定——(null, "a") 与 ("a", null) 不同。
    [Fact]
    public void Null_components_participate_in_equality()
    {
        Assert.Equal(new Nullable(null, null), new Nullable(null, null));
        Assert.NotEqual(new Nullable(null, "a"), new Nullable("a", null));
        Assert.NotEqual(new Nullable(null, null), new Nullable(null, "a"));
    }

    // 分量顺序决定哈希：文档明确写了改顺序会让已缓存的哈希失效，这里把它钉住。
    [Fact]
    public void Component_order_affects_the_hash_code()
    {
        Assert.NotEqual(new Nullable("a", "b").GetHashCode(), new Nullable("b", "a").GetHashCode());
    }

    [Fact]
    public void Value_objects_work_as_dictionary_keys()
    {
        var map = new Dictionary<ValueObject, string> { [new Money(1m, "CNY")] = "one" };

        Assert.Equal("one", map[new Money(1m, "cny")]);
        Assert.False(map.ContainsKey(new Weight(1m, "CNY")));
    }
}
