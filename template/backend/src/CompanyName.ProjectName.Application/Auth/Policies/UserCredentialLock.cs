#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>
/// 一个用户登录凭据的分布式锁键：口令、安全版本、邮箱与两步验证凭据，以及凭它们登记的会话。
/// </summary>
/// <remarks>
/// <para>改写这些凭据的路径——改口令、管理员重置口令、本人找回口令、改邮箱与确认邮箱、两步验证的启用、换绑、停用、重发恢复码、
/// 管理员重置两步验证、绑定与解绑外部登录——都先取这把锁，再读用户、改写并在锁内提交。
/// 用户行没有并发标记，少一条路径不取锁，它晚到的写入就会盖掉别人刚提交的结果
/// （实测：重发恢复码的提交晚于管理员重置，两步验证被写回"已启用"）。</para>
/// <para>登记会话的路径——口令登录、登录第二步、已有账号的外部登录——也在锁内按最新的用户状态决定并提交：
/// 否则撤销会话之后还能冒出一个凭旧口令、旧两步验证状态建立的会话，撤销只撤得掉当时已有的那些。</para>
/// <para>声明式 <c>[UnitOfWork]</c> 在方法返回后才提交、晚于锁的释放，这些路径因此用锁内的手动工作单元。
/// 公开是为了部署级的两步验证重置命令（DbMigrator）也取同一把锁。</para>
/// <para>键名沿用这把锁最初只管两步验证时的取法：改了键，滚动升级期间新旧实例会各持一把锁。</para>
/// </remarks>
public static class UserCredentialLock
{
    public static string Key(Guid userId) => $"MyProject:2fa:lock:{userId:N}";
}
#endif
