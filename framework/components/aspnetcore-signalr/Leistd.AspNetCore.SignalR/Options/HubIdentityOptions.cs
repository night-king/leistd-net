namespace Leistd.AspNetCore.SignalR.Options;

/// <summary>Hub 连接主体的解析与有效性复检配置。</summary>
public sealed class HubIdentityOptions
{
    /// <summary>每次 Hub 调用前复评的授权策略名；为 <see langword="null"/> 时使用宿主的默认策略。</summary>
    /// <remarks>ASP.NET Core 只在握手时跑端点策略；复评让账号禁用或锁定对已建立的连接生效。</remarks>
    public string? PolicyName { get; set; }

    /// <summary>两次复评之间的最小间隔；为 <see langword="null"/>（默认）时每次调用都复评。</summary>
    /// <remarks>
    /// <para>高频 Hub（如光标同步）按实测放宽。</para>
    /// <para>只经代码配置（<c>AddSignalRAmbientContext</c> 的委托或 <c>services.Configure</c>），不绑定配置节。</para>
    /// </remarks>
    public TimeSpan? RevalidationInterval { get; set; }
}
