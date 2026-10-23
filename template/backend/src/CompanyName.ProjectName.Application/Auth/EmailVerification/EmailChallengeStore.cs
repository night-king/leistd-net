using Leistd.MultiTenancy.Extensions;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
using Leistd.Security.OneTimeCodes.VerificationCodes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Policies;
using Leistd.Email.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.ExceptionHandling;
using Leistd.Timing;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.BackgroundJobs.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.Application.Auth.EmailVerification;

/// <summary>
/// 邮箱验证码挑战：签发（含按邮箱限流）与一次性校验。注册、验证已有账号的邮箱与找回口令各用一种用途，互不通用。
/// </summary>
public class EmailChallengeStore(
    IDistributedCache distributedCache,
    IDistributedLock distributedLock,
    IVerificationCodeDigest codeDigest,
    IUserRegistrationPolicyProvider registrationPolicy,
    IEmailSender emailSender,
    ILogger<EmailChallengeStore> logger,
    IClock clock,
    ICurrentTenant currentTenant,
    IBackgroundTaskQueue taskQueue,
    IOptions<VerificationCodeOptions> verificationCodeOptions)
{
    private const string RegistrationPurpose = "registration-email";

    // 已登录用户验证自己当前的邮箱。与注册分开成两种用途：挑战绑定用途，
    // 注册时发出的验证码不能拿来验证已有账号的邮箱，反之亦然。
    private const string AccountEmailPurpose = "account-email";

    // 找回口令：挑战另外绑定目标用户与签发时的安全版本，码对了也要在凭据锁内核对这两项
    private const string PasswordResetPurpose = "password-reset";

    // 登录第二步与开启邮箱方式：与找回口令一样绑定账号与签发时的安全版本，各自一种用途，码不能互换
    private const string TwoFactorSignInPurpose = "two-factor-sign-in";
    private const string TwoFactorEmailSetupPurpose = "two-factor-email-setup";

    /// <summary>同一邮箱（按租户）找回口令发码的上限，按 <see cref="PasswordResetWindow"/> 的固定窗口计。</summary>
    /// <remarks>
    /// 发送间隔之外再加这一道：每个码可试的次数有限，但只靠间隔一天仍能换几千个码来猜。
    /// 窗口从该邮箱本窗口第一次发码起算，到期后重新计数；不是任意滚动 24 小时内的上限。
    /// </remarks>
    public const int PasswordResetLimitPerWindow = 10;

    /// <summary>找回口令发码上限的计数窗口。</summary>
    public static readonly TimeSpan PasswordResetWindow = TimeSpan.FromHours(24);
    private const string CacheKeyPrefix = "MyProject:email-verification";

    /// <summary>为注册签发验证码；调用方已确认功能开启、图形验证码通过且邮箱未被占用。</summary>
    public Task<EmailVerificationChallengeOutputDto> IssueRegistrationAsync(
        string email,
        UserRegistrationPolicy policy,
        CancellationToken cancellationToken = default)
        => IssueChallengeAsync(NormalizeEmail(email), RegistrationPurpose, policy, cancellationToken);

    /// <summary>为已登录用户验证自己当前的邮箱签发验证码。</summary>
    public async Task<EmailVerificationChallengeOutputDto> IssueAccountEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        // 不看"注册要求邮箱验证"开关：那管的是注册流程，验证自己已有的邮箱与它无关。
        // 验证码的有效期、发送间隔与尝试次数沿用同一组设置——它们描述的是验证码本身。
        // 不要求图形验证码：调用方已登录，发送频率仍受同一套按邮箱的限流约束。
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        return await IssueChallengeAsync(NormalizeEmail(email), AccountEmailPurpose, policy, cancellationToken);
    }

    /// <summary>为找回口令签发验证码。</summary>
    /// <remarks>
    /// <para>不论邮箱是否对应一个能找回的账号，都走同一条路：签发挑战、占用发送间隔、计入窗口上限，响应一样。
    /// <paramref name="target"/> 为 null 时挑战照样签发，但码没有发给任何人，也不绑定账号，永远校验不过。</para>
    /// <para>邮件在后台发送（<see cref="PasswordResetCodeMailer"/>），请求不等待 SMTP；发送失败、队列已满都只记日志，
    /// 配额不退回——退回与否本身会泄露这个邮箱对应着账号。</para>
    /// </remarks>
    /// <param name="email">请求里的邮箱。</param>
    /// <param name="target">能找回的账号及其此刻的安全版本；没有时为 null。</param>
    /// <param name="policy">验证码的有效期、发送间隔与尝试次数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EmailVerificationChallengeOutputDto> IssuePasswordResetAsync(
        string email,
        EmailCodeRecipient? target,
        UserRegistrationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        EnsureKeyUsable();

        var scope = GetScope();
        var emailDigest = GetEmailDigest(NormalizeEmail(email));
        var rateKey = GetRateCacheKey(scope, emailDigest);
        var windowKey = GetPasswordResetWindowCacheKey(scope, emailDigest);
        await using var rateLock = await distributedLock.LockAsync(GetRateLockKey(scope, emailDigest), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, rateLock.LockLost);
        var operationToken = lockScope.Token;

        await EnsureSendIntervalElapsedAsync(scope, rateKey, operationToken);
        var now = clock.Now;
        var window = await ReadPasswordResetWindowAsync(windowKey, now, operationToken);
        if (window is { Count: >= PasswordResetLimitPerWindow })
        {
            throw new BusinessException(AuthErrorCodes.EmailCodeSendTooFrequent, "Verification codes are being sent too frequently. Please try again later.");
        }

        var challengeId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var expiresIn = TimeSpan.FromMinutes(policy.EmailCodeExpiryMinutes);
        var expiresAt = now.Add(expiresIn);
        var challenge = new EmailVerificationChallengeState
        {
            Scope = scope,
            Purpose = PasswordResetPurpose,
            EmailDigest = emailDigest,
            CodeHash = codeDigest.Compute(code),
            ExpiresAt = expiresAt,
            RemainingAttempts = policy.EmailCodeMaxAttempts,
            UserId = target?.UserId,
            SecurityStamp = target?.SecurityStamp
        };
        var windowStartedAt = window?.StartedAt ?? now;

        await distributedCache.SetStringAsync(
            rateKey,
            challengeId.ToString("N"),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(policy.EmailCodeSendIntervalSeconds)
            },
            operationToken);
        await distributedCache.SetStringAsync(
            windowKey,
            JsonSerializer.Serialize(new PasswordResetWindowState(windowStartedAt, (window?.Count ?? 0) + 1)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = windowStartedAt + PasswordResetWindow - now },
            operationToken);
        await distributedCache.SetStringAsync(
            GetChallengeCacheKey(challengeId),
            JsonSerializer.Serialize(challenge),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiresIn },
            operationToken);

        if (target is not null)
        {
            var delivery = new PasswordResetCodeDelivery(
                challengeId, target.UserId, target.Email, code, policy.EmailCodeExpiryMinutes, expiresAt);
            if (!taskQueue.TryQueue((services, token) =>
                    new ValueTask(services.GetRequiredService<PasswordResetCodeMailer>().SendAsync(delivery, token))))
            {
                logger.LogWarning(
                    "Password reset code {ChallengeId} for user {UserId} was not sent: the background queue is full",
                    challengeId,
                    target.UserId);
            }
        }

        return new EmailVerificationChallengeOutputDto
        {
            ChallengeId = challengeId,
            ExpiresInSeconds = checked((int)expiresIn.TotalSeconds),
            RetryAfterSeconds = policy.EmailCodeSendIntervalSeconds
        };
    }

    /// <summary>校验找回口令的验证码；通过即作废，返回挑战绑定的账号与安全版本。</summary>
    /// <remarks>返回值只说明码对了：账号是否仍是这一版凭据、是否仍能找回，由调用方在凭据锁内核对。</remarks>
    public async Task<EmailCodeProof?> ValidatePasswordResetAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
    {
        var challenge = await ConsumeChallengeAsync(email, input, PasswordResetPurpose, cancellationToken);
        return ToProof(challenge);
    }

    /// <summary>为登录第二步签发验证码，发往账号当前的已验证邮箱；挑战绑定账号与签发时的安全版本。</summary>
    /// <remarks>按邮箱的发送间隔与注册、找回口令共用；不设找回口令那样的窗口上限：每次发码都要先过口令那一步，
    /// 输错计入账号的登录失败与锁定。</remarks>
    /// <param name="recipient">收码的账号。</param>
    /// <param name="compose">按验证码与有效期写出邮件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EmailVerificationChallengeOutputDto> IssueTwoFactorSignInAsync(
        EmailCodeRecipient recipient,
        EmailCodeComposer compose,
        CancellationToken cancellationToken = default)
    {
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        return await IssueChallengeAsync(
            NormalizeEmail(recipient.Email), TwoFactorSignInPurpose, policy, cancellationToken, recipient, compose);
    }

    /// <summary>校验登录第二步的验证码；通过即作废，返回挑战绑定的账号与安全版本。</summary>
    public async Task<EmailCodeProof?> ValidateTwoFactorSignInAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => ToProof(await ConsumeChallengeAsync(email, input, TwoFactorSignInPurpose, cancellationToken));

    /// <summary>为开启邮箱验证码签发确认码：证明本人此刻收得到这个地址的信。</summary>
    /// <param name="recipient">收码的账号（当前用户）。</param>
    /// <param name="compose">按验证码与有效期写出邮件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EmailVerificationChallengeOutputDto> IssueTwoFactorEmailSetupAsync(
        EmailCodeRecipient recipient,
        EmailCodeComposer compose,
        CancellationToken cancellationToken = default)
    {
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        return await IssueChallengeAsync(
            NormalizeEmail(recipient.Email), TwoFactorEmailSetupPurpose, policy, cancellationToken, recipient, compose);
    }

    /// <summary>校验开启邮箱验证码的确认码；通过即作废，返回挑战绑定的账号与安全版本。</summary>
    public async Task<EmailCodeProof?> ValidateTwoFactorEmailSetupAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => ToProof(await ConsumeChallengeAsync(email, input, TwoFactorEmailSetupPurpose, cancellationToken));

    private static EmailCodeProof? ToProof(EmailVerificationChallengeState? challenge) =>
        challenge is { UserId: { } userId, SecurityStamp: { } securityStamp }
            ? new EmailCodeProof(userId, securityStamp)
            : null;

    private void EnsureKeyUsable()
    {
        // 缺摘要密钥时返回安全的暂不可用提示；具体部署原因由错误码与服务端日志定位。
        if (!verificationCodeOptions.Value.IsKeyUsable)
        {
            logger.LogError(
                "Email verification is unavailable: {Section}:Key is missing or shorter than {MinimumKeyBytes} bytes.",
                VerificationCodeOptions.SectionName, VerificationCodeOptions.MinimumKeyBytes);
            throw new BusinessException(AuthErrorCodes.EmailVerificationUnavailable,
                "Email verification is temporarily unavailable. Please contact your administrator.");
        }
    }

    // 配额键的值是占位那次的挑战 Id；那次发送失败时另写一个按 Id 的失败标记，它的预留随之作废。
    private async Task EnsureSendIntervalElapsedAsync(string scope, string rateKey, CancellationToken cancellationToken)
    {
        var reservation = await distributedCache.GetStringAsync(rateKey, cancellationToken);
        if (!string.IsNullOrEmpty(reservation) &&
            string.IsNullOrEmpty(await distributedCache.GetStringAsync(
                GetFailedReservationCacheKey(scope, reservation), cancellationToken)))
        {
            throw new BusinessException(AuthErrorCodes.EmailCodeSendTooFrequent, "Verification codes are being sent too frequently. Please try again later.");
        }
    }

    // 窗口到期以注入的时钟为准，缓存条目的过期只负责回收
    private async Task<PasswordResetWindowState?> ReadPasswordResetWindowAsync(
        string windowKey,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var json = await distributedCache.GetStringAsync(windowKey, cancellationToken);
        var window = json is null ? null : JsonSerializer.Deserialize<PasswordResetWindowState>(json);
        return window is not null && window.StartedAt + PasswordResetWindow > now ? window : null;
    }

    // recipient 与 compose 只用于绑定账号的用途（登录第二步、开启邮箱方式）：挑战记下账号与安全版本，邮件由调用方写
    private async Task<EmailVerificationChallengeOutputDto> IssueChallengeAsync(
        string normalizedEmail,
        string purpose,
        UserRegistrationPolicy policy,
        CancellationToken cancellationToken,
        EmailCodeRecipient? recipient = null,
        EmailCodeComposer? compose = null)
    {
        EnsureKeyUsable();

        var scope = GetScope();
        var emailDigest = GetEmailDigest(normalizedEmail);
        var rateKey = GetRateCacheKey(scope, emailDigest);
        await using var rateLock = await distributedLock.LockAsync(GetRateLockKey(scope, emailDigest), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, rateLock.LockLost);
        var operationToken = lockScope.Token;

        await EnsureSendIntervalElapsedAsync(scope, rateKey, operationToken);

        var challengeId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var expiresIn = TimeSpan.FromMinutes(policy.EmailCodeExpiryMinutes);
        var challenge = new EmailVerificationChallengeState
        {
            Scope = scope,
            Purpose = purpose,
            EmailDigest = emailDigest,
            CodeHash = codeDigest.Compute(code),
            ExpiresAt = clock.Now.Add(expiresIn),
            RemainingAttempts = policy.EmailCodeMaxAttempts,
            UserId = recipient?.UserId,
            SecurityStamp = recipient?.SecurityStamp
        };
        var challengeKey = GetChallengeCacheKey(challengeId);
        var reservationId = challengeId.ToString("N");
        var sendInterval = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(policy.EmailCodeSendIntervalSeconds)
        };

        // 发信之前先占住发送配额：并发的两个请求不能都发出去。
        await distributedCache.SetStringAsync(rateKey, reservationId, sendInterval, operationToken);
        try
        {
            await distributedCache.SetStringAsync(
                challengeKey,
                JsonSerializer.Serialize(challenge),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiresIn },
                operationToken);

            // 锁只保护上面的预留与挑战：此刻已丢锁就不发；发送开始后丢锁不再中止它，只有请求取消能中止。
            operationToken.ThrowIfCancellationRequested();
            var message = compose is null
                ? new EmailMessage
                {
                    To = normalizedEmail,
                    Subject = purpose == AccountEmailPurpose
                        ? "Email Address Verification Code"
                        : "Account Registration Verification Code",
                    Body = BuildEmailBody(code, policy.EmailCodeExpiryMinutes, purpose),
                }
                : await compose(code, policy.EmailCodeExpiryMinutes, cancellationToken);
            await emailSender.SendAsync(message, cancellationToken);
        }
        catch
        {
            // 发送失败既不留下收不到码的挑战，也不白占发送间隔。只作废本次预留、不删共享配额键：
            // 锁失效后新的持有者可能已经改写了它，删掉就绕过了限频。
            try
            {
                await Task.WhenAll(
                    distributedCache.RemoveAsync(challengeKey, CancellationToken.None),
                    distributedCache.SetStringAsync(
                        GetFailedReservationCacheKey(scope, reservationId), "1", sendInterval, CancellationToken.None));
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Failed to clean up email verification challenge {ChallengeId} after send failure",
                    challengeId);
            }
            throw;
        }

        // 发送器正常返回即投递设施已接受：挑战与配额都保留，之后的取消或丢锁不再回滚。
        return new EmailVerificationChallengeOutputDto
        {
            ChallengeId = challengeId,
            ExpiresInSeconds = checked((int)expiresIn.TotalSeconds),
            RetryAfterSeconds = policy.EmailCodeSendIntervalSeconds
        };
    }

    /// <summary>校验注册验证码；通过即作废。</summary>
    public async Task<bool> ValidateRegistrationAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => await ConsumeChallengeAsync(email, input, RegistrationPurpose, cancellationToken) is not null;

    /// <summary>校验已有账号的邮箱验证码；通过即作废。</summary>
    public async Task<bool> ValidateAccountEmailAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => await ConsumeChallengeAsync(email, input, AccountEmailPurpose, cancellationToken) is not null;

    // 码对且挑战有效时作废它并返回其内容，否则返回 null
    private async Task<EmailVerificationChallengeState?> ConsumeChallengeAsync(
        string email,
        EmailVerificationInputDto input,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) ||
            input.ChallengeId is not { } challengeId ||
            challengeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.Code))
        {
            return null;
        }

        var challengeKey = GetChallengeCacheKey(challengeId);
        await using var challengeLock = await distributedLock.LockAsync(
            GetChallengeLockKey(challengeId),
            cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            challengeLock.LockLost);
        var operationToken = lockScope.Token;

        var serializedChallenge = await distributedCache.GetStringAsync(challengeKey, operationToken);
        if (string.IsNullOrEmpty(serializedChallenge))
        {
            return null;
        }

        EmailVerificationChallengeState? challenge;
        try
        {
            challenge = JsonSerializer.Deserialize<EmailVerificationChallengeState>(serializedChallenge);
        }
        catch (JsonException)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return null;
        }

        if (challenge is null)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return null;
        }

        var emailDigest = GetEmailDigest(NormalizeEmail(email));
        if (!string.Equals(challenge.Scope, GetScope(), StringComparison.Ordinal) ||
            !string.Equals(challenge.Purpose, purpose, StringComparison.Ordinal) ||
            !FixedTimeEquals(challenge.EmailDigest, emailDigest))
        {
            // A request from another tenant/email must not be able to consume or exhaust this challenge.
            return null;
        }

        var remainingLifetime = challenge.ExpiresAt - clock.Now;
        if (remainingLifetime <= TimeSpan.Zero)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return null;
        }

        bool codeMatches;
        try
        {
            codeMatches = codeDigest.Matches(challenge.CodeHash, input.Code.Trim());
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return null;
        }

        if (codeMatches)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return challenge;
        }

        challenge = challenge with { RemainingAttempts = challenge.RemainingAttempts - 1 };
        if (challenge.RemainingAttempts <= 0)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return null;
        }

        await distributedCache.SetStringAsync(
            challengeKey,
            JsonSerializer.Serialize(challenge),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remainingLifetime },
            operationToken);
        return null;
    }

    private static string BuildEmailBody(string code, int expiryMinutes, string purpose)
    {
        var (heading, intro) = purpose == AccountEmailPurpose
            ? ("Email Address Verification Code", "You are verifying the email address of your account. Your verification code is:")
            : ("Account Registration Verification Code", "You are registering an account. Your verification code is:");
        return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
    <div style='background-color: #0f172a; padding: 20px; text-align: center; color: white;'>
        <h2 style='margin: 0;'>{heading}</h2>
    </div>
    <div style='padding: 30px; background-color: #f8fafc; color: #334155;'>
        <p>Hello,</p>
        <p>{intro}</p>
        <div style='margin: 20px 0; text-align: center;'>
            <span style='font-size: 32px; font-weight: bold; letter-spacing: 8px; color: #2563eb;'>{code}</span>
        </div>
        <p style='font-size: 14px; color: #64748b;'>This code will expire in {expiryMinutes} minutes. Please do not share it with anyone.</p>
        <p style='font-size: 14px; color: #64748b; margin-top: 30px;'>If you did not request this, please ignore this email.</p>
    </div>
</div>";
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string GetEmailDigest(string normalizedEmail)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));

    // 与其他按租户隔离的键同一写法（宿主 host:、租户 {Id:N}:），挑战据此拒绝跨租户消费
    private string GetScope() => currentTenant.ScopeKey(CacheKeyPrefix);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string GetChallengeCacheKey(Guid challengeId)
        => $"{CacheKeyPrefix}:challenge:{challengeId:N}";

    private static string GetChallengeLockKey(Guid challengeId)
        => $"{CacheKeyPrefix}:lock:challenge:{challengeId:N}";

    private static string GetRateCacheKey(string scope, string emailDigest)
        => $"{scope}:rate:{emailDigest}";

    private static string GetFailedReservationCacheKey(string scope, string reservationId)
        => $"{scope}:rate-failed:{reservationId}";

    private static string GetRateLockKey(string scope, string emailDigest)
        => $"{scope}:lock:rate:{emailDigest}";

    private static string GetPasswordResetWindowCacheKey(string scope, string emailDigest)
        => $"{scope}:password-reset-window:{emailDigest}";

    // 找回口令发码计数的固定窗口
    private sealed record PasswordResetWindowState(DateTime StartedAt, int Count);

    private sealed record EmailVerificationChallengeState
    {
        public required string Scope { get; init; }

        public required string Purpose { get; init; }

        public required string EmailDigest { get; init; }

        public required string CodeHash { get; init; }

        public required DateTime ExpiresAt { get; init; }

        public required int RemainingAttempts { get; init; }

        /// <summary>绑定账号的用途（找回口令、两步验证）：码发给的账号；收不到码的挑战与其他用途为 null。</summary>
        public Guid? UserId { get; init; }

        /// <summary>绑定账号的用途：签发时账号的安全版本，此后凭据一变挑战即作废。</summary>
        public string? SecurityStamp { get; init; }
    }
}

/// <summary>收验证码的账号（找回口令、两步验证这类绑定账号的用途）。</summary>
/// <param name="UserId">账号 Id。</param>
/// <param name="Email">账号当前的已验证邮箱，验证码发往这里。</param>
/// <param name="SecurityStamp">发码时账号的安全版本。</param>
public sealed record EmailCodeRecipient(Guid UserId, string Email, string SecurityStamp);

/// <summary>绑定账号的验证码已通过：挑战签发给的账号与当时的安全版本。</summary>
/// <param name="UserId">账号 Id。</param>
/// <param name="SecurityStamp">发码时账号的安全版本；与当前不一致说明凭据已经变过，证明作废。</param>
public sealed record EmailCodeProof(Guid UserId, string SecurityStamp);

/// <summary>按验证码与有效期（分钟）写出一封验证码邮件。</summary>
public delegate Task<EmailMessage> EmailCodeComposer(string code, int expiryMinutes, CancellationToken cancellationToken);
