using Leistd.ExceptionHandling;
using Leistd.Settings.Definitions;
using Leistd.Settings.Errors;

namespace Leistd.Settings.Exceptions;

/// <summary>把设置写入了定义未允许的层级。</summary>
/// <remarks>错误码为 <see cref="SettingErrorCodes.ScopeNotAllowed"/>。</remarks>
public class SettingScopeNotAllowedException : BusinessException
{
    /// <summary>以设置名与层级构造。</summary>
    /// <param name="settingName">设置名。</param>
    /// <param name="scope">被拒绝的层级。</param>
    /// <param name="allowed">定义允许的层级。</param>
    /// <param name="displayName">给用户看的设置显示名，填入提示的 <c>{Name}</c>；缺省时用设置名。</param>
    public SettingScopeNotAllowedException(
        string settingName,
        SettingScopes scope,
        SettingScopes allowed,
        string? displayName = null)
        : base(SettingErrorCodes.ScopeNotAllowed,
            $"Setting '{displayName ?? settingName}' cannot be written at scope '{scope}'; it allows '{allowed}'.")
    {
        SettingName = settingName;
        Scope = scope;
        Allowed = allowed;
        WithData("Name", displayName ?? settingName);
    }

    /// <summary>设置名。</summary>
    public string SettingName { get; }

    /// <summary>被拒绝的层级。</summary>
    public SettingScopes Scope { get; }

    /// <summary>定义允许的层级。</summary>
    public SettingScopes Allowed { get; }
}
