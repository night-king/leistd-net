using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Timing;
using Leistd.ExceptionHandling;
using Leistd.Settings.Validation;

namespace CompanyName.ProjectName.Application.Settings.Validators;

/// <summary>展示时区只接受 IANA 标识，与读取端共用同一套判定，不会出现"写得进去却解析不出来"。</summary>
/// <remarks>用纯值判定而不注入 <see cref="IUserTimeZoneProvider"/>：后者经账户偏好读取依赖设置写入入口，注入会成环。</remarks>
internal sealed class TimeZoneSettingValidator : ISettingValueValidator
{
    public Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
    {
        if (context.Definition.Name == SettingConstant.Display.TimeZone && !UserTimeZoneProvider.TryResolveId(context.Value, out _))
        {
            throw new BusinessException(AppSettingErrorCodes.TimeZoneInvalid, $"'{context.Value}' is not a valid IANA time zone id.")
                .WithData("Value", context.Value);
        }

        return Task.CompletedTask;
    }
}
