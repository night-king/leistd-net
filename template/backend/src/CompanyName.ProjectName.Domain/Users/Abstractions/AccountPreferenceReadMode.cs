#if (RemoteTokenAuth)
namespace CompanyName.ProjectName.Domain.Users.Abstractions;

/// <summary>读取方式。</summary>
public enum AccountPreferenceReadMode
{
    /// <summary>当前用户的 <c>/me</c>：每次都向 Identity 条件重验证，不加入在途回源。</summary>
    CurrentUser,

    /// <summary>服务端产出文本（收件人的语言与时区等）：新鲜期内用缓存，并发读取合并回源。</summary>
    Server,
}
#endif
