#if (LocalIdentity)
#if (Email)
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
#if (Email)
using Leistd.Redaction;
using Leistd.Security.OneTimeCodes.VerificationCodes;
using Leistd.Settings.Resolution;
using Microsoft.Extensions.Options;
#endif

namespace CompanyName.ProjectName.Application.Auth.TwoFactor;

/// <summary>本人此刻登录第二步能用的方式与默认方式。</summary>
/// <param name="Methods">能用的方式，按系统顺序（身份验证器、邮箱验证码），恢复码排在最后。</param>
/// <param name="DefaultMethod">进入第二步时直接给出的方式；为 null 即不需要第二步。</param>
/// <param name="MaskedEmail">邮箱验证码能用时，收码地址的打码形式。</param>
internal sealed record TwoFactorMethodOptions(
    IReadOnlyList<TwoFactorMethod> Methods,
    TwoFactorMethod? DefaultMethod,
    string? MaskedEmail)
{
    /// <summary>登录要第二步。</summary>
    public bool RequiresSecondStep => DefaultMethod is not null;

    /// <summary>这种方式此刻能用。</summary>
    public bool Allows(TwoFactorMethod method) => Methods.Contains(method);
}

/// <summary>按用户的设置与所在组织的策略判定第二步方式：登录第一步、发码、校验与个人设置共用这一处。</summary>
/// <remarks>
/// <para>默认方式：本人指定的首选仍能用就取它，否则按 身份验证器 → 邮箱验证码 的顺序取第一个能用的；恢复码永不作默认。</para>
/// <para>邮箱验证码要同时满足：本人开启、邮箱已验证、组织允许（<c>Security.AllowEmailTwoFactor</c>）、验证码摘要密钥可用。
/// 任何一项不满足时它就不是一种可用方式：只开了它的人回到单步登录，个人设置里给出原因。</para>
/// </remarks>
internal sealed class TwoFactorMethodResolver
#if (Email)
    (ISettingProvider settingProvider, IOptions<VerificationCodeOptions> verificationCodeOptions)
#endif
{
    /// <summary>判定 <paramref name="user"/> 此刻的第二步方式。</summary>
#if (Email)
    public async Task<TwoFactorMethodOptions> ResolveAsync(User user, CancellationToken cancellationToken = default) =>
        Build(user, user.EmailTwoFactorEnabled && await GetEmailUnavailableReasonAsync(user, cancellationToken) is null);
#else
    public Task<TwoFactorMethodOptions> ResolveAsync(User user, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build(user, emailUsable: false));
#endif

    private static TwoFactorMethodOptions Build(User user, bool emailUsable)
    {
        var methods = new List<TwoFactorMethod>();
        if (user.TwoFactorEnabled)
        {
            methods.Add(TwoFactorMethod.Authenticator);
        }

        string? maskedEmail = null;
        if (emailUsable)
        {
            methods.Add(TwoFactorMethod.Email);
#if (Email)
            maskedEmail = TextRedactor.RedactEmail(user.Email);
#endif
        }

        TwoFactorMethod? defaultMethod = user.PreferredTwoFactorMethod is { } preferred && methods.Contains(preferred)
            ? preferred
            : methods.Count > 0 ? methods[0] : null;

        if (user.TwoFactorEnabled && user.RecoveryCodesLeft > 0)
        {
            methods.Add(TwoFactorMethod.RecoveryCode);
        }

        return new TwoFactorMethodOptions(methods, defaultMethod, maskedEmail);
    }
#if (Email)

    /// <summary>邮箱验证码为什么用不了；能用（或能开启）时为 null。不看本人是否已开启。</summary>
    public async Task<TwoFactorEmailUnavailableReason?> GetEmailUnavailableReasonAsync(
        User user,
        CancellationToken cancellationToken = default)
    {
        if (!await settingProvider.GetAsync<bool>(SettingConstant.Security.AllowEmailTwoFactor, cancellationToken))
            return TwoFactorEmailUnavailableReason.NotAllowed;

        if (!verificationCodeOptions.Value.IsKeyUsable)
            return TwoFactorEmailUnavailableReason.DeliveryUnavailable;

        return user.EmailConfirmed ? null : TwoFactorEmailUnavailableReason.EmailNotVerified;
    }
#endif
}
#endif
