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

namespace CompanyName.ProjectName.Application.Auth.EmailVerification;

/// <summary>邮箱验证码挑战：签发（含按邮箱限流）与一次性校验。注册与验证已有账号的邮箱各用一种用途，互不通用。</summary>
public class EmailChallengeStore(
    IDistributedCache distributedCache,
    IDistributedLock distributedLock,
    IVerificationCodeDigest codeDigest,
    IUserRegistrationPolicyProvider registrationPolicy,
    IEmailSender emailSender,
    ILogger<EmailChallengeStore> logger,
    IClock clock,
    ICurrentTenant currentTenant,
    IOptions<VerificationCodeOptions> verificationCodeOptions)
{
    private const string RegistrationPurpose = "registration-email";

    // 已登录用户验证自己当前的邮箱。与注册分开成两种用途：挑战绑定用途，
    // 注册时发出的验证码不能拿来验证已有账号的邮箱，反之亦然。
    private const string AccountEmailPurpose = "account-email";
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

    private async Task<EmailVerificationChallengeOutputDto> IssueChallengeAsync(
        string normalizedEmail,
        string purpose,
        UserRegistrationPolicy policy,
        CancellationToken cancellationToken)
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

        var scope = GetScope();
        var emailDigest = GetEmailDigest(normalizedEmail);
        var rateKey = GetRateCacheKey(scope, emailDigest);
        await using var rateLock = await distributedLock.LockAsync(GetRateLockKey(scope, emailDigest), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, rateLock.LockLost);
        var operationToken = lockScope.Token;

        // 配额键的值是占位那次的挑战 Id；那次发送失败时另写一个按 Id 的失败标记，它的预留随之作废。
        var reservation = await distributedCache.GetStringAsync(rateKey, operationToken);
        if (!string.IsNullOrEmpty(reservation) &&
            string.IsNullOrEmpty(await distributedCache.GetStringAsync(
                GetFailedReservationCacheKey(scope, reservation), operationToken)))
        {
            throw new BusinessException(AuthErrorCodes.EmailCodeSendTooFrequent, "Verification codes are being sent too frequently. Please try again later.");
        }

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
            RemainingAttempts = policy.EmailCodeMaxAttempts
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
            await emailSender.SendAsync(
                new EmailMessage
                {
                    To = normalizedEmail,
                    Subject = purpose == AccountEmailPurpose
                        ? "Email Address Verification Code"
                        : "Account Registration Verification Code",
                    Body = BuildEmailBody(code, policy.EmailCodeExpiryMinutes, purpose),
                },
                cancellationToken);
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
    public Task<bool> ValidateRegistrationAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => ValidateChallengeAsync(email, input, RegistrationPurpose, cancellationToken);

    /// <summary>校验已有账号的邮箱验证码；通过即作废。</summary>
    public Task<bool> ValidateAccountEmailAsync(
        string email,
        EmailVerificationInputDto input,
        CancellationToken cancellationToken = default)
        => ValidateChallengeAsync(email, input, AccountEmailPurpose, cancellationToken);

    private async Task<bool> ValidateChallengeAsync(
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
            return false;
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
            return false;
        }

        EmailVerificationChallengeState? challenge;
        try
        {
            challenge = JsonSerializer.Deserialize<EmailVerificationChallengeState>(serializedChallenge);
        }
        catch (JsonException)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return false;
        }

        if (challenge is null)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return false;
        }

        var emailDigest = GetEmailDigest(NormalizeEmail(email));
        if (!string.Equals(challenge.Scope, GetScope(), StringComparison.Ordinal) ||
            !string.Equals(challenge.Purpose, purpose, StringComparison.Ordinal) ||
            !FixedTimeEquals(challenge.EmailDigest, emailDigest))
        {
            // A request from another tenant/email must not be able to consume or exhaust this challenge.
            return false;
        }

        var remainingLifetime = challenge.ExpiresAt - clock.Now;
        if (remainingLifetime <= TimeSpan.Zero)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return false;
        }

        bool codeMatches;
        try
        {
            codeMatches = codeDigest.Matches(challenge.CodeHash, input.Code.Trim());
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return false;
        }

        if (codeMatches)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return true;
        }

        challenge = challenge with { RemainingAttempts = challenge.RemainingAttempts - 1 };
        if (challenge.RemainingAttempts <= 0)
        {
            await distributedCache.RemoveAsync(challengeKey, operationToken);
            return false;
        }

        await distributedCache.SetStringAsync(
            challengeKey,
            JsonSerializer.Serialize(challenge),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remainingLifetime },
            operationToken);
        return false;
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

    private sealed record EmailVerificationChallengeState
    {
        public required string Scope { get; init; }

        public required string Purpose { get; init; }

        public required string EmailDigest { get; init; }

        public required string CodeHash { get; init; }

        public required DateTime ExpiresAt { get; init; }

        public required int RemainingAttempts { get; init; }
    }
}
