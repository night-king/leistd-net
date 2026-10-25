#if (LocalIdentity)
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CompanyName.ProjectName.Application.Auth.Passkeys;

/// <summary>入参里浏览器交回的通行密钥凭据（创建结果或断言）：校验、选分支与转成原文共用这一个判据。</summary>
/// <remarks>
/// 字段非空就算出示了；出示了就必须是 JSON 对象。若只把对象计为"出示"，<c>[]</c>、字符串这类值会在校验里被当成没给，
/// 却在分支里被当成给了：既绕开"恰好一种"，又可能变成一份空证明，让近期会话代替了本该当场出示的因子。
/// </remarks>
internal static class PasskeyCredentialJson
{
    /// <summary>出示了凭据（不论形状是否正确）。</summary>
    public static bool IsPresented(JsonElement? credential) => credential.HasValue;

    /// <summary>出示了却不是 JSON 对象时给出字段错误（文案是本地化键）。</summary>
    public static IEnumerable<ValidationResult> ValidateShape(JsonElement? credential, string member)
    {
        if (credential is { ValueKind: not JsonValueKind.Object })
        {
            yield return new ValidationResult("The passkey response must be the credential returned by the browser.", [member]);
        }
    }

    /// <summary>凭据的 JSON 原文；没出示时为 <c>null</c>。</summary>
    public static string? RawText(JsonElement? credential) => credential?.GetRawText();
}
#endif
