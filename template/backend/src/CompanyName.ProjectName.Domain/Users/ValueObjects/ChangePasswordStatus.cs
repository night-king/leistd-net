#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Users.ValueObjects;

/// <summary>本人修改口令的判定结果。</summary>
/// <remarks>
/// 与 <see cref="CredentialValidationStatus"/> 同型：领域只给判定，
/// <b>失败的后果由应用层决定</b>——抛哪个业务异常、要不要计入失败次数、要不要留审计，
/// 都是应用层的事。领域服务自己抛异常的话，应用层就没有机会在异常之前先做那些事。
/// </remarks>
public enum ChangePasswordStatus
{
    /// <summary>当前口令正确，新口令已写入实体。</summary>
    Succeeded,

    /// <summary>该账号没有本地口令（仅外部登录），无从校验也无从修改。</summary>
    NoLocalPassword,

    /// <summary>当前口令不正确。实体未被修改。</summary>
    CurrentPasswordIncorrect,
}
#endif
