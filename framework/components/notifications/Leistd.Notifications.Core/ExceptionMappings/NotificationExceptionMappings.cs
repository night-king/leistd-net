using System.Net;
using Leistd.ExceptionHandling.Options;
using Leistd.Notifications.Errors;

namespace Leistd.Notifications.ExceptionMappings;

// 通知组件的非默认 HTTP 错误语义，由 AddNotifications 登记；宿主用 MapCode 覆盖，与调用顺序无关。
internal static class NotificationExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapDefaultCode(NotificationErrorCodes.IdentityCannotOperate, (int)HttpStatusCode.Forbidden);
    }
}
