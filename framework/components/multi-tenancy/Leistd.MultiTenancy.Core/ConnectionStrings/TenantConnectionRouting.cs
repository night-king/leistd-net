namespace Leistd.MultiTenancy.ConnectionStrings;

// “本进程注册了租户连接路由”的显式标记，由本地与远端两个解析入口各放一份，供逐库枚举判断有无独立库可列。
// 不能从 IConnectionStringResolver 或 ITenantDatabaseDirectory 是否注册推断：前者与多租户无关，
// 单库项目也可注册；后者属于控制库存储，不分库的控制面服务同样有它。
internal sealed class TenantConnectionRouting
{
    internal static TenantConnectionRouting Instance { get; } = new();

    private TenantConnectionRouting()
    {
    }
}
