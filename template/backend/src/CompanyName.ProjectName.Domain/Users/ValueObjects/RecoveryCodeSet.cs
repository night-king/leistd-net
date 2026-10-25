#if (LocalIdentity)
using Leistd.Ddd.Domain.Values;

namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>账号的恢复码：尚未使用的恢复码摘要。</summary>
/// <remarks>
/// 开启第一个独立因子（身份验证器或通行密钥）时发放，最后一个独立因子去掉时作废；与具体哪种方式无关，
/// 丢了手机或通行密钥的人凭它完成第二步。一个用户至多十个、用过即删，不值得为它单开一张表。
/// </remarks>
public sealed class RecoveryCodeSet : ValueObject
{
    private const char Separator = ';';

    /// <summary>尚未使用的恢复码摘要，以 <c>;</c> 分隔；用完为空串。</summary>
    public string Hashes { get; private set; }

    private RecoveryCodeSet()
    {
        Hashes = null!;
    }

    private RecoveryCodeSet(string hashes)
    {
        Hashes = hashes;
    }

    /// <param name="hashes">恢复码摘要。</param>
    public static RecoveryCodeSet Create(IEnumerable<string> hashes) => new(string.Join(Separator, hashes));

    /// <summary>剩余可用的个数。</summary>
    public int Count => Split().Count;

    /// <summary>用掉一个；摘要不在剩余列表里时返回 null。</summary>
    public RecoveryCodeSet? Consume(string hash)
    {
        var hashes = Split();
        return hashes.Remove(hash) ? new RecoveryCodeSet(string.Join(Separator, hashes)) : null;
    }

    private List<string> Split() => [.. Hashes.Split(Separator, StringSplitOptions.RemoveEmptyEntries)];

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return Hashes;
    }
}
#endif
