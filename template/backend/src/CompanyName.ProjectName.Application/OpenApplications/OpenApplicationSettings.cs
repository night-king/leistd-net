#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.OpenApplications;

/// <summary>模板自有的开放应用设置，存于 OpenIddict 应用的 Settings；管理登记与令牌签发共用这一处键名与读法。</summary>
public static class OpenApplicationSettings
{
    /// <summary>会话绑定：此客户端的授权码与刷新令牌依赖签发时的 Identity 会话，会话退出、撤销或空闲到期后不能再换取令牌。</summary>
    /// <remarks>
    /// 这是模板的授权收敛政策，不是 OIDC 后通道登出元数据 <c>backchannel_logout_session_required</c>
    /// （后者只约定 Logout Token 是否带 sid）。浏览器 BFF 类客户端应开启；离线类客户端按需关闭。
    /// </remarks>
    public const string SessionBound = "leistd:session_bound";

    /// <summary>
    /// 读取会话绑定。缺失返回 <c>null</c>（登记早于该设置，需管理员显式选择）；
    /// 无法识别的值按绑定处理，宁可让续期失败也不放宽会话约束。
    /// </summary>
    public static bool? ReadSessionBound(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue(SessionBound, out var value)
            ? !string.Equals(value, "false", StringComparison.Ordinal)
            : null;

    /// <summary>写入会话绑定的规范值。</summary>
    public static string Format(bool sessionBound) => sessionBound ? "true" : "false";
}
#endif
