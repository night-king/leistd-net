#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.TwoFactor;

/// <summary>
/// 一个用户两步验证凭据（<c>User.TwoFactor</c>）的分布式锁键。
/// </summary>
/// <remarks>
/// 改写凭据的路径——启用、换绑、停用、重发恢复码、登录第二步用掉验证码或恢复码、管理员重置——都先取这把锁，
/// 再读用户、改写并在锁内提交。用户行没有并发标记，少一条路径不取锁，它晚到的整行写入就会盖掉别人刚提交的结果
/// （实测：重发恢复码的提交晚于管理员重置，两步验证被写回"已启用"）。
/// 声明式 <c>[UnitOfWork]</c> 在方法返回后才提交、晚于锁的释放，这些路径因此用锁内的手动工作单元。
/// 公开是为了部署级的两步验证重置命令（DbMigrator）也取同一把锁。
/// </remarks>
public static class TwoFactorCredentialLock
{
    public static string Key(Guid userId) => $"MyProject:2fa:lock:{userId:N}";
}
#endif
