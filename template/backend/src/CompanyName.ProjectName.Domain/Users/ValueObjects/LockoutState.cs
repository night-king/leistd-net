#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Policies;
using Leistd.Ddd.Domain.Values;

namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>账号锁定状态：是否锁定、截止时间与连续失败次数，三者一起变化。</summary>
/// <remarks>
/// 锁定分两种：登录失败累计触发的临时锁定有截止时间，它只挡新的登录；管理员锁定没有截止时间，已在线的会话也一并失效。
/// 临时锁定到期后库里仍是"已锁定"，直到下一次登录失败或解锁才清掉，判定一律带当前时刻。
/// </remarks>
public sealed class LockoutState : ValueObject
{
    /// <summary>未锁定且没有失败记录。</summary>
    public static LockoutState None { get; } = new(false, null, 0);

    public bool IsLocked { get; private set; }

    /// <summary>锁定截止时间；管理员锁定为 null。</summary>
    public DateTime? End { get; private set; }

    /// <summary>连续登录失败次数；触发锁定时清零，锁定到期后重新给满一轮尝试。</summary>
    public int AccessFailedCount { get; private set; }

    private LockoutState()
    {
    }

    private LockoutState(bool isLocked, DateTime? end, int accessFailedCount)
    {
        IsLocked = isLocked;
        End = end;
        AccessFailedCount = accessFailedCount;
    }

    /// <summary>锁定；不传截止时间即管理员锁定。失败次数保留。</summary>
    public LockoutState LockUntil(DateTime? end) => new(true, end, AccessFailedCount);

    /// <summary>记一次登录失败；累计达到 <paramref name="policy"/> 的阈值时锁定到 <paramref name="now"/> 加锁定时长。</summary>
    /// <remarks>
    /// 锁定时计数清零：锁定到期后重新给满一轮尝试次数。上一轮失败锁定已到期的，先解除再计数，
    /// 否则过期锁定会让界面一直显示"已锁定"。管理员锁定不经这里解除。
    /// </remarks>
    /// <param name="lockedOut">这一次失败触发了锁定。</param>
    public LockoutState RecordFailure(DateTime now, LoginLockoutPolicy policy, out bool lockedOut)
    {
        var current = IsLocked && End is { } end && end <= now ? None : this;
        var failures = current.AccessFailedCount + 1;
        lockedOut = policy.IsEnabled && failures >= policy.MaxFailedAttempts;
        return lockedOut
            ? new LockoutState(true, now + policy.Duration, 0)
            : new LockoutState(current.IsLocked, current.End, failures);
    }

    /// <summary>登录成功：失败次数清零，锁定状态不变。</summary>
    public LockoutState ResetFailures() => new(IsLocked, End, 0);

    /// <summary>此刻处于锁定中（管理员锁定，或截止时间未到的临时锁定）。</summary>
    public bool IsActiveAt(DateTime now) => IsLocked && (End is null || End > now);

    /// <summary>此刻处于登录失败触发的临时锁定中。</summary>
    public bool IsTemporaryAt(DateTime now) => IsLocked && End is { } end && end > now;

    /// <summary>管理员锁定（没有截止时间）。</summary>
    public bool IsAdministrative => IsLocked && End is null;
#if (Email)

    /// <summary>解除登录失败触发的临时锁定并清零失败次数；管理员锁定原样保留。</summary>
    public LockoutState ReleaseTemporary() => IsAdministrative ? this : None;
#endif

    /// <summary>有可解除的内容：已锁定，或留有失败次数。</summary>
    public bool HasAnythingToClear => IsLocked || AccessFailedCount > 0;

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return IsLocked;
        yield return End;
        yield return AccessFailedCount;
    }
}
#endif
