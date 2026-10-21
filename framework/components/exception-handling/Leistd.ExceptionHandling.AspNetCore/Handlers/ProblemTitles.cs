using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Leistd.ExceptionHandling.AspNetCore.Handlers;

// ProblemDetails 标题的取法：按 Title:{状态码} 查本地化词条，缺失时回退到官方的英文状态短语。
// 业务异常与自动模型校验两条路径共用这一处，否则同一个状态码在两条路径上一条跟随语言、一条永远是英文。
// 本地化失败不得覆盖原本要返回的错误，因此查询全程吞异常、回退。
internal static class ProblemTitles
{
    // 未启用本地化、未命中或查询出错时返回 fallback（缺省为状态码的英文短语）。
    public static string Localize(IStringLocalizer? localizer, int statusCode, string? fallback = null)
    {
        fallback ??= Default(statusCode);
        if (localizer is null)
            return fallback;

        try
        {
            var localized = localizer["Title:" + statusCode];
            return localized.ResourceNotFound ? fallback : localized.Value;
        }
        catch
        {
            return fallback;
        }
    }

    // 与标题约定判断"是否为框架默认标题"用同一张官方短语表，两边不会对不上
    private static string Default(int statusCode)
        => ReasonPhrases.GetReasonPhrase(statusCode) is { Length: > 0 } phrase ? phrase : "Error";
}
