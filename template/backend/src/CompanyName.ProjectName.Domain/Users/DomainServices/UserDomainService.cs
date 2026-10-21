#if (ExternalLogin)
using System.Text;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Domain.Users.Constants;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Policies;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
#endif
using CompanyName.ProjectName.Domain.Users.Errors;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Shared.Security.PasswordHash;
#endif
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Auditing.Abstractions;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Leistd.ExceptionHandling;
#if (LocalIdentity)
using Leistd.MultiTenancy.Context;
#endif

namespace CompanyName.ProjectName.Domain.Users.DomainServices;

/// <summary>用户领域服务。</summary>
public class UserDomainService(
    IRepository<User, Guid> userRepository,
    IDataFilter dataFilter,
#if (LocalIdentity)
    // CreateSuperAdminAsync 与 PromoteToSuperAdmin 用它挡"租户上下文里造超管"，两者只在本地身份形态存在
    ICurrentTenant currentTenant,
    IPasswordHasher passwordHasher,
#endif
    ILogger<UserDomainService> logger)
{
#if (ExternalLogin)
    // 外部登录生成用户名的基底长度上限是可读性取舍：名字再长对识别没有帮助，加上后缀仍远低于 UsernameRules.MaxLength。
    private const int UsernameBaseMaxLength = 24;
    private const int UsernameSuffixAttempts = 5;
    private const string FallbackUsernameBase = "user";

#endif
#if (!LocalIdentity)
    /// <summary>把签发方的主体投影成本地用户行：不存在就建，存在就按令牌刷新资料字段。</summary>
    /// <remarks>
    /// <para>本形态下用户行的主键<b>就是</b>签发方的 <c>sub</c>（见 <see cref="User"/> 的构造函数），
    /// 所以这条投影不可能"对错人"——而让人手填主体标识就可能，且抄错时不报错。</para>
    /// <para><b>资料字段每次刷新，不做本地编辑。</b>用户名、邮箱、显示名归签发方所有；
    /// 本服务没有回写通道，允许本地改只会积累与签发方的漂移。</para>
    /// <para><b>角色与启停不碰。</b>那两样是本服务自己的授权决定，刷新资料时必须原样保留，
    /// 否则每次请求都会把管理员刚做的授权冲掉。</para>
    /// <para>首次访问的并发不在这里处理：撞主键要到冲刷时才抛，本方法内接不到。
    /// 由持有事务边界的调用方重试一次，见 <c>IUserAppService.EnsureCurrentUserProjectedAsync</c>。</para>
    /// </remarks>
    /// <param name="subjectId">签发方主体标识，取自令牌的 <c>sub</c>。</param>
    /// <param name="username">令牌里的用户名；缺失时回落为主体标识，保证非空且可检索。</param>
    /// <param name="email">令牌里的邮箱；缺失时留空串。</param>
    /// <param name="displayName">令牌里的展示名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<User> EnsureProjectedAsync(
        Guid subjectId,
        string? username,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var (name, mail) = ProjectedIdentity(subjectId, username, email);
        // 与新建时同一口径：签发方没给显示名就用用户名。比较与写入用同一个值，否则每个请求都会判成"变了"
        var display = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim();

        var existing = await userRepository.GetByIdAsync(subjectId, cancellationToken);
        if (existing is not null)
        {
            // 没变就不写：这条路径在每个已认证请求上都会走到
            if (existing.Username == name && existing.Email == mail && existing.DisplayName == display)
            {
                return existing;
            }

            existing.ProjectFromIssuer(name, mail, display);
            await userRepository.UpdateAsync(existing, cancellationToken);
            return existing;
        }

        // 这里不包 try/catch：工作单元内 InsertAsync 只登记实体、不访问数据库，
        // 撞主键要到冲刷时才抛，catch 在这里是永不触发的死代码（见工作单元文档）。
        // 并发首访由调用方重试一次收口——它持有事务边界，也只有它能重开一个干净的工作单元。
        var user = new User(subjectId, name, mail, displayName: display);
        await userRepository.InsertAsync(user, cancellationToken);

        logger.LogInformation("Projected issuer subject {SubjectId} into a local user row.", subjectId);
        return user;
    }

    /// <summary>取得主体对应的本地用户行；还没有时建立只含主体标识的最小行，供部署引导在首次访问之前分配角色。</summary>
    /// <remarks>
    /// <para>最小行按投影的回落规则命名（用户名取主体标识、邮箱留空），不编造资料；
    /// 首次真实访问时由 <see cref="EnsureProjectedAsync"/> 按令牌补齐。</para>
    /// <para>已有用户原样返回：资料归签发方，启停归本服务，引导都不改。
    /// 已被删除的用户不在这里恢复：删除是本服务做过的授权决定，只有部署级恢复命令（<c>--grant-admin --restore</c>）显式撤回它。</para>
    /// </remarks>
    /// <param name="subjectId">签发方主体标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户行，以及它是否由本次建立。</returns>
    public async Task<(User User, bool Created)> EnsureSubjectAsync(
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        using (dataFilter.Disable<ISoftDelete>())
        {
            var existing = await userRepository.GetByIdAsync(subjectId, cancellationToken);
            if (existing is { IsDeleted: true })
            {
                throw new InvalidOperationException(
                    $"User {subjectId} has been deleted in this service and cannot be bootstrapped; add --restore to restore it as an administrator.");
            }

            if (existing is not null)
            {
                return (existing, false);
            }
        }

        var (name, mail) = ProjectedIdentity(subjectId, username: null, email: null);
        var user = new User(subjectId, name, mail);
        await userRepository.InsertAsync(user, cancellationToken);
        logger.LogInformation("Created a minimal local user row for issuer subject {SubjectId}.", subjectId);
        return (user, true);
    }

    /// <summary>令牌缺少用户名或邮箱时的回落：用户名取主体标识，保证非空且可检索；邮箱留空串。</summary>
    private static (string Username, string Email) ProjectedIdentity(Guid subjectId, string? username, string? email)
        => (string.IsNullOrWhiteSpace(username) ? subjectId.ToString() : username.Trim(), email?.Trim() ?? string.Empty);

#endif
    /// <summary>检查用户名是否可用。</summary>
    /// <remarks>
    /// 查重要看见被软删除的行：用户名与邮箱的唯一索引都没有排除 <c>IsDeleted</c>，
    /// 软删除的用户仍然占着它们，而仓储默认把这些行过滤掉。不关掉过滤，这里会答"可用"，
    /// 随后落库撞唯一索引——用户看到的是 500，而不是"该用户名已被占用"。
    /// <para>
    /// 保留租户过滤：跨租户允许同名同邮箱，唯一索引也是按租户分开的。
    /// </para>
    /// </remarks>
    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken cancellationToken = default)
    {
        using var _ = dataFilter.Disable<ISoftDelete>();
        return !await userRepository.AnyAsync(u => u.Username == username, cancellationToken);
    }

    /// <summary>检查用户名是否可用（排除指定用户）。</summary>
    /// <inheritdoc cref="IsUsernameAvailableAsync(string, CancellationToken)" path="/remarks"/>
    public async Task<bool> IsUsernameAvailableAsync(Guid excludeUserId, string username, CancellationToken cancellationToken = default)
    {
        using var _ = dataFilter.Disable<ISoftDelete>();
        return !await userRepository.AnyAsync(u => u.Id != excludeUserId && u.Username == username, cancellationToken);
    }

    /// <summary>检查邮箱是否可用。</summary>
    /// <inheritdoc cref="IsUsernameAvailableAsync(string, CancellationToken)" path="/remarks"/>
    public async Task<bool> IsEmailAvailableAsync(string email, CancellationToken cancellationToken = default)
    {
        using var _ = dataFilter.Disable<ISoftDelete>();
        return !await userRepository.AnyAsync(u => u.Email == email, cancellationToken);
    }

    /// <summary>检查邮箱是否可用（排除指定用户）。</summary>
    /// <inheritdoc cref="IsUsernameAvailableAsync(string, CancellationToken)" path="/remarks"/>
    public async Task<bool> IsEmailAvailableAsync(Guid excludeUserId, string email, CancellationToken cancellationToken = default)
    {
        using var _ = dataFilter.Disable<ISoftDelete>();
        return !await userRepository.AnyAsync(u => u.Id != excludeUserId && u.Email == email, cancellationToken);
    }

#if (LocalIdentity)
    /// <summary>校验口令是否满足服务端策略，通过后返回哈希。</summary>
    /// <param name="password">明文口令</param>
    /// <param name="subject">错误消息里的主体描述</param>
    /// <remarks>
    /// <para>本领域服务是用户口令落值的唯一出口：创建、改密、重置、种子路径都经由它，
    /// 因此策略只在这里施加一次。DTO 与前端只做快速反馈，不承担安全不变量——
    /// 内部调用（种子、租户初始化、bootstrap）根本不经过 DTO。</para>
    /// <para>各入口各判各的时，系统的真实下限等于最宽的那一条。</para>
    /// </remarks>
    private string HashWithPolicy(string? password, string subject = "Password")
    {
        PasswordPolicy.Ensure(password, subject);
        return passwordHasher.HashPassword(password!);
    }

    /// <summary>创建<b>宿主</b>引导管理员（<c>IsSuperAdmin</c>）。</summary>
    /// <remarks>
    /// <c>IsSuperAdmin</c> 是宿主专用的权限管理防锁死主体，会旁路功能权限、资源授权和
    /// 数据范围，且不可停用或删除。租户管理员通过普通 Admin 角色管理权限，不得获得此标记。
    /// 在租户上下文中调用会以 <see cref="InvalidOperationException"/> 拒绝。
    /// </remarks>
    /// <exception cref="InvalidOperationException">当前存在租户上下文。</exception>
    public async Task<User> CreateSuperAdminAsync(
        string username,
        string email,
        string password,
        string? displayName,
        string passwordSubject,
        CancellationToken cancellationToken = default)
    {
        EnsureHostContextForSuperAdmin();

        var user = new User(username, email, HashWithPolicy(password, passwordSubject), displayName);
        user.MarkAsSuperAdmin();
        await userRepository.InsertAsync(user, cancellationToken);

        logger.LogInformation("Host super admin created: {Username} (ID: {UserId})", user.Username, user.Id);
        return user;
    }

    /// <summary>把已有的<b>宿主</b>用户提升为超级管理员，守卫与 <see cref="CreateSuperAdminAsync"/> 相同。</summary>
    /// <remarks>只改实体，调用方负责保存。</remarks>
    /// <exception cref="InvalidOperationException">当前存在租户上下文。</exception>
    public void PromoteToSuperAdmin(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureHostContextForSuperAdmin();
        user.MarkAsSuperAdmin();
    }

    private void EnsureHostContextForSuperAdmin()
    {
        if (currentTenant.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Cannot make a super admin inside tenant '{currentTenant.Id}'. IsSuperAdmin is the host " +
                "bootstrap escape hatch and must stay host-only; tenant administrators are ordinary users " +
                "holding the tenant's Admin role. Use CreateUserAsync and assign the Admin role instead.");
        }
    }

    /// <summary>管理员重置他人口令（不校验原口令，由应用层完成越权判断）。</summary>
    public void ResetPassword(User user, string? newPassword) =>
        user.UpdatePasswordHash(HashWithPolicy(newPassword));
#if (Email)

    /// <summary>本人凭邮箱验证码找回口令：按口令策略设置新口令（轮换安全版本），并解除登录失败触发的临时锁定。</summary>
    /// <remarks>
    /// 只有外部登录、还没有口令的账号，这一步等于设置第一个口令。能否找回（<see cref="User.CanRecoverPasswordByEmail"/>）
    /// 与验证码是否属于这个账号的这一版凭据，由应用层在凭据锁内判定。
    /// </remarks>
    public void RecoverPassword(User user, string? newPassword)
    {
        user.UpdatePasswordHash(HashWithPolicy(newPassword));
        user.ReleaseTemporaryLockout();
    }
#endif
#endif

#if (LocalIdentity)
    /// <summary>创建用户。</summary>
    /// <remarks>
    /// 资源服务形态没有这个方法：那一侧的用户行由 <c>EnsureProjectedAsync</c> 按令牌投影，
    /// 不存在"由本服务决定一个新主体的标识"这回事。
    /// </remarks>
    /// <param name="passwordSubject">
    /// 口令策略错误消息里的主体描述。内部调用（种子、租户初始化）应传入可辨识的值，
    /// 否则失败消息只会说"Password"，看不出是哪一处的口令不合规
    /// </param>
    public async Task<User> CreateUserAsync(
        string username,
        string email,
        string password,
        string? displayName,
        string passwordSubject = "Password",
        CancellationToken cancellationToken = default)
    {
        // 检查用户名唯一性
        if (!await IsUsernameAvailableAsync(username, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.UsernameTaken, $"Username '{username}' already exists.")
                .WithData("Username", username);
        }

        // 检查邮箱唯一性
        if (!await IsEmailAvailableAsync(email, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.EmailTaken, "Email is already in use.")
                .WithData("Email", email);
        }

        // 创建用户
        var user = new User(username, email, HashWithPolicy(password, passwordSubject), displayName);
        await userRepository.InsertAsync(user, cancellationToken);

        logger.LogInformation("User created: {Username} (ID: {UserId})", user.Username, user.Id);
        return user;
    }

#endif
#if (ExternalLogin)
    /// <summary>为首次外部登录建立本地用户：用户名按 <paramref name="usernameBase"/> 生成，没有本地口令。</summary>
    /// <param name="usernameBase">
    /// 期望的用户名基底，取提供商的公开句柄或显示名；为空或清洗后不可用时回落到 <c>user</c>。
    /// <b>不要传邮箱或邮箱本地部</b>：用户名是公开标识符，那样等于把半个联系方式公开。
    /// </param>
    /// <param name="email">已验证的邮箱或占位地址；由调用方确认未被占用。</param>
    /// <param name="emailConfirmed">提供商已确认该邮箱。</param>
    /// <param name="displayName">显示名；为空时取用户名。</param>
    /// <param name="avatarUrl">提供商的头像地址。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<User> CreateExternalAsync(
        string? usernameBase,
        string email,
        bool emailConfirmed,
        string? displayName,
        string? avatarUrl,
        CancellationToken cancellationToken = default)
    {
        var username = await GenerateAvailableUsernameAsync(usernameBase, cancellationToken);
        var user = new User(username, email, passwordHash: null, displayName: displayName ?? username);
        if (emailConfirmed)
        {
            user.ConfirmEmail();
        }

        if (!string.IsNullOrEmpty(avatarUrl))
        {
            user.SetAvatar(avatarUrl);
        }

        await userRepository.InsertAsync(user, cancellationToken);
        logger.LogInformation("User created via external login: {Username} (ID: {UserId})", user.Username, user.Id);
        return user;
    }

    /// <summary>按候选基底生成一个可用的本地用户名（<c>alice</c>，已被占用时 <c>alice_418203</c>）。</summary>
    /// <returns>当前未被占用、且符合 <see cref="UsernameRules"/> 的用户名。</returns>
    /// <exception cref="BusinessException">连续若干次随机后缀都被占用。</exception>
    /// <remarks>
    /// 不直接用提供商给的值：不同域的同名用户（<c>alice@x.com</c> 与 <c>alice@y.com</c>）会撞上 <c>Username</c> 的唯一索引，
    /// 第二个人首次登录直接失败；提供商给的值还可能含 <c>.</c> <c>+</c> 这类字符，不符合 <see cref="UsernameRules"/>，
    /// 会造出用户自己在账号设置里都改不回去的名字。
    /// <para>
    /// 裸名优先是为了可读：句柄或显示名本身就是用户认得的名字。
    /// 回落基底不发裸名：显示名清洗后不可用（纯中文、纯符号）的用户全都归到同一个 <c>user</c>，
    /// 裸名既没有任何识别价值，又会成为所有这类用户的确定性争抢点。六位随机后缀把它们分散到九十万个坑位上，
    /// 空间仍然有限——分配失败的出口一直留着。
    /// </para>
    /// <para>
    /// 随机候选也可能已被占用，所以最多尝试五次：同一基底已占用 N 个后缀时单次碰撞率约 N/900000，
    /// 一个租户积累上万个 <c>user_*</c> 后就不再是可以忽略的量。
    /// </para>
    /// <para>
    /// 并发边界：可用性是先查后插，而仓储在工作单元内不立即保存，真正落库在提交时。
    /// 两个请求同时判定同一个裸基底可用时，仍会有一个在提交时撞唯一索引。
    /// 这里不为它加提交期重试：撞上要求两人同时<b>首次</b>登录且基底相同，基底一旦被占用后续都走随机后缀。
    /// </para>
    /// </remarks>
    private async Task<string> GenerateAvailableUsernameAsync(string? preferredBase, CancellationToken cancellationToken)
    {
        var preferred = NormalizeUsernameBase(preferredBase);
        var baseName = preferred ?? FallbackUsernameBase;

        if (preferred is not null && await IsUsernameAvailableAsync(preferred, cancellationToken))
        {
            return preferred;
        }

        for (var attempt = 0; attempt < UsernameSuffixAttempts; attempt++)
        {
            var candidate = $"{baseName}_{Random.Shared.Next(100_000, 1_000_000)}";
            if (await IsUsernameAvailableAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        // 不复用 UserErrorCodes.UsernameTaken：那条的词条是"用户名已存在"并回显用户名，
        // 而这里的用户名是本服务生成的，用户既没填过它，也改不了它，只能重试。
        throw new BusinessException(
            ExternalAuthErrorCodes.UsernameAllocationFailed,
            "Could not allocate a username for this account. Try again.");
    }

    // 清洗成 UsernameRules 允许的字符集：只留字母数字与下划线，其余折成单个下划线并去掉首尾与连续的下划线。
    // 清洗后短于下限的返回 null，由调用方回落——补位凑长度只会造出 "zh__" 这种既不可读也没意义的名字。
    private static string? NormalizeUsernameBase(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return null;
        }

        var builder = new StringBuilder(preferred.Length);
        foreach (var ch in preferred)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var cleaned = builder.ToString().Trim('_');
        if (cleaned.Length > UsernameBaseMaxLength)
        {
            cleaned = cleaned[..UsernameBaseMaxLength].TrimEnd('_');
        }

        return cleaned.Length >= UsernameRules.MinLength ? cleaned : null;
    }

#endif
#if (LocalIdentity)
    /// <summary>管理员修改用户资料；邮箱在租户内唯一。</summary>
    /// <exception cref="BusinessException">邮箱已被占用（<see cref="UserErrorCodes.EmailTaken"/>），或头像不合规。</exception>
    public async Task UpdateManagementAsync(
        User user,
        string email,
        string? displayName,
        string? avatar,
        bool emailConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!await IsEmailAvailableAsync(user.Id, email, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.EmailTaken, "Email is already in use.")
                .WithData("Email", email);
        }

        user.UpdateManagement(email, displayName, avatar, emailConfirmed);
    }

#endif
    /// <summary>更新个人信息。</summary>
    public async Task UpdateProfileAsync(
        User user,
        string username,
        string email,
        string? displayName,
        string? phoneNumber,
        CancellationToken cancellationToken = default)
    {
        // 检查用户名唯一性
        if (!await IsUsernameAvailableAsync(user.Id, username, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.UsernameTaken, $"Username '{username}' already exists.")
                .WithData("Username", username);
        }

        // 检查邮箱唯一性
        if (!await IsEmailAvailableAsync(user.Id, email, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.EmailTaken, "Email is already in use.")
                .WithData("Email", email);
        }

        user.UpdateProfile(username, email, displayName, phoneNumber);
    }

#if (LocalIdentity)
    /// <summary>本人修改口令：校验当前口令，通过则写入新口令的哈希。</summary>
    /// <remarks>
    /// <b>失败返回状态而不是抛异常</b>，与 <see cref="ValidateCredentialsAsync"/> 同型：
    /// 当前口令不对是一次<b>再认证失败</b>，应用层要先计入失败次数、留下审计，然后才抛。
    /// 这里直接抛的话，应用层拿不到那个时机（见 <c>IReauthenticationGuard</c>）。
    /// 新口令的强度校验仍在这里，失败照常抛——那是输入不合格，不是认证失败。
    /// </remarks>
    /// <param name="user">当前用户。</param>
    /// <param name="currentPassword">待校验的当前口令。</param>
    /// <param name="newPassword">新口令。</param>
    public ChangePasswordStatus ChangePassword(User user, string currentPassword, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.PasswordHash == null)
        {
            return ChangePasswordStatus.NoLocalPassword;
        }

        if (!VerifyCurrentPassword(user, currentPassword))
        {
            return ChangePasswordStatus.CurrentPasswordIncorrect;
        }

        user.UpdatePasswordHash(HashWithPolicy(newPassword, "New password"));
        return ChangePasswordStatus.Succeeded;
    }

#if (ExternalLogin)
    /// <summary>给没有本地口令的账号（只经外部登录建立）设置第一个口令；已有口令时拒绝，改口令另有入口。</summary>
    /// <remarks>与改口令同样轮换安全版本：此前签发、尚未完成的登录挑战随之作废。</remarks>
    public void SetInitialPassword(User user, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.EnsureLocalPasswordNotSet();
        user.UpdatePasswordHash(HashWithPolicy(newPassword, "New password"));
    }

#endif
    /// <summary>再认证时核对本人的当前口令，只给判定；没有本地口令（只经外部登录）的账号一律不通过。</summary>
    /// <remarks>失败计数与锁定由应用层的再认证守卫负责，这里不改用户。</remarks>
    public bool VerifyCurrentPassword(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.PasswordHash is not null &&
            passwordHasher.VerifyPassword(user.PasswordHash, password) != PasswordVerificationStatus.Failed;
    }

    /// <summary>为已经验证的当前口令升级哈希，不重新应用新口令策略。</summary>
    public void RehashPassword(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.RehashPassword(passwordHasher.HashPassword(password));
    }

    /// <summary>校验用户名密码，只给判定。</summary>
    /// <remarks>
    /// <para><b>失败返回状态而不是抛异常，也不在这里累计失败</b>：累计要落在独立的工作单元里，
    /// 那是事务编排、属于应用层（见 <c>IAccessFailureCounter</c>）。领域只回答"口令对不对"。</para>
    /// <para>锁定中的账号<b>不校验密码</b>，直接返回 <see cref="CredentialValidationStatus.LockedOut"/>：
    /// 锁定期间若仍按密码对错给出不同结果，攻击者照样能一个个试，锁定就只是换了一种报错。</para>
    /// <para>没有密码的账号（只经外部登录）按凭据无效处理且<b>不应计数</b>：那里没有可猜的密码，
    /// 计数只会让别人能把它锁住，连外部登录一起挡在外面。调用方据
    /// <see cref="CredentialValidationResult.Countable"/> 判断。</para>
    /// </remarks>
    public async Task<CredentialValidationResult> ValidateCredentialsAsync(
        string usernameOrEmail,
        string password,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetFirstAsync(
            u => u.Username == usernameOrEmail || u.Email == usernameOrEmail,
            q => q.OrderBy(u => u.Id),
            cancellationToken);

        if (user == null || user.PasswordHash == null)
        {
            return new CredentialValidationResult(CredentialValidationStatus.InvalidCredentials, user);
        }

        if (user.GetAccessStatus(now) == UserAccessStatus.LockedOut)
        {
            return new CredentialValidationResult(CredentialValidationStatus.LockedOut, user);
        }

        var verification = passwordHasher.VerifyPassword(user.PasswordHash, password);
        if (verification != PasswordVerificationStatus.Failed)
        {
            return new CredentialValidationResult(CredentialValidationStatus.Succeeded, user,
                PasswordRehashNeeded: verification == PasswordVerificationStatus.RehashNeeded);
        }

        return new CredentialValidationResult(CredentialValidationStatus.InvalidCredentials, user, Countable: true);
    }
#endif
}
