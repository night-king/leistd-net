#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.Policies;

/// <summary>登录失败锁定策略：连续输错 <see cref="MaxFailedAttempts"/> 次后锁定 <see cref="Duration"/>。</summary>
/// <remarks>
/// 锁定按账号计，所以它同时是一个拒绝服务面：知道用户名的人能反复输错把本人锁在外面。
/// 用有期限的锁定兜住这一点——到期自动解除，管理员也可以提前解除；已登录的会话不受影响。
/// </remarks>
/// <param name="MaxFailedAttempts">连续失败多少次后锁定；0 表示不锁定。</param>
/// <param name="Duration">锁定时长。</param>
public readonly record struct LoginLockoutPolicy(int MaxFailedAttempts, TimeSpan Duration)
{
    /// <summary>是否启用锁定。</summary>
    public bool IsEnabled => MaxFailedAttempts > 0;
}
#endif
