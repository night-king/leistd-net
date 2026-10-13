#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using CompanyName.ProjectName.Domain.Users.Policies;

namespace CompanyName.ProjectName.UnitTests.Domain;

public sealed class UserAccessStatusTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Active_unlocked_user_is_allowed()
    {
        var user = CreateUser();

        Assert.Equal(UserAccessStatus.Allowed, user.GetAccessStatus(Now));
    }

    [Fact]
    public void Disabled_user_is_denied_before_lock_state_is_considered()
    {
        var user = CreateUser();
        user.Disable();
        user.Lock(Now.AddMinutes(5));

        Assert.Equal(UserAccessStatus.Disabled, user.GetAccessStatus(Now));
    }

    [Fact]
    public void Indefinitely_locked_user_is_denied()
    {
        var user = CreateUser();
        user.Lock();

        Assert.Equal(UserAccessStatus.LockedOut, user.GetAccessStatus(Now));
    }

    [Fact]
    public void Timed_lock_uses_the_callers_clock_and_expires_at_its_boundary()
    {
        var user = CreateUser();
        var lockoutEnd = Now.AddMinutes(5);
        user.Lock(lockoutEnd);

        Assert.Equal(UserAccessStatus.LockedOut, user.GetAccessStatus(lockoutEnd.AddTicks(-1)));
        Assert.Equal(UserAccessStatus.Allowed, user.GetAccessStatus(lockoutEnd));
    }

    /// <summary>可翻译成 SQL 的锁定判据与实体上的判定逐例一致：统计里的"锁定中"与列表里的锁定标记不会分叉。</summary>
    [Fact]
    public void Locked_out_query_matches_the_lockout_state_at_every_boundary()
    {
        var unlocked = CreateUser();
        var administratorLocked = CreateUser();
        administratorLocked.Lock();
        var timed = CreateUser();
        timed.Lock(Now.AddMinutes(5));
        var lockedOut = User.LockedOutAt(Now.AddMinutes(5)).Compile();
        var lockedBefore = User.LockedOutAt(Now.AddMinutes(5).AddTicks(-1)).Compile();

        foreach (var user in new[] { unlocked, administratorLocked, timed })
        {
            Assert.Equal(user.Lockout.IsActiveAt(Now.AddMinutes(5)), lockedOut(user));
            Assert.Equal(user.Lockout.IsActiveAt(Now.AddMinutes(5).AddTicks(-1)), lockedBefore(user));
        }

        Assert.True(lockedBefore(timed));
        Assert.False(lockedOut(timed));
    }

    [Fact]
    public void Login_success_records_the_callers_time()
    {
        var user = CreateUser();
        user.RecordAccessFailed(Now, Lockout);

        user.RecordLoginSuccess(Now, "127.0.0.1");

        Assert.Equal(Now, user.LastLogin!.Time);
        Assert.Equal("127.0.0.1", user.LastLogin.Ip);
        Assert.Equal(0, user.Lockout.AccessFailedCount);
    }

    [Fact]
    public void Reaching_the_threshold_locks_temporarily_and_resets_the_count()
    {
        var user = CreateUser();
        for (var i = 1; i < Lockout.MaxFailedAttempts; i++)
        {
            Assert.False(user.RecordAccessFailed(Now, Lockout));
        }

        Assert.True(user.RecordAccessFailed(Now, Lockout));
        Assert.Equal(UserAccessStatus.LockedOut, user.GetAccessStatus(Now));
        Assert.True(user.IsTemporarilyLockedOut(Now));
        Assert.Equal(Now + Lockout.Duration, user.Lockout.End);
        Assert.Equal(0, user.Lockout.AccessFailedCount);
    }

    // 过期锁定留在库里的 IsLocked 要在下一次失败时清掉，否则会一直被当成"已锁定"
    [Fact]
    public void A_failure_after_an_expired_lockout_starts_a_fresh_count()
    {
        var user = CreateUser();
        user.Lock(Now);

        Assert.False(user.RecordAccessFailed(Now.AddMinutes(1), Lockout));
        Assert.False(user.Lockout.IsLocked);
        Assert.Equal(1, user.Lockout.AccessFailedCount);
    }

    [Fact]
    public void A_zero_threshold_never_locks()
    {
        var user = CreateUser();
        var disabled = Lockout with { MaxFailedAttempts = 0 };

        for (var i = 0; i < 20; i++)
        {
            Assert.False(user.RecordAccessFailed(Now, disabled));
        }

        Assert.Equal(UserAccessStatus.Allowed, user.GetAccessStatus(Now));
    }

    // 管理员锁定没有截止时间，不能被当作"失败锁定"放过已在线的会话
    [Fact]
    public void An_administrator_lock_is_not_a_temporary_lockout()
    {
        var user = CreateUser();
        user.Lock();

        Assert.Equal(UserAccessStatus.LockedOut, user.GetAccessStatus(Now));
        Assert.False(user.IsTemporarilyLockedOut(Now));
    }

    [Fact]
    public void Rehash_preserves_the_security_version_but_a_password_change_rotates_it()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        user.RehashPassword("upgraded-hash");
        Assert.Equal("upgraded-hash", user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);

        user.UpdatePasswordHash("changed-password-hash");
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    private static readonly LoginLockoutPolicy Lockout = new(5, TimeSpan.FromMinutes(15));

    private static User CreateUser() =>
        new("access-status-user", "access-status@example.com", "password-hash");
}
#endif
