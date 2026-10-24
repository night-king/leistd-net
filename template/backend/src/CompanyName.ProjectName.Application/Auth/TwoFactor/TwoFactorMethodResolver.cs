#if (LocalIdentity)
#if (Email)
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using CompanyName.ProjectName.Domain.Auth.Passkeys;
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
/// <param name="Methods">能用的方式，按系统顺序（通行密钥、身份验证器、邮箱验证码），恢复码排在最后。</param>
/// <param name="DefaultMethod">进入第二步时直接给出的方式；为 null 即不需要第二步，或登录被拒（见 <paramref name="Refusal"/>）。</param>
/// <param name="MaskedEmail">邮箱验证码能用时，收码地址的打码形式。</param>
/// <param name="Refusal">本人开着第二步，此刻却一种方式都用不了时拒绝登录的原因；能登录时为 null。</param>
internal sealed record TwoFactorMethodOptions(
    IReadOnlyList<TwoFactorMethod> Methods,
    TwoFactorMethod? DefaultMethod,
    string? MaskedEmail,
    TwoFactorSignInRefusal? Refusal = null)
{
    /// <summary>登录要第二步。</summary>
    public bool RequiresSecondStep => DefaultMethod is not null;

    /// <summary>这种方式此刻能用。</summary>
    public bool Allows(TwoFactorMethod method) => Methods.Contains(method);
}

/// <summary>本人开着第二步、此刻却完成不了时拒绝登录的原因：不退回单步登录。</summary>
internal enum TwoFactorSignInRefusal
{
    /// <summary>第一步经邮箱通道得来，第二步只认独立因子，本人没有。</summary>
    IndependentFactorRequired,

    /// <summary>只开了邮箱验证码，发码能力此刻不可用（验证码摘要密钥缺失）。</summary>
    DeliveryUnavailable,

    /// <summary>独立因子只有通行密钥、恢复码也用完了，部署此刻没有配置通行密钥的依赖方。</summary>
    PasskeyUnavailable
}

/// <summary>按用户的设置、所在组织的策略与这次登录的来路判定第二步方式：登录第一步、发码、校验与个人设置共用这一处。</summary>
/// <remarks>
/// <para>默认方式：本人指定的首选仍能用就取它，否则按 通行密钥 → 身份验证器 → 邮箱验证码 的顺序取第一个能用的
/// （抗钓鱼的在前）。恢复码只作备选，只有其余方式都用不了（部署没配通行密钥、本人又只有通行密钥）时才作默认。</para>
/// <para>邮箱验证码要同时满足：本人开启、邮箱已验证、组织允许（<c>Security.AllowEmailTwoFactor</c>）、验证码摘要密钥可用。
/// 组织关闭、邮箱改为未验证是管理决定：只开了它的人回到单步登录，个人设置里给出原因。</para>
/// <para>登录时（<see cref="ResolveForSignInAsync"/>）另按 <see cref="TwoFactorMethods.IsIndependent"/> 收口：第一步经邮箱通道得来，
/// 或组织要求两步验证而本人有独立因子时，不给非独立因子。本人开着第二步、此刻一种都用不了时拒绝登录（<see cref="TwoFactorSignInRefusal"/>）。</para>
/// </remarks>
internal sealed class TwoFactorMethodResolver(
#if (Email)
    ISettingProvider settingProvider,
    IOptions<VerificationCodeOptions> verificationCodeOptions,
#endif
    IPasskeyProtocol passkeys)
{
    /// <summary>判定 <paramref name="user"/> 此刻设置了哪些第二步方式、哪些能用：个人设置用，不带登录的来路。</summary>
#if (Email)
    public async Task<TwoFactorMethodOptions> ResolveAsync(User user, CancellationToken cancellationToken = default) =>
        Build(user, user.EmailTwoFactorEnabled && await GetEmailUnavailableReasonAsync(user, cancellationToken) is null);
#else
    public Task<TwoFactorMethodOptions> ResolveAsync(User user, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build(user, emailUsable: false));
#endif

    /// <summary>判定这次登录第二步的方式：第一步、发码与校验都用它，三处结论一致。</summary>
    /// <param name="user">登录用户。</param>
    /// <param name="firstFactorViaEmail">第一步经邮箱通道得来：口令是邮件找回的，或外部登录按邮箱自动关联。</param>
    /// <param name="cancellationToken">取消令牌。</param>
#if (Email)
    public async Task<TwoFactorMethodOptions> ResolveForSignInAsync(
        User user,
        bool firstFactorViaEmail,
        CancellationToken cancellationToken = default)
    {
        var emailReason = user.EmailTwoFactorEnabled ? await GetEmailUnavailableReasonAsync(user, cancellationToken) : null;
        var independentOnly = firstFactorViaEmail ||
            (user.HasIndependentFactor &&
             await settingProvider.GetAsync<bool>(SettingConstant.Security.RequireTwoFactor, cancellationToken));
        var options = Build(user, user.EmailTwoFactorEnabled && emailReason is null && !independentOnly);
        if (options.RequiresSecondStep)
            return options;

        if (user.HasIndependentFactor)
            return options with { Refusal = TwoFactorSignInRefusal.PasskeyUnavailable };

        // 本人开着邮箱验证码，组织也允许、邮箱也已验证，此刻却用不了：拒绝，而不是当作没开第二步
        var expectsEmail = user.EmailTwoFactorEnabled &&
            emailReason is null or TwoFactorEmailUnavailableReason.DeliveryUnavailable;
        if (!expectsEmail)
            return options;

        return options with
        {
            Refusal = independentOnly
                ? TwoFactorSignInRefusal.IndependentFactorRequired
                : TwoFactorSignInRefusal.DeliveryUnavailable
        };
    }
#else
    public Task<TwoFactorMethodOptions> ResolveForSignInAsync(
        User user,
        bool firstFactorViaEmail,
        CancellationToken cancellationToken = default)
    {
        var options = Build(user, emailUsable: false);
        return Task.FromResult(!options.RequiresSecondStep && user.HasIndependentFactor
            ? options with { Refusal = TwoFactorSignInRefusal.PasskeyUnavailable }
            : options);
    }
#endif

    private TwoFactorMethodOptions Build(User user, bool emailUsable)
    {
        var methods = new List<TwoFactorMethod>();
        if (user.PasskeyCount > 0 && passkeys.IsAvailable)
        {
            methods.Add(TwoFactorMethod.Passkey);
        }

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

        if (user.HasIndependentFactor && user.RecoveryCodesLeft > 0)
        {
            methods.Add(TwoFactorMethod.RecoveryCode);
            defaultMethod ??= TwoFactorMethod.RecoveryCode;
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
