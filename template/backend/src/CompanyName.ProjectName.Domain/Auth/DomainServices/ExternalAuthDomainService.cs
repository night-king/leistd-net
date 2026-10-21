#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.Timing;
using Leistd.ExceptionHandling;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.ValueObjects;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Auditing.Abstractions;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Domain.Auth.DomainServices;

/// <summary>外部认证领域服务：外部身份与本地用户的匹配、绑定与解绑。</summary>
/// <remarks>
/// 依赖 <see cref="UserDomainService"/> 只为复用建号（用户名生成、查重与头像校验）：外部登录不另写一套建用户的出口。
/// 用户聚合只读，不在这里修改。
/// </remarks>
public class ExternalAuthDomainService(
    IRepository<User, Guid> userRepository,
    UserDomainService userDomainService,
    IRepository<ExternalLoginConnection, Guid> externalLoginConnectionRepository,
    IDataFilter dataFilter,
    IClock clock,
    ILogger<ExternalAuthDomainService> logger)
{
    /// <summary>查找或创建外部登录用户。</summary>
    /// <returns>
    /// 用户；以及本次是否新建了用户。新建的用户还没有任何角色：默认角色属于入口相关的分配，由应用层完成。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 按邮箱关联已有用户要求两边都已验证：提供商确认邮箱属于这个外部账号，且本地账号的邮箱也已确认。
    /// 否则任何人在提供商那里填上别人的邮箱（或抢先用别人的邮箱在本地注册）就能接管对方账号。
    /// 不满足时邮箱已被占用则拒绝，由用户先登录原账号、再在账号设置里绑定。
    /// 新建账号只采用已验证的邮箱（并记为已确认），未验证的换成占位地址。
    /// </para>
    /// </remarks>
    public async Task<(User User, bool Created)> FindOrCreateUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default)
    {
        // 先按外部连接查找，再用邮箱关联已有用户（两边都已验证才关联）。
        var connection = await FindConnectionAsync(provider, externalUserInfo, cancellationToken);

        if (connection != null)
        {
            var existingUser = await userRepository.GetByIdAsync(connection.UserId, cancellationToken);
            if (existingUser == null)
            {
                throw new BusinessException(UserErrorCodes.NotFound, $"User {connection.UserId} not found.")
                    .WithData("Id", connection.UserId);
            }

            logger.LogInformation("User {Username} signed in via {Provider}", existingUser.Username, provider);
            return (existingUser, false);
        }

        var created = false;
        var user = await FindByVerifiedEmailAsync(provider, externalUserInfo, cancellationToken);

        if (user == null)
        {
            // 未验证的邮箱不写进账号：写进去就占用了别人的地址，本人随后注册、登录都会被挡，
            // 找回时接手的还是挂着对方外部绑定的账号。原始邮箱只留在外部连接上
            var emailVerified = externalUserInfo.EmailVerified && !string.IsNullOrEmpty(externalUserInfo.Email);

            // 用户名不取提供商给的账号标签：Google 那边标签就是邮箱，拿它当用户名会把半个联系方式
            // 变成公开标识符，而且不同域的同名用户会撞上 Username 的唯一索引。
            // 基底优先取提供商的公开句柄（GitHub 的 login），没有句柄时用显示名派生。
            // 占位地址按构造唯一，不由任何业务值派生：用户名可以改、外部连接可以解绑，
            // 而用户行连同它的邮箱一直在（软删除也还占着唯一索引）。
            // 由用户名或 providerId 派生都会让下一个人算出同一个地址，撞上 Email 的唯一索引
            user = await userDomainService.CreateExternalAsync(
                externalUserInfo.SuggestedUsername ?? externalUserInfo.DisplayName,
                emailVerified ? externalUserInfo.Email! : PlaceholderEmail(provider),
                // 提供商已确认：本人再用别的提供商（同一已验证邮箱）登录时能关联回来
                emailConfirmed: emailVerified,
                externalUserInfo.DisplayName,
                externalUserInfo.AvatarUrl,
                cancellationToken);
            logger.LogInformation("Created a new user via {Provider}: {Username}", provider, user.Username);
            created = true;
        }

        var newConnection = new ExternalLoginConnection(
            userId: user.Id,
            provider: provider,
            providerUserId: externalUserInfo.ProviderId,
            profile: ProfileOf(externalUserInfo));
        await externalLoginConnectionRepository.InsertAsync(newConnection, cancellationToken);

        logger.LogInformation("Linked user {Username} to a {Provider} login connection", user.Username, provider);

        return (user, created);
    }

    /// <summary>只读地找出这次外部登录对应的已有用户：已绑定的连接，或两边都已验证的同一邮箱；都没有时返回 null。</summary>
    /// <remarks>
    /// 不写任何东西：应用层先用它认出账号，再在该用户的凭据锁内调用 <see cref="FindOrCreateUserAsync"/>
    /// 按最新状态重新判定并写入关联。邮箱命中被删用户或未双方验证时与 <see cref="FindOrCreateUserAsync"/> 同样拒绝。
    /// </remarks>
    public async Task<Guid?> LocateUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default)
    {
        if (await FindConnectionAsync(provider, externalUserInfo, cancellationToken) is { } connection)
            return connection.UserId;

        return (await FindByVerifiedEmailAsync(provider, externalUserInfo, cancellationToken))?.Id;
    }

    private Task<ExternalLoginConnection?> FindConnectionAsync(
        string provider, ExternalUserInfo externalUserInfo, CancellationToken cancellationToken) =>
        externalLoginConnectionRepository.GetFirstAsync(
            c => c.Provider == provider && c.ProviderUserId == externalUserInfo.ProviderId,
            q => q.OrderBy(c => c.Id),
            cancellationToken);

    // 按邮箱找可以自动关联的已有用户：被删用户与未双方验证时拒绝，没有同邮箱用户时返回 null
    private async Task<User?> FindByVerifiedEmailAsync(
        string provider, ExternalUserInfo externalUserInfo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(externalUserInfo.Email))
            return null;

        User? user;
        // 查找要看见被软删除的行：Email 的唯一索引没有排除 IsDeleted，被删用户仍然占着地址。
        // 只按未删除的行查，这里会判定"没人用过"，随后带着同一个地址建号，落库撞唯一索引返回 500
        using (dataFilter.Disable<ISoftDelete>())
        {
            user = await userRepository.GetFirstAsync(
                u => u.Email == externalUserInfo.Email,
                q => q.OrderBy(u => u.Id),
                cancellationToken);
        }

        // 被删用户不参与自动关联：登不进去的账号接不了这个外部身份，
        // 但它的地址还占着，也不能拿同一个地址另建一个
        if (user is { IsDeleted: true })
        {
            // 用专用错误码而不是 UserErrorCodes.EmailTaken：本地化形态下词条会整条替换这里的消息，
            // 而 EmailTaken 的词条只说"已被使用"，既丢掉"账号已删除"这层信息，又带一个要回显地址的占位符
            throw new BusinessException(
                    ExternalAuthErrorCodes.EmailOwnedByDeletedAccount,
                    "This email belongs to a deleted account. Ask an administrator to restore it, or use another address.")
                .WithData("Provider", ProviderName(provider, externalUserInfo));
        }

        if (user != null && !(externalUserInfo.EmailVerified && user.EmailConfirmed))
        {
            throw new BusinessException(
                    ExternalAuthErrorCodes.AccountExistsSignInToLink,
                    $"An account with this email already exists. Sign in to it and link {ProviderName(provider, externalUserInfo)} from account settings.")
                .WithData("Provider", ProviderName(provider, externalUserInfo));
        }

        return user;
    }

    /// <summary>把一个外部身份绑定到已登录的用户（"绑定"模式，而不是登录或建号）。</summary>
    /// <remarks>
    /// 一个外部身份只能属于一个用户：已绑在别人名下时拒绝，而不是改绑——
    /// 改绑等于让任何能登录这个外部账号的人把它从原主人那里抢走。
    /// 同一提供商每人只绑一个，界面按提供商展示，多个同类绑定说不清哪个在用。
    /// </remarks>
    public async Task<ExternalLoginConnection> LinkAsync(
        User user,
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default)
    {
        var existing = await externalLoginConnectionRepository.GetFirstAsync(
            c => c.Provider == provider && c.ProviderUserId == externalUserInfo.ProviderId,
            q => q.OrderBy(c => c.Id),
            cancellationToken);
        if (existing is not null && existing.UserId != user.Id)
        {
            throw new BusinessException(ExternalAuthErrorCodes.AlreadyLinked, $"This {ProviderName(provider, externalUserInfo)} account is already linked to another user.")
                .WithData("Provider", ProviderName(provider, externalUserInfo));
        }

        if (existing is not null)
        {
            existing.Sync(ProfileOf(externalUserInfo));
            await externalLoginConnectionRepository.UpdateAsync(existing, cancellationToken);
            return existing;
        }

        if (await externalLoginConnectionRepository.AnyAsync(c => c.UserId == user.Id && c.Provider == provider, cancellationToken))
        {
            throw new BusinessException(ExternalAuthErrorCodes.ProviderAlreadyLinked, $"A {ProviderName(provider, externalUserInfo)} account is already linked. Unlink it first.")
                .WithData("Provider", ProviderName(provider, externalUserInfo));
        }

        var connection = new ExternalLoginConnection(
            userId: user.Id,
            provider: provider,
            providerUserId: externalUserInfo.ProviderId,
            profile: ProfileOf(externalUserInfo));
        await externalLoginConnectionRepository.InsertAsync(connection, cancellationToken);

        logger.LogInformation("User {Username} linked a {Provider} login connection", user.Username, provider);
        return connection;
    }

    /// <summary>解绑用户的一个外部身份。</summary>
    /// <remarks>
    /// 必须还剩一种登录方式（设有密码，或还有别的绑定），否则解绑之后这个账号就再也登不进来了。
    /// 解绑后要轮换用户的安全版本，让此前用这个外部账号完成第一步的登录挑战作废；那是对用户聚合的修改，由应用层在同一工作单元里完成。
    /// </remarks>
    /// <returns>被解绑的连接；不存在或不属于该用户时为 null。</returns>
    public async Task<ExternalLoginConnection?> UnlinkAsync(
        User user,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await externalLoginConnectionRepository.GetByIdAsync(connectionId, cancellationToken);
        if (connection is null || connection.UserId != user.Id)
            return null;

        await EnsureAnotherSignInMethodAsync(user, connectionId, cancellationToken);
        await externalLoginConnectionRepository.DeleteAsync(connection, cancellationToken);
        logger.LogInformation("User {Username} unlinked a {Provider} login connection", user.Username, connection.Provider);
        return connection;
    }

    /// <summary>解绑这个连接之后还剩一种登录方式（设有密码，或还有别的绑定）；否则以 <see cref="ExternalAuthErrorCodes.LastSignInMethod"/> 拒绝。</summary>
    /// <remarks>只读判定：应用层在确认身份之前先问一次，不为一个注定被拒的解绑消耗验证码；<see cref="UnlinkAsync"/> 在写入前再判一次。</remarks>
    public async Task EnsureAnotherSignInMethodAsync(User user, Guid connectionId, CancellationToken cancellationToken = default)
    {
        var otherLinks = await externalLoginConnectionRepository.CountAsync(
            c => c.UserId == user.Id && c.Id != connectionId,
            cancellationToken);
        if (!user.HasLocalPassword && otherLinks == 0)
        {
            throw new BusinessException(ExternalAuthErrorCodes.LastSignInMethod, "This is your only way to sign in. Set a password in your security settings or link another account first.");
        }
    }

    // 邮箱在本地身份形态下必填且租户内唯一，但外部账号可能没有已验证邮箱，只能填占位地址。
    // 它没有读取方（要查"是哪个外部账号"看 ExternalLoginConnection），所以不需要可读、
    // 更不能由业务值派生：用户名能改、外部连接能解绑，派生出来的地址会和残留的用户行撞唯一索引。
    private static string PlaceholderEmail(string provider) =>
        // 提供商只放域名部分：本地部有 64 字节上限（RFC 5321 §4.5.3.1.1），
        // 提供商名称来自业务扩展，长度不由账号规则决定。
        // 域名标签也有 63 字符上限，同样取决于提供商取的名字
        $"{Guid.NewGuid():N}@{provider.ToLowerInvariant()}.local";

    private ExternalProfile ProfileOf(ExternalUserInfo externalUserInfo) =>
        new(clock.Now, externalUserInfo.ProviderAccountLabel, externalUserInfo.Email, externalUserInfo.AvatarUrl);

    // 面向用户的提示用提供商显示名，标识只作回退。
    private static string ProviderName(string provider, ExternalUserInfo externalUserInfo) =>
        string.IsNullOrWhiteSpace(externalUserInfo.ProviderDisplayName) ? provider : externalUserInfo.ProviderDisplayName;
}
#endif
