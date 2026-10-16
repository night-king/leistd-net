#if (IncludeNotifications || IncludeRealTime)
using Leistd.Lock.Redis.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>实时推送的跨副本背板：配了 Redis 时接上 SignalR 官方 Redis 背板，没配时保持单节点。</summary>
public static class SignalRBackplaneExtensions
{
    /// <summary>有 <c>ConnectionStrings:Redis</c> 时注册官方 Redis 背板。</summary>
    /// <remarks>
    /// <para>通道前缀取锁前缀的最终值（默认 <c>companyname-projectname:</c>，可由 <c>Leistd:Lock:Redis:KeyPrefix</c> 覆盖）：
    /// Redis 的发布订阅不受数据库编号隔离，共用一个 Redis 的应用与环境前缀必须不同，锁前缀已经按这个维度区分，不另设配置键。
    /// 同一应用的 API 副本与 Worker 必须取到同一个值。</para>
    /// <para>多副本还要求负载均衡对 Hub 路径做会话粘滞；Redis 不可用期间的推送丢失、不补发（见部署文档）。</para>
    /// </remarks>
    public static ISignalRServerBuilder AddMyProjectBackplane(this ISignalRServerBuilder signalR, IConfiguration configuration)
    {
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrEmpty(redis))
        {
            return signalR;
        }

        signalR.AddStackExchangeRedis(redis);
        signalR.Services.AddOptions<RedisOptions>().Configure<IOptions<RedisLockOptions>>((options, lockOptions) =>
            options.Configuration.ChannelPrefix = RedisChannel.Literal(lockOptions.Value.KeyPrefix));
        return signalR;
    }
}
#endif
