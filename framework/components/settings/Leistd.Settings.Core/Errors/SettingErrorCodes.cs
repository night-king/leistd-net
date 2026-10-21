namespace Leistd.Settings.Errors;

/// <summary>设置组件抛出的错误码，默认译文随包分发，宿主资源里的同名词条优先。</summary>
public static class SettingErrorCodes
{
    /// <summary>设置未定义。占位：<c>Name</c>。</summary>
    public const string Undefined = "Setting:Undefined";

    /// <summary>写入定义未允许的层级。占位：<c>Name</c>。</summary>
    public const string ScopeNotAllowed = "Setting:ScopeNotAllowed";

    /// <summary>空字符串不是合法值，清除用 <see langword="null"/>。占位：<c>Name</c>。</summary>
    public const string EmptyValueRejected = "Setting:EmptyValueRejected";

    /// <summary>布尔设置只接受 <c>true</c> / <c>false</c>。占位：<c>Value</c>。</summary>
    public const string BooleanRequired = "Setting:BooleanRequired";

    /// <summary>整数设置收到非整数。占位：<c>Name</c>。</summary>
    public const string IntegerRequired = "Setting:IntegerRequired";

    /// <summary>整数超出区间。占位：<c>Name</c>、<c>Minimum</c>、<c>Maximum</c>。</summary>
    public const string ValueOutOfRange = "Setting:ValueOutOfRange";

    /// <summary>值不在候选之内。占位：<c>Value</c>、<c>Allowed</c>。</summary>
    public const string ValueNotAllowed = "Setting:ValueNotAllowed";

    /// <summary>设置不存在或不对客户端开放。占位：<c>Name</c>。</summary>
    public const string NotAvailable = "Setting:NotAvailable";

    /// <summary>进程级设置只能在宿主上下文修改。占位：<c>Name</c>。</summary>
    public const string HostOnly = "Setting:HostOnly";

    /// <summary>当前身份不是用户，不能读写个人偏好。</summary>
    public const string IdentityCannotOperate = "Setting:IdentityCannotOperate";
}
