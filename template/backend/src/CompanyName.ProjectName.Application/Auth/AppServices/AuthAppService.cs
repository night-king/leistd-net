using Leistd.MultiTenancy.Extensions;
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.SignIn;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
#if (Email)
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using Leistd.Security.OneTimeCodes.VerificationCodes;
using Microsoft.Extensions.Options;
#endif
using CompanyName.ProjectName.Domain.Users.Errors;
using CompanyName.ProjectName.Application.Auth.TwoFactor;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.Attributes;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Users.Mappings;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Leistd.ObjectMapping.Abstractions;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Application.AppServices;
using Leistd.EventBus.Abstractions;
using Leistd.Security.Users;
using Microsoft.Extensions.Logging;

using CompanyName.ProjectName.Domain.Users.ValueObjects;
using CompanyName.ProjectName.Domain.Users.Policies;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using Leistd.MultiTenancy.Context;
using Leistd.Timing;
using Leistd.Lock.Abstractions;
using System.Security.Claims;
using Leistd.ExceptionHandling;
using System.Globalization;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

internal sealed class AuthAppService(
    IUserRepository userRepository,
    UserDomainService userDomainService,
    IRoleRepository roleRepository,
    ICurrentUser currentUser,
    ICurrentSessionState currentSession,
    ICaptchaVerifier captchaVerifier,
#if (Email)
    EmailChallengeStore emailChallengeStore,
#endif
    SessionIssuer sessionIssuer,
    UserSessionDomainService userSessionDomainService,
    TwoFactorChallengeStore twoFactorChallengeStore,
    TwoFactorDomainService twoFactorDomainService,
    IOperationRecorder operationRecorder,
    IDistributedCache distributedCache,
    IObjectMapper objectMapper,
    IUserRegistrationPolicyProvider registrationPolicy,
#if (Email)
    IOptions<VerificationCodeOptions> verificationCodeOptions,
#endif
    IReauthenticationGuard reauthenticationGuard,
    IAccessFailureCounter accessFailureCounter,
    ILocalEventBus localEventBus,
    ICurrentTenant currentTenant,
    IClock clock,
    IDistributedLock distributedLock,
    IUnitOfWorkManager unitOfWorkManager,
    ILogger<AuthAppService> logger) : BaseAppService(), IAuthAppService
{
    public async Task<SessionLoginResult> AuthenticateSessionAsync(
        LoginInputDto input,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var result = await userDomainService.ValidateCredentialsAsync(
            input.UsernameOrEmail,
            input.Password,
            now,
            cancellationToken);

        if (result.Status == CredentialValidationStatus.LockedOut)
        {
            // 已在锁定中：不计数也不再记一条，否则锁定期内的每次尝试都是一个匿名刷表的面
            throw SessionIssuer.LockedOut(result.User!, now);
        }

        if (result.Countable)
        {
            // 计数走与再认证同一份实现：独立工作单元提交，不随下面的抛出回滚
            var counted = await accessFailureCounter.CountAsync(result.User!.Id, cancellationToken);
            if (counted is { LockoutTriggered: true, User: { } lockedUser })
            {
                await RecordLockedOutAsync(lockedUser, cancellationToken);
                throw SessionIssuer.LockedOut(lockedUser, now);
            }
        }

        if (result.Status == CredentialValidationStatus.InvalidCredentials)
        {
            // 目标取被尝试的标识，而不是操作人——此刻没有主体。"谁在被试"正是这条记录的价值，
            // 也是"有人在爆破"唯一能被看出来的地方。用户输入受 MaxTargetIdLength 截断保护。
            //
            // 这是一个**匿名写入面**：框架在 RecordDeniedOperationAsync 里把"匿名一律不记"
            // 写成了安全属性，理由是那会让审计表变成不需要凭据的写入面。此处之所以可以记，
            // 是因为攻击面从"整个 API"收敛到了这一个端点，**并且下面按阈值节流**——
            // 全仓没有任何限流（无 AddRateLimiter），不节流就是一个无需凭据即可刷爆审计表的洞。
            var attempts = await TrackFailedLoginAsync(input.UsernameOrEmail, cancellationToken);
            if (ShouldRecordFailedLogin(attempts))
            {
                await operationRecorder.RecordFailedAsync(
                    OperationRecordActions.AuthLoginFailed,
                    OperationTarget.For(input.UsernameOrEmail),
                    OperationRecordAuthorizations.CredentialsPresented,
                    OperationFailure.FromCode(AuthErrorCodes.InvalidCredentials, new Dictionary<string, object?>
                    {
                        ["attempts"] = attempts,
                        ["windowMinutes"] = FailedLoginWindowMinutes
                    }));
            }

            throw new BusinessException(AuthErrorCodes.InvalidCredentials, "The username or password is incorrect.");
        }

        // 口令对了之后，在凭据锁内按最新的用户行决定去向并提交：改口令、重置口令与之串行，
        // 撤销会话之后不会再冒出一个凭旧口令登记的会话。口令校验本身在锁外，持锁时不做昂贵的哈希
        var validatedStamp = result.User!.SecurityStamp;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(result.User.Id), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        // 校验之后、取到锁之前安全版本变了：口令可能已被改掉或重置，按最新的口令再比一次。
        // 只是启用或停用了两步验证（口令没变）时照常往下走，由 StartAsync 按最新状态决定要不要第二步
        var user = await userRepository.GetByIdAsync(result.User.Id, cancellationToken);
        if (user is null || (!user.HasSecurityStamp(validatedStamp) && !userDomainService.VerifyCurrentPassword(user, input.Password)))
        {
            throw new BusinessException(AuthErrorCodes.InvalidCredentials, "The username or password is incorrect.");
        }

        var outcome = await sessionIssuer.StartAsync(user, cancellationToken: cancellationToken);
        if (result.PasswordRehashNeeded)
        {
            userDomainService.RehashPassword(user, input.Password);
            await userRepository.UpdateAsync(user, cancellationToken);
        }

        // 还要第二步时先不记成功：密码对了不等于登录成功，第二步通过时再记（见 CompleteTwoFactorLoginAsync）
        if (outcome.Principal is not null)
        {
            await RecordLoginSucceededAsync(user, cancellationToken);
        }

        await unit.CompleteAsync(cancellationToken);
        return outcome;
    }

    /// <summary>登录第二步：校验验证码或恢复码，通过后签发会话。</summary>
    /// <remarks>
    /// <para>锁定中的账号不校验验证码，与第一步同理：否则锁定期间照样能一个个试。</para>
    /// <para>输错计入账号的登录失败次数：第二步的 10⁶ 空间只靠单个挑战的尝试上限挡不住——
    /// 知道密码的人可以反复走第一步领新挑战。</para>
    /// </remarks>
    public async Task<ClaimsPrincipal> CompleteTwoFactorLoginAsync(
        TwoFactorLoginInputDto input,
        CancellationToken cancellationToken = default)
    {
        var challenge = await twoFactorChallengeStore.GetAsync(input.Token, cancellationToken);
        if (challenge is null || challenge.TenantId != currentTenant.Id)
        {
            throw TwoFactorChallengeExpired();
        }

        // 同一用户的第二步串行执行，与验证码挑战同一做法：输错次数记在缓存里，
        // 并发请求会读到同一个次数，不加锁就能在一个挑战上试出远超上限的次数。
        // 用的是凭据锁：用掉的步与恢复码要写回凭据，不能与重发、停用、重置交错。
        // 本方法没有工作单元，仓储写入立即落库，都发生在锁释放之前。锁内重读挑战
        await using var userLock = await distributedLock.LockAsync(
            UserCredentialLock.Key(challenge.UserId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, userLock.LockLost);
        cancellationToken = lockScope.Token;
        challenge = await twoFactorChallengeStore.GetAsync(input.Token, cancellationToken);
        var user = challenge is not null
            ? await userRepository.GetByIdAsync(challenge.UserId, cancellationToken)
            : null;
        if (challenge is null || user is null)
        {
            throw TwoFactorChallengeExpired();
        }

        // 第一步之后凭据变了（改口令、管理员重置、启用或停用两步验证、解绑外部登录）：
        // 这个挑战凭的是旧凭据，作废。撤销会话挡不住它——挑战不是会话
        if (!user.HasSecurityStamp(challenge.SecurityStamp))
        {
            await twoFactorChallengeStore.RemoveAsync(input.Token, cancellationToken);
            throw TwoFactorChallengeExpired();
        }

        var now = clock.Now;
        try
        {
            SessionIssuer.EnsureAllowed(user, now);
        }
        catch
        {
            await twoFactorChallengeStore.RemoveAsync(input.Token, cancellationToken);
            throw;
        }

        bool verified;
        var usedRecoveryCode = false;
        // 入参 DTO 已保证两者至少有一个；都给了时以恢复码为准
        if (!string.IsNullOrWhiteSpace(input.RecoveryCode))
        {
            verified = usedRecoveryCode = twoFactorDomainService.UseRecoveryCode(user, input.RecoveryCode);
        }
        else
        {
            verified = twoFactorDomainService.VerifyCode(user, input.Code!, now);
        }

        if (!verified)
        {
            // 与口令登录、再认证同一份计数实现：独立工作单元提交，不随下面的抛出回滚
            var counted = await accessFailureCounter.CountAsync(user.Id, cancellationToken);
            if (counted is { LockoutTriggered: true, User: { } lockedUser })
            {
                await twoFactorChallengeStore.RemoveAsync(input.Token, cancellationToken);
                await RecordLockedOutAsync(lockedUser, cancellationToken);
                throw SessionIssuer.LockedOut(lockedUser, now);
            }

            if (!await twoFactorChallengeStore.RecordFailureAsync(input.Token, challenge, cancellationToken))
            {
                throw TwoFactorChallengeExpired();
            }

            throw new BusinessException(AuthErrorCodes.TwoFactorCodeInvalid, "The verification code is incorrect.");
        }

        await twoFactorChallengeStore.RemoveAsync(input.Token, cancellationToken);
        // 用掉的步与恢复码必须落库，否则同一个码还能再用一次
        await userRepository.UpdateAsync(user, cancellationToken);

        if (usedRecoveryCode)
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.AuthTwoFactorRecoveryCodeUsed,
                OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
                OperationRecordAuthorizations.CredentialsPresented,
                cancellationToken);
        }

        await RecordLoginSucceededAsync(user, cancellationToken);
        return await sessionIssuer.SignInAsync(user, cancellationToken: cancellationToken);
    }

    // 登录成功的"什么人"由目标承载：SignInAsync 只往响应里种 Cookie，
    // 本次请求的主体仍是匿名，操作人字段为空是诚实的。
    private Task RecordLoginSucceededAsync(User user, CancellationToken cancellationToken) =>
        operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthLoginSucceeded,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.CredentialsPresented,
            cancellationToken);

    // 登录侧的授权依据是"出示了凭据"：此刻还没有主体，与再认证侧的"本人"不同。
    // 提醒与审计的其余部分两侧一致，收在 IAccessFailureCounter 里。
    private Task RecordLockedOutAsync(User user, CancellationToken cancellationToken)
        => accessFailureCounter.RecordLockoutAsync(
            user, OperationRecordAuthorizations.CredentialsPresented, cancellationToken);

    private static BusinessException TwoFactorChallengeExpired() =>
        new BusinessException(AuthErrorCodes.TwoFactorChallengeExpired, "The sign-in attempt has expired. Sign in again.");

    /// <summary>失败登录的计数窗口（分钟）。</summary>
    private const int FailedLoginWindowMinutes = 5;

    /// <summary>在这些累计次数上落一条记录；之后每 100 次再落一条。</summary>
    /// <remarks>
    /// <b>这是节流，不是"先记后合并"。</b><c>IOperationRecordWriter</c> 刻意没有更新能力
    /// （append-only 是它写在契约里的设计），所以做不到逐次记录再归并。
    /// 阈值写入让首次失败立刻可见、升级过程可见，同时把单个标识的写入量从 O(n) 降到 O(log n)。
    /// <para><b>代价</b>：窗口内非阈值的那些失败不各自成行，精确时刻会丢。
    /// 它能回答"某标识在某窗口内失败了 N 次"，不能回答"第 7 次发生在几点"。
    /// 对"谁在爆破"够用，对逐次取证不够——需要后者时按 CorrelationId 去请求日志里查。</para>
    /// <para>平台统计的"失败登录告警"数的就是这些记录，仪表盘的指标说明写出了这组阈值；改阈值时一并改。</para>
    /// </remarks>
    private static readonly int[] FailedLoginRecordThresholds = [1, 5, 25, 100];

    private static bool ShouldRecordFailedLogin(int attempts)
        => Array.IndexOf(FailedLoginRecordThresholds, attempts) >= 0
           || (attempts > 100 && attempts % 100 == 0);

    /// <summary>累计同一标识在窗口内的失败次数，返回本次之后的累计值。</summary>
    /// <remarks>
    /// <b>刻意不加分布式锁。</b><c>CaptchaAppService</c> 为"读取-消费-比较"的原子性用了锁，
    /// 那里丢一次就是一次可被重放的验证码；这里丢一次递增只会让某个阈值记录晚一点出现，
    /// 不影响"有人在爆破"这个信号。为一个节流计数器去抢分布式锁，代价大于收益。
    /// <para>缓存键对标识做哈希：它是<b>用户输入</b>，直接拼进键会把任意内容带进缓存键空间。</para>
    /// </remarks>
    private async Task<int> TrackFailedLoginAsync(string identifier, CancellationToken cancellationToken)
    {
        var key = FailedLoginCacheKey(identifier);
        var current = await distributedCache.GetStringAsync(key, cancellationToken);
        var attempts = int.TryParse(current, out var parsed) ? parsed + 1 : 1;

        await distributedCache.SetStringAsync(
            key,
            attempts.ToString(CultureInfo.InvariantCulture),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(FailedLoginWindowMinutes)
            },
            cancellationToken);

        return attempts;
    }

    // 按租户隔离：登录名只在租户内唯一，共用一个计数会让一个租户里的爆破把另一个租户的同名用户挡在门外
    private string FailedLoginCacheKey(string identifier)
    {
        var normalized = identifier.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return currentTenant.ScopeKey($"auth:login-failures:{Convert.ToHexString(hash)}");
    }

    public async Task<SecurityConfigOutputDto> GetSecurityConfigAsync(CancellationToken cancellationToken = default)
    {
        // 按租户解析：同一套部署下，不同租户的注册门槛可以不同，
        // 而登录页拿到的必须是它所在那个租户的那一份
        var policy = await registrationPolicy.GetAsync(cancellationToken);

        return new SecurityConfigOutputDto
        {
            RegistrationEnabled = policy.Enabled,
#if (Email)
            EnableEmailVerification = policy.EnableEmailVerification,
            EmailVerificationAvailable = verificationCodeOptions.Value.IsKeyUsable
#endif
        };
    }

    /// <remarks>建用户与分配默认角色必须同生共死：拆开后注册失败会留下没有任何角色的用户。</remarks>
    [UnitOfWork]
    public async Task<UserOutputDto> RegisterAsync(RegisterInputDto input, CancellationToken cancellationToken = default)
    {
        // 关闭自助注册只挡住自己建号；管理员建号与外部登录首次登录不受影响
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        if (!policy.Enabled)
        {
            throw new BusinessException(AuthErrorCodes.RegistrationDisabled, "Self sign-up is not open.");
        }

        // 不与用户名同行记邮箱（哪怕已脱敏）：用户名常常就是邮箱本地部
        // （本地注册时用户自己这么取，外部登录更是按本地部生成），
        // 同一行给出本地部与域名等于把脱敏拼回去。要查地址用操作记录或按用户查库。
        logger.LogInformation("Registering user {Username}", input.Username);

#if (Email)
        // 本次注册当场验证过这个地址才算已确认；关闭邮箱验证时只有图形验证码，地址仍是未验证
        var emailVerified = false;

        if (policy.EnableEmailVerification)
        {
            if (input.EmailVerification?.ChallengeId is null || input.EmailVerification.ChallengeId == Guid.Empty)
            {
                throw new BusinessException(AuthErrorCodes.EmailCodeRequired, "Please enter the email verification code.");
            }

            var isValidEmailCode = await emailChallengeStore.ValidateRegistrationAsync(
                input.Email,
                input.EmailVerification,
                cancellationToken);
            if (!isValidEmailCode)
            {
                throw new BusinessException(AuthErrorCodes.EmailCodeInvalid, "The email verification code is incorrect or has expired.");
            }

            emailVerified = true;
        }
        else
        {
            var isValidCaptcha = await captchaVerifier.VerifyAsync(input.CaptchaToken ?? string.Empty, input.CaptchaCode ?? string.Empty, cancellationToken);
            if (!isValidCaptcha)
            {
                throw new BusinessException(AuthErrorCodes.CaptchaInvalid, "The image captcha is incorrect or has expired.");
            }
        }
#else
        // 没有发信能力：注册只靠图形验证码证明是人在操作，邮箱照常登记但不验证
        var isValidCaptcha = await captchaVerifier.VerifyAsync(input.CaptchaToken ?? string.Empty, input.CaptchaCode ?? string.Empty, cancellationToken);
        if (!isValidCaptcha)
        {
            throw new BusinessException(AuthErrorCodes.CaptchaInvalid, "The image captcha is incorrect or has expired.");
        }
#endif

        var user = await userDomainService.CreateUserAsync(
            input.Username, input.Email, input.Password, input.DisplayName,
            cancellationToken: cancellationToken);
#if (Email)
        // 验证码发到的正是这个地址：与本人确认邮箱同一结论，同邮箱的外部登录与安全邮件据此生效
        if (emailVerified)
        {
            user.ConfirmEmail();
        }

#endif
        var defaultRoles = await roleRepository.GetDefaultRolesAsync(cancellationToken);
        user.AssignRoles(defaultRoles.Select(role => role.Id));
        await userRepository.UpdateAsync(user, cancellationToken);
        // 用刚分配的角色名，不回查：成员关系在本工作单元内尚未落库
        var roleNames = defaultRoles.Select(role => role.Name).ToList();

        logger.LogInformation("User registered (ID: {Id})", user.Id);

        // 跟随本方法的 [UnitOfWork]：注册回滚则记录一并回滚，不留"记了但没发生"的假账。
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthRegistered,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.SelfRegistration,
            cancellationToken);

        return ToOutput(user, roleNames);
    }

    /// <summary>获取当前用户信息。</summary>
    public async Task<UserOutputDto> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var output = await ToOutputAsync(await GetCurrentUserEntityAsync(cancellationToken), cancellationToken);
        // 与服务端拦截读同一份判定（本请求的会话校验结果），界面与接口对受限与否不会各说各的
        return output with { TwoFactorSetupRequired = currentSession.TwoFactorSetupRequired };
    }

    /// <summary>更新个人信息。</summary>
    /// <remarks>
    /// <para>换邮箱要确认是本人（<see cref="IReauthenticationGuard.EnsureRecentAsync"/>）：会话刚认证过，或出示当前口令或验证码。
    /// 邮箱是安全提醒的去处，确认之后还是按邮箱关联外部登录的依据。拿到一个旧会话的人换成自己的地址、验证、
    /// 再用同一邮箱的外部账号登录，就得到一个不依赖这个会话、改口令也撤不掉的登录方式。用户名、显示名与电话不受此限。</para>
    /// <para>在本人的凭据锁内先确认身份（独立提交，验证码用掉不退回），再另开工作单元重读、改并提交：
    /// 与邮箱确认串行，确认的只能是此刻账号上的那个地址。</para>
    /// <para>换了邮箱时提交之后提醒本人（<see cref="EmailChangedEvent"/>）：旧地址取自这次重读的行。</para>
    /// </remarks>
    public async Task<UserOutputDto> UpdateCurrentUserAsync(UpdateCurrentUserInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        // 锁内读到的地址在释放前不会再变；只为决定要不要确认身份，业务阶段另行重读
        var current = await GetCurrentUserEntityAsync(cancellationToken);
        // 与领域判定"换了邮箱"同一口径（大小写不敏感）
        var emailChanged = !string.Equals(current.Email, input.Email, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            await reauthenticationGuard.EnsureRecentAsync(
                new ReauthenticationProof(input.CurrentPassword, input.TwoFactorCode),
                OperationRecordActions.AuthEmailChanged,
                cancellationToken);
        }

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        logger.LogInformation("Updating current user profile (ID: {UserId})", user.Id);
        // 换掉之前的地址与验证状态取自库里的这一行：提醒发给被换掉的地址，不取请求里的任何值
        var previousEmail = user.Email;
        var previousEmailConfirmed = user.EmailConfirmed;

        await userDomainService.UpdateProfileAsync(
            user,
            input.Username,
            input.Email,
            input.DisplayName,
            input.PhoneNumber,
            cancellationToken);

        await userRepository.UpdateAsync(user, cancellationToken);
        if (emailChanged)
        {
            await localEventBus.PublishAsync(
                new EmailChangedEvent(user.Id, previousEmail, previousEmailConfirmed, user.Id, clock.Now),
                cancellationToken);
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.AuthEmailChanged,
                OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
                OperationRecordAuthorizations.AuthenticatedSelf,
                cancellationToken);
        }

        await unit.CompleteAsync(cancellationToken);
        logger.LogInformation("Current user profile updated (ID: {UserId})", user.Id);

        return await ToOutputAsync(user, cancellationToken);
    }

    /// <summary>修改密码，并撤销除当前以外的全部会话。</summary>
    /// <remarks>
    /// 口令与会话撤销同生共死：撤不掉就整体失败，不留"口令换了、旧会话还在"的状态。
    /// 在本人的凭据锁内读、改并提交：同时进行的口令登录、登录第二步要么先登记完会话、被这里一并撤掉，
    /// 要么等这里提交后再按新口令判定，撤销之后不会再冒出凭旧口令登记的会话。
    /// 再认证失败的计数与审计各自独立提交，不随本方法的回滚丢失；提醒在提交之后发出。
    /// </remarks>
    public async Task ChangePasswordAsync(ChangePasswordInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);

        logger.LogInformation("Changing current user password (ID: {UserId})", user.Id);

        // 改口令要再证明一次自己知道当前口令，这就是一次再认证：锁定期内不给试，失败按登录的同一套计数。
        // 少了这一道，持有被盗会话的人能在这个接口上无限次猜当前口令，把登录页的锁定整个绕过去。
        await reauthenticationGuard.EnsureAllowedAsync(user, cancellationToken);

        switch (userDomainService.ChangePassword(user, input.CurrentPassword, input.NewPassword))
        {
            case ChangePasswordStatus.NoLocalPassword:
                // 没有本地口令就没什么可猜的，不计入失败次数
                throw new BusinessException(
                    SecurityErrorCodes.LocalPasswordNotSet,
                    "The current account has no local password set and cannot change the password.");

            case ChangePasswordStatus.CurrentPasswordIncorrect:
                // 这一次恰好把次数用完时，守卫返回的是"已锁定"而不是"口令不正确"
                throw await reauthenticationGuard.RejectAsync(
                    user,
                    OperationRecordActions.AuthPasswordChanged,
                    SecurityErrorCodes.CurrentPasswordIncorrect,
                    "The current password is incorrect.",
                    cancellationToken);
        }

        await userRepository.UpdateAsync(user, cancellationToken);

        // 凭据换了，以旧密码建立的其他会话随之失效；发起修改的这台保留，免得改完密码自己也被踢出去
        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasswordChanged), clock.Now),
            cancellationToken);

        logger.LogInformation("Current user password changed (ID: {UserId})", user.Id);

        // 改自己的密码：此刻主体已建立，操作人字段有值，目标即本人。
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthPasswordChanged,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
        await unit.CompleteAsync(cancellationToken);
    }

#if (ExternalLogin)
    /// <summary>给没有本地口令的账号设置第一个口令，并撤销除当前以外的全部会话。</summary>
    /// <remarks>
    /// <para>与改口令同样的会话去留：发起设置的这台保留，其他会话随之失效；安全版本轮换，待完成的登录挑战作废。</para>
    /// <para>已有口令时先于确认身份拒绝，不消耗验证码。确认身份在本人的凭据锁内独立提交（验证码用掉不退回），
    /// 之后另开工作单元重读用户、设置并提交；提醒在提交之后发出，复用"口令已修改"。</para>
    /// </remarks>
    public async Task SetPasswordAsync(SetPasswordInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;

        (await GetCurrentUserEntityAsync(cancellationToken)).EnsureLocalPasswordNotSet();

        await reauthenticationGuard.EnsureRecentAsync(
            new ReauthenticationProof(null, input.TwoFactorCode),
            OperationRecordActions.AuthPasswordSet,
            cancellationToken);

        using var unit = unitOfWorkManager.Begin(requiresNew: true);
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        userDomainService.SetInitialPassword(user, input.NewPassword);
        await userRepository.UpdateAsync(user, cancellationToken);

        await userSessionDomainService.RevokeAllAsync(user.Id, currentUser.GetSessionId(), cancellationToken);
        await localEventBus.PublishAsync(
            new SecurityAlertRequestedEvent(user.Id, new SecurityAlert(SecurityAlertKind.PasswordChanged), clock.Now),
            cancellationToken);
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthPasswordSet,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
        logger.LogInformation("Current user password set (ID: {UserId})", user.Id);
    }

#endif
    /// <summary>设置或清除自己的头像。</summary>
    /// <remarks>
    /// 本人只能上传图片，入参 DTO 已挡掉外部地址（它来自外部登录提供方，不由本人随手填）；
    /// 浏览器端已裁剪缩放，实体按 <see cref="AvatarPolicy"/> 校验体积与真实类型。
    /// </remarks>
    public async Task<UserOutputDto> SetCurrentUserAvatarAsync(SetAvatarInputDto input, CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        user.SetAvatar(input.Avatar);
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthAvatarChanged,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);

        return await ToOutputAsync(user, cancellationToken);
    }
#if (Email)

    /// <summary>给自己当前的邮箱发验证码。</summary>
    public async Task<EmailVerificationChallengeOutputDto> SendCurrentUserEmailCodeAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetCurrentUserEntityAsync(cancellationToken);
        user.EnsureEmailUnconfirmed();

        return await emailChallengeStore.IssueAccountEmailAsync(user.Email, cancellationToken);
    }

    /// <summary>用验证码确认自己当前的邮箱。</summary>
    /// <remarks>
    /// 校验针对的是<b>此刻</b>账号上的邮箱：发码之后改过邮箱的话，旧验证码对新地址无效，
    /// 不能用它把一个没验证过的新地址标成已验证。为此读、校验与确认都在本人的凭据锁内完成，
    /// 与改邮箱串行——锁外读出旧地址、校验通过后再写"已确认"，会把期间换上的新地址一并标成已验证。
    /// </remarks>
    public async Task<UserOutputDto> ConfirmCurrentUserEmailAsync(EmailVerificationInputDto input, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        await using var held = await distributedLock.LockAsync(UserCredentialLock.Key(userId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.LockLost);
        cancellationToken = lockScope.Token;
        using var unit = unitOfWorkManager.Begin(requiresNew: true);

        var user = await GetCurrentUserEntityAsync(cancellationToken);
        if (!await emailChallengeStore.ValidateAccountEmailAsync(user.Email, input, cancellationToken))
        {
            throw new BusinessException(AuthErrorCodes.EmailCodeInvalid, "The email verification code is incorrect or has expired.");
        }

        user.ConfirmEmail();
        await userRepository.UpdateAsync(user, cancellationToken);

        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthEmailVerified,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);

        await unit.CompleteAsync(cancellationToken);
        return await ToOutputAsync(user, cancellationToken);
    }
#endif

    private async Task<User> GetCurrentUserEntityAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.Id!.Value;
        return await userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new BusinessException(UserErrorCodes.NotFound, $"User {userId} not found.")
                .WithData("Id", userId);
    }

    /// <remarks>
    /// 写路径直接用手里刚改过的实体，不回查用户行；角色不随这些写入变化，按已落库的关联读取。
    /// </remarks>
    private async Task<UserOutputDto> ToOutputAsync(User user, CancellationToken cancellationToken)
    {
        var roleNames = await userRepository.GetRoleNamesAsync(user.Id, cancellationToken);
        return ToOutput(user, roleNames);
    }

    /// <remarks>
    /// 角色名由调用方给出：写路径刚分配完角色、关联行尚未落库，映射配置里的实体连接查不到。
    /// 经 <see cref="UserMappings.RoleNamesKey"/> 传入，仍走已注册的 <c>User → UserOutputDto</c> 映射。
    /// </remarks>
    private UserOutputDto ToOutput(User user, List<string> roleNames)
    {
        return objectMapper.Map<User, UserOutputDto>(
            user,
            new Dictionary<string, object> { [UserMappings.RoleNamesKey] = roleNames });
    }
}
