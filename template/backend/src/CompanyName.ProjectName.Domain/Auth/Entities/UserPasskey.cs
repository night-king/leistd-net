#if (LocalIdentity)
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Domain.Auth.Entities;

/// <summary>本人登记的一把通行密钥（WebAuthn 凭据）。</summary>
/// <remarks>
/// <para>独立的聚合：免口令登录要先按凭据 Id 找到它，才知道是哪个用户；用户上只记个数（<c>User.PasskeyCount</c>），
/// 增删只经 <c>PasskeyDomainService</c>，与个数同一工作单元提交。</para>
/// <para>凭据 Id、公钥与认证器标志都存明文：公钥不是秘密，凭据 Id 要建唯一索引才能按它定位。
/// 实现 <see cref="IMultiTenant"/>：凭据随用户落在其租户的库里，免口令登录在当前租户内按凭据 Id 查找。</para>
/// </remarks>
public class UserPasskey : CreationAuditedEntity<Guid>, IAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>名称的最大长度。</summary>
    public const int NameMaxLength = 128;

    /// <summary>所属租户（null 为宿主），由多租户落值拦截器在创建时填充。</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>用户 Id。</summary>
    public Guid UserId { get; private set; }

    /// <summary>凭据 Id（认证器生成，租户内唯一）。</summary>
    public byte[] CredentialId { get; private set; }

    /// <summary>COSE 格式的公钥。</summary>
    public byte[] PublicKey { get; private set; }

    /// <summary>登记时交给认证器的用户句柄（用户 Id 的字节），断言时认证器原样带回。</summary>
    public byte[] UserHandle { get; private set; }

    /// <summary>签名计数器；认证器支持时每次使用递增，回退说明凭据可能被克隆。</summary>
    public long SignCount { get; private set; }

    /// <summary>认证器型号（AAGUID）；证明为 none 时多为全零。</summary>
    public Guid Aaguid { get; private set; }

    /// <summary>认证器声明的传输方式（usb、nfc、ble、hybrid、internal），以逗号分隔，登录时交还浏览器作提示。</summary>
    public string Transports { get; private set; }

    /// <summary>凭据可以被同步备份（多设备通行密钥）。</summary>
    public bool IsBackupEligible { get; private set; }

    /// <summary>凭据当前已备份；随每次使用更新。</summary>
    public bool IsBackedUp { get; private set; }

    /// <summary>本人起的名称，便于在列表里区分。</summary>
    public string Name { get; private set; }

    /// <summary>最近一次使用的时刻；从未使用为 null。</summary>
    public DateTime? LastUsedTime { get; private set; }

    private UserPasskey()
    {
        CredentialId = null!;
        PublicKey = null!;
        UserHandle = null!;
        Transports = null!;
        Name = null!;
    }

    internal UserPasskey(Guid userId, PasskeyCredentialData credential, string name)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Id = Guid.CreateVersion7();
        UserId = userId;
        CredentialId = credential.CredentialId;
        PublicKey = credential.PublicKey;
        UserHandle = credential.UserHandle;
        SignCount = credential.SignCount;
        Aaguid = credential.Aaguid;
        Transports = string.Join(',', credential.Transports);
        IsBackupEligible = credential.IsBackupEligible;
        IsBackedUp = credential.IsBackedUp;
        Name = NormalizeName(name);
    }

    /// <summary>传输方式列表。</summary>
    public IReadOnlyList<string> GetTransports() =>
        Transports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>一次通过的使用：记下新的计数器与备份状态。</summary>
    public void RecordUse(long signCount, bool isBackedUp, DateTime now)
    {
        SignCount = signCount;
        IsBackedUp = isBackedUp;
        LastUsedTime = now;
    }

    /// <summary>改名。</summary>
    public void Rename(string name)
    {
        Name = NormalizeName(name);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim();
        ArgumentException.ThrowIfNullOrEmpty(trimmed, nameof(name));
        return trimmed.Length <= NameMaxLength ? trimmed : trimmed[..NameMaxLength];
    }
}

/// <summary>认证器登记成功后交回的凭据数据，已经过协议校验。</summary>
/// <param name="CredentialId">凭据 Id。</param>
/// <param name="PublicKey">COSE 公钥。</param>
/// <param name="UserHandle">用户句柄。</param>
/// <param name="SignCount">签名计数器。</param>
/// <param name="Aaguid">认证器型号。</param>
/// <param name="Transports">传输方式。</param>
/// <param name="IsBackupEligible">可备份。</param>
/// <param name="IsBackedUp">已备份。</param>
public sealed record PasskeyCredentialData(
    byte[] CredentialId,
    byte[] PublicKey,
    byte[] UserHandle,
    long SignCount,
    Guid Aaguid,
    IReadOnlyList<string> Transports,
    bool IsBackupEligible,
    bool IsBackedUp);
#endif
