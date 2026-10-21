using Leistd.ExceptionHandling;
using Leistd.Settings.Errors;

namespace Leistd.Settings.Exceptions;

/// <summary>读写了未定义的设置（名字拼错或漏注册 <c>ISettingDefinitionProvider</c>）。</summary>
/// <remarks>错误码为 <see cref="SettingErrorCodes.Undefined"/>。</remarks>
public class UndefinedSettingException : BusinessException
{
    /// <summary>以未定义的设置名构造。</summary>
    /// <param name="settingName">未定义的设置名。</param>
    public UndefinedSettingException(string settingName)
        : base(SettingErrorCodes.Undefined,
            $"Setting '{settingName}' is not defined. Register an ISettingDefinitionProvider that defines it.")
    {
        SettingName = settingName;
        WithData("Name", settingName);
    }

    /// <summary>未定义的设置名。</summary>
    public string SettingName { get; }
}
