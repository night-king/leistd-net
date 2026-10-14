using StackExchange.Redis;

namespace Leistd.Lock.Tests.Redis;

/// <summary>真实 Redis 的可达性判定与共享连接。</summary>
/// <remarks>
/// <para>默认连 <c>127.0.0.1:6379</c>，可用 <c>LEISTD_TEST_REDIS</c> 覆盖。
/// 连得上就跑，连不上就整类跳过——仓库的提交前自检
/// （<c>dotnet test framework/Leistd.Framework.slnx</c>）必须在没装 Redis 的机器上也能全绿。</para>
/// <para><b>跳过是有代价的，代价由 CI 承担</b>：CI 用 <c>services: redis</c> 提供服务，
/// 并在跑测试前显式 ping 一次。少了服务时 CI 那一步直接失败，
/// 而不是让这一批用例悄无声息地跳过去。</para>
/// </remarks>
internal static class RedisServer
{
    public const string ConnectionEnvironmentVariable = "LEISTD_TEST_REDIS";
    private const string DefaultConnection = "127.0.0.1:6379";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) is { Length: > 0 } value
            ? value
            : DefaultConnection;

    public static string SkipReason =>
        $"没有可用的 Redis（{ConnectionString}）。启一个即可跑这一批：" +
        $"docker run -d -p 6379:6379 redis:8-alpine，或设 {ConnectionEnvironmentVariable}。";

    private static readonly Lazy<IConnectionMultiplexer?> Shared = new(() =>
    {
        try
        {
            var options = ConfigurationOptions.Parse(ConnectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 1000;
            var multiplexer = ConnectionMultiplexer.Connect(options);
            if (!multiplexer.IsConnected)
            {
                multiplexer.Dispose();
                return null;
            }

            // IsConnected 只说明握手完成；真正发一条命令才能确认服务可用
            multiplexer.GetDatabase().Ping();
            return multiplexer;
        }
        catch
        {
            return null;
        }
    });

    public static bool IsAvailable => Shared.Value is not null;

    public static IConnectionMultiplexer Connection =>
        Shared.Value ?? throw new InvalidOperationException(SkipReason);
}
