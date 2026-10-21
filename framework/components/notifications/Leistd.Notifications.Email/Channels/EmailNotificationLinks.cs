namespace Leistd.Notifications.Email.Channels;

// 邮件里附哪个链接：绝对 http(s) 原样附上；以 / 开头的站内链接在配置了站点地址时拼成绝对地址；其余不附
internal static class EmailNotificationLinks
{
    public static string? Resolve(string? link, string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        if (IsHttpUrl(link))
        {
            return link;
        }

        // "//host" 是协议相对的外部地址，不是站内链接
        if (!string.IsNullOrWhiteSpace(publicBaseUrl) && link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal))
        {
            return publicBaseUrl.TrimEnd('/') + link;
        }

        return null;
    }

    public static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
