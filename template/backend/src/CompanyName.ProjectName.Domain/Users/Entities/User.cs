#if (LocalIdentity)
using System.Linq.Expressions;
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
using Leistd.ExceptionHandling;
#if (ExternalLogin)
using CompanyName.ProjectName.Domain.Users.Errors;
#endif
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Policies;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Domain.Users.Entities;

public class User : FullAuditedEntity<Guid>, IAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>所属租户（null 为宿主用户），由多租户落值拦截器在创建时填充。</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>用户名（租户内唯一）。</summary>
    public string Username { get; private set; }

    /// <summary>邮箱（租户内唯一）。</summary>
    public string Email { get; private set; }

#if (LocalIdentity)
    /// <summary>邮箱是否已验证。</summary>
    public bool EmailConfirmed { get; private set; }

    /// <summary>密码哈希（可为空，OAuth 用户无密码）。</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>手机号。</summary>
    public string? PhoneNumber { get; private set; }
#endif

    /// <summary>头像。</summary>
    public string? Avatar { get; private set; }

    /// <summary>显示名称。</summary>
    public string? DisplayName { get; private set; }

    /// <summary>是否启用。</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>是否系统内置超级管理员。</summary>
    public bool IsSuperAdmin { get; private set; }

#if (LocalIdentity)
    /// <summary>锁定状态。</summary>
    public LockoutState Lockout { get; private set; } = LockoutState.None;

    /// <summary>最近一次成功登录；从未登录为 null。</summary>
    public LoginTrace? LastLogin { get; private set; }

    /// <summary>两步验证凭据；未启用为 null。</summary>
    public TwoFactorCredential? TwoFactor { get; private set; }

    /// <summary>是否已启用两步验证。</summary>
    public bool TwoFactorEnabled => TwoFactor is not null;

    /// <summary>设有本地口令（外部登录建的账号可以没有）。</summary>
    public bool HasLocalPassword => PasswordHash is not null;

    /// <summary>账号安全版本：凭据（口令、两步验证、外部登录绑定）每变一次就换一个新值。</summary>
    /// <remarks>
    /// 登录第二步的挑战记下签发时的值，完成前比对：第一步通过之后改了口令、重置或解绑了凭据，
    /// 旧挑战随之作废。挑战不是会话，撤销会话挡不住它。
    /// </remarks>
    public string SecurityStamp { get; private set; }
#endif

    private readonly List<UserRole> _roles = [];

    /// <summary>角色成员关系（子实体）。</summary>
    /// <remarks>
    /// 只在经 <c>IUserRepository</c> 带角色读取时加载，<c>GetByIdAsync</c> 读出的用户这里为空：修改角色前必须带角色读取。
    /// 本工作单元内撤销的行仍在集合里（<c>IsDeleted</c> 为真），判断成员关系用 <see cref="IsInRole"/> 或 <see cref="GetRoleIds"/>。
    /// </remarks>
    public IReadOnlyCollection<UserRole> Roles => _roles;

    private User()
    {
        Username = null!;
        Email = null!;
#if (LocalIdentity)
        SecurityStamp = null!;
#endif
    }

    public User(
#if (!LocalIdentity)
        Guid subjectId,
#endif
        string username,
        string email,
        string? passwordHash = null,
        string? displayName = null)
    {
#if (LocalIdentity)
        Id = Guid.CreateVersion7();
#else
        if (subjectId == Guid.Empty) throw new ArgumentException("Subject Id cannot be empty.", nameof(subjectId));
        Id = subjectId;
#endif
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(email);
        Username = username;
        Email = email;
#if (LocalIdentity)
        PasswordHash = passwordHash;
        SecurityStamp = NewSecurityStamp();
#endif
        DisplayName = displayName ?? username;
    }

#if (!LocalIdentity)
    /// <summary>按签发方令牌刷新资料字段。</summary>
    /// <remarks>
    /// 资源服务形态下这三个字段归签发方所有，本地没有编辑入口，只有这一条刷新通道。
    /// <b>刻意不碰 <see cref="IsActive"/> 与角色</b>：那是本服务自己的授权决定，
    /// 跟着刷新走会把管理员刚做的启停与授权冲掉，而且每个请求冲一次。
    /// </remarks>
    public void ProjectFromIssuer(string username, string email, string? displayName)
    {
        Username = username;
        Email = email;
        DisplayName = displayName;
    }

#endif
#if (LocalIdentity)
    /// <summary>管理员修改资料；启用状态只经 <see cref="Enable"/>/<see cref="Disable"/>，那里才有超管保护。</summary>
    /// <remarks>邮箱唯一由 <c>UserDomainService.UpdateManagementAsync</c> 判定，因此不对领域层之外开放。</remarks>
    internal void UpdateManagement(string email, string? displayName, string? avatar, bool emailConfirmed)
    {
        AvatarPolicy.EnsureValid(avatar);
        Email = email;
        DisplayName = displayName;
        Avatar = string.IsNullOrEmpty(avatar) ? null : avatar;
        EmailConfirmed = emailConfirmed;
    }

#endif

    /// <summary>本人修改资料。头像另有入口（<see cref="SetAvatar"/>），不随资料表单一起提交。</summary>
    /// <remarks>换了邮箱即回到"未验证"：验证过的是旧地址，新地址是否属于本人还不知道。</remarks>
    public void UpdateProfile(string username, string email, string? displayName, string? phoneNumber)
    {
#if (LocalIdentity)
        if (!string.Equals(Email, email, StringComparison.OrdinalIgnoreCase))
        {
            EmailConfirmed = false;
        }

#endif
        Username = username;
        Email = email;
        DisplayName = displayName;
#if (LocalIdentity)
        PhoneNumber = phoneNumber;
#endif
    }

    /// <summary>设置或清除头像。</summary>
    /// <exception cref="BusinessException">取值不合规（见 <see cref="AvatarPolicy.EnsureValid"/>）。</exception>
    public void SetAvatar(string? avatar)
    {
        AvatarPolicy.EnsureValid(avatar);
        Avatar = string.IsNullOrEmpty(avatar) ? null : avatar;
    }

    /// <summary>标记为宿主超级管理员。</summary>
    /// <remarks>只由 <see cref="UserDomainService"/> 调用：它在标记之前挡住租户上下文，超管必须只在宿主。</remarks>
    internal void MarkAsSuperAdmin()
    {
        IsSuperAdmin = true;
    }

    public bool CanBeManagedBy(Guid? actorUserId)
    {
        return !IsSuperAdmin || Id == actorUserId;
    }

    public bool CanBeDisabled()
    {
        return !IsSuperAdmin;
    }

    /// <summary>能否由 <paramref name="actorUserId"/> 停用：在 <see cref="CanBeDisabled"/> 之上，任何人都不能停用自己。</summary>
    /// <remarks>
    /// 停用会立刻作废本人全部会话，操作者当场被关在门外，而能把他请回来的往往只剩他自己。
    /// 自己移出角色、收窄权限不在此列：那是正当的自我降级，误操作由部署级恢复命令兜底。
    /// </remarks>
    public bool CanBeDisabledBy(Guid? actorUserId)
    {
        return CanBeDisabled() && Id != actorUserId;
    }

    public bool CanBeDeleted()
    {
        return !IsSuperAdmin;
    }
#if (LocalIdentity)

    /// <summary>能否由 <paramref name="actorUserId"/> 删除：在 <see cref="CanBeDeleted"/> 之上，任何人都不能删除自己，理由同 <see cref="CanBeDisabledBy"/>。</summary>
    public bool CanBeDeletedBy(Guid? actorUserId)
    {
        return CanBeDeleted() && Id != actorUserId;
    }

    /// <summary>能否由 <paramref name="actorUserId"/> 经用户管理重置两步验证：在 <see cref="CanBeManagedBy"/> 之上，任何人都不能重置自己。</summary>
    /// <remarks>
    /// 本人的两步验证走个人安全设置：停用要口令与验证码、受组织强制策略约束，换绑要再认证与第二因素。
    /// 管理入口不查这些，放行本人就等于给会话持有者一条绕过它们的路。
    /// 丢了验证器的本人用恢复码登录后自助换绑；内置超级管理员只能由本人操作，因此也只走这条路，
    /// 恢复码也用尽时由部署方处理（见项目文档）。
    /// </remarks>
    public bool CanTwoFactorBeResetBy(Guid? actorUserId)
    {
        return CanBeManagedBy(actorUserId) && Id != actorUserId;
    }
#endif

    /// <summary>撤回软删除，让这一行按原 Id 回到正常查询里；删除时间与删除者一并清空。</summary>
    /// <remarks>
    /// 只供部署级的管理员恢复命令使用，在线管理没有"恢复已删除用户"的入口。
    /// 不碰启停、角色与凭据：删除前的状态原样回来，其余由调用方逐项决定。
    /// </remarks>
    public void Restore()
    {
        IsDeleted = false;
        DeleterId = null;
        DeletionTime = null;
    }

    /// <summary>当前持有该角色（未撤销）。</summary>
    public bool IsInRole(Guid roleId) => _roles.Any(role => role.RoleId == roleId && !role.IsDeleted);

    /// <summary>当前持有的角色 Id（不含已撤销的）。</summary>
    public IReadOnlyList<Guid> GetRoleIds() => [.. _roles.Where(role => !role.IsDeleted).Select(role => role.RoleId)];

    /// <summary>分配角色；已持有的跳过。</summary>
    /// <remarks>角色是否存在由调用方经角色仓储确认：这里只按 Id 引用，不读角色聚合。</remarks>
    public void AssignRoles(IEnumerable<Guid> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        foreach (var roleId in roleIds.Distinct())
        {
            if (!IsInRole(roleId))
            {
                _roles.Add(new UserRole(Id, roleId));
            }
        }
    }

    /// <summary>用给定角色整体替换现有角色：不在其中的撤销，缺的补上，保留的不动；为空即清空。</summary>
    /// <remarks>撤销与补上随同一次保存落库，不会出现中途失败后零角色的状态。</remarks>
    public void ReplaceRoles(IEnumerable<Guid> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var target = roleIds.ToHashSet();
        foreach (var membership in _roles.Where(role => !role.IsDeleted && !target.Contains(role.RoleId)))
        {
            membership.Revoke();
        }

        AssignRoles(target);
    }

    /// <summary>撤销该角色的成员关系；未持有时什么也不做。</summary>
    public void RemoveRole(Guid roleId)
    {
        foreach (var membership in _roles.Where(role => role.RoleId == roleId && !role.IsDeleted))
        {
            membership.Revoke();
        }
    }

    /// <summary>撤销全部角色成员关系。</summary>
    public void RemoveAllRoles() => ReplaceRoles([]);

    public void Enable()
    {
        IsActive = true;
    }

    public void Disable()
    {
        IsActive = false;
    }

#if (LocalIdentity)
    public void UpdatePasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
        RotateSecurityStamp();
    }

    /// <summary>升级已验证口令的哈希参数，保留安全版本。</summary>
    public void RehashPassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>换一个新的安全版本，使此前签发、尚未完成的登录挑战作废。</summary>
    /// <remarks>改口令、启用或停用两步验证时自动调用；其余凭据变化（解绑外部登录）由调用方显式调用。</remarks>
    public void RotateSecurityStamp()
    {
        SecurityStamp = NewSecurityStamp();
    }

    /// <summary>安全版本仍是 <paramref name="stamp"/>：签发后凭据没有变过。</summary>
    public bool HasSecurityStamp(string stamp) => string.Equals(SecurityStamp, stamp, StringComparison.Ordinal);

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    public void ConfirmEmail()
    {
        EmailConfirmed = true;
    }
#if (Email)

    /// <summary>邮箱尚未验证；已验证时以 <see cref="AuthErrorCodes.EmailAlreadyVerified"/> 拒绝再发验证码。</summary>
    public void EnsureEmailUnconfirmed()
    {
        if (EmailConfirmed)
        {
            throw new BusinessException(AuthErrorCodes.EmailAlreadyVerified, "This email address has already been verified.");
        }
    }
#endif

    /// <summary>锁定；不传截止时间即管理员锁定。</summary>
    public void Lock(DateTime? lockoutEnd = null)
    {
        Lockout = Lockout.LockUntil(lockoutEnd);
    }

    /// <summary>解除锁定并清零失败次数。</summary>
    public void Unlock()
    {
        Lockout = LockoutState.None;
    }

    /// <summary>记一次密码错误；累计达到 <paramref name="policy"/> 的阈值时锁定一段时间，返回 true。</summary>
    /// <remarks>规则见 <see cref="LockoutState.RecordFailure"/>。</remarks>
    public bool RecordAccessFailed(DateTime now, LoginLockoutPolicy policy)
    {
        Lockout = Lockout.RecordFailure(now, policy, out var lockedOut);
        return lockedOut;
    }

    /// <summary>处于因登录失败而起的临时锁定中（有截止时间且未到）。</summary>
    /// <remarks>
    /// 与管理员锁定（无截止时间）分开判：临时锁定可以由别人反复输错触发，
    /// 它只挡新的登录，不能拿来把已经登录的本人踢下线。
    /// </remarks>
    public bool IsTemporarilyLockedOut(DateTime now) => Lockout.IsTemporaryAt(now);

    /// <summary>在 <paramref name="now"/> 时刻处于锁定中的用户（与 <see cref="LockoutState.IsActiveAt"/> 同一判据），可翻译成 SQL。</summary>
    public static Expression<Func<User, bool>> LockedOutAt(DateTime now) =>
        user => user.Lockout.IsLocked && (user.Lockout.End == null || user.Lockout.End > now);

    /// <summary>已建立的会话与已签发的令牌还能否继续使用：账号被禁用、或被锁定且没有截止时间时不能。</summary>
    /// <remarks>
    /// 登录失败触发的临时锁定只挡新的登录：它可以由别人反复输错触发，若也作用于已在线的会话，
    /// 知道用户名就能把本人踢下线。
    /// </remarks>
    public bool AllowsExistingSessions(DateTime now) => GetAccessStatus(now) switch
    {
        UserAccessStatus.Allowed => true,
        UserAccessStatus.LockedOut => IsTemporarilyLockedOut(now),
        _ => false,
    };

    public void RecordLoginSuccess(DateTime now, string? ip = null)
    {
        LastLogin = new LoginTrace(now, ip);
        Lockout = Lockout.ResetFailures();
    }

    /// <summary>剩余可用的恢复码个数；未启用两步验证为 0。</summary>
    public int RecoveryCodesLeft => TwoFactor?.RecoveryCodesLeft ?? 0;

    /// <summary>启用两步验证。</summary>
    /// <param name="protectedSecret">已加密的密钥。</param>
    /// <param name="recoveryCodeHashes">恢复码摘要。</param>
    /// <param name="usedStep">启用时校验通过的那一步，随即记为已用：同一个码不能紧接着再拿去登录。</param>
    /// <exception cref="BusinessException">两步验证已启用（<see cref="AuthErrorCodes.TwoFactorAlreadyEnabled"/>）：重新启用会静默换掉正在使用的密钥。</exception>
    public void EnableTwoFactor(string protectedSecret, IEnumerable<string> recoveryCodeHashes, long usedStep)
    {
        EnsureTwoFactorDisabled();
        TwoFactor = TwoFactorCredential.Create(protectedSecret, recoveryCodeHashes, usedStep);
        RotateSecurityStamp();
    }

    /// <summary>换绑身份验证器：换成新的密钥与一组新的恢复码，旧验证器与旧恢复码随即作废。</summary>
    /// <param name="protectedSecret">已加密的新密钥。</param>
    /// <param name="recoveryCodeHashes">新恢复码摘要。</param>
    /// <param name="usedStep">确认新密钥时校验通过的那一步，随即记为已用。</param>
    /// <exception cref="BusinessException">两步验证未启用（<see cref="AuthErrorCodes.TwoFactorNotEnabled"/>）：没有可换的验证器，应走启用。</exception>
    public void ReplaceTwoFactor(string protectedSecret, IEnumerable<string> recoveryCodeHashes, long usedStep)
    {
        EnsureTwoFactorEnabled();
        TwoFactor = TwoFactorCredential.Create(protectedSecret, recoveryCodeHashes, usedStep);
        RotateSecurityStamp();
    }

    /// <summary>停用两步验证，并清掉密钥与恢复码。</summary>
    public void DisableTwoFactor()
    {
        TwoFactor = null;
        RotateSecurityStamp();
    }

    /// <summary>换一组恢复码，旧的全部作废。</summary>
    /// <exception cref="BusinessException">两步验证未启用（<see cref="AuthErrorCodes.TwoFactorNotEnabled"/>）。</exception>
    public void ReplaceRecoveryCodes(IEnumerable<string> recoveryCodeHashes)
    {
        EnsureTwoFactorEnabled();
        TwoFactor = TwoFactor!.WithRecoveryCodes(recoveryCodeHashes);
    }

    /// <summary>两步验证尚未启用；已启用时以 <see cref="AuthErrorCodes.TwoFactorAlreadyEnabled"/> 拒绝。</summary>
    public void EnsureTwoFactorDisabled()
    {
        if (TwoFactorEnabled)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorAlreadyEnabled, "Two-factor authentication is already turned on.");
        }
    }

#if (ExternalLogin)
    /// <summary>还没有本地口令（只经外部登录建立）；已有时以 <see cref="SecurityErrorCodes.PasswordAlreadySet"/> 拒绝，改口令另有入口。</summary>
    public void EnsureLocalPasswordNotSet()
    {
        if (HasLocalPassword)
        {
            throw new BusinessException(SecurityErrorCodes.PasswordAlreadySet, "This account already has a password. Use change password instead.");
        }
    }

#endif
    /// <summary>两步验证已启用；未启用时以 <see cref="AuthErrorCodes.TwoFactorNotEnabled"/> 拒绝。</summary>
    public void EnsureTwoFactorEnabled()
    {
        if (!TwoFactorEnabled)
        {
            throw new BusinessException(AuthErrorCodes.TwoFactorNotEnabled, "Two-factor authentication is not turned on.");
        }
    }

    /// <summary>记下校验通过的步序号。</summary>
    /// <exception cref="BusinessException">两步验证未启用（<see cref="AuthErrorCodes.TwoFactorNotEnabled"/>）。</exception>
    public void RecordTwoFactorStep(long step)
    {
        EnsureTwoFactorEnabled();
        TwoFactor = TwoFactor!.WithLastUsedStep(step);
    }

    /// <summary>用掉一个恢复码；未启用两步验证或摘要不在剩余列表里时返回 false。</summary>
    public bool TryConsumeRecoveryCode(string hash)
    {
        if (TwoFactor?.ConsumeRecoveryCode(hash) is not { } next)
            return false;

        TwoFactor = next;
        return true;
    }

#endif

    public UserAccessStatus GetAccessStatus(DateTime now)
    {
        if (!IsActive)
        {
            return UserAccessStatus.Disabled;
        }

#if (LocalIdentity)
        if (Lockout.IsActiveAt(now))
        {
            return UserAccessStatus.LockedOut;
        }
#endif

        return UserAccessStatus.Allowed;
    }
}
