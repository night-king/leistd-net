#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Entities;

namespace CompanyName.ProjectName.Domain.Auth.Passkeys;

/// <summary>WebAuthn 协议：生成登记与断言的选项、校验浏览器交回的响应。</summary>
/// <remarks>
/// <para>只管协议本身（挑战、来源、RP ID、签名、计数器、用户验证），不管凭据存在哪、仪式状态存在哪、谁能做：
/// 那些是领域与应用层的规则。实现由基础设施层提供（Fido2NetLib），换库只换实现。</para>
/// <para>选项与仪式状态都是不透明的 JSON 文本：选项原样交给浏览器，状态由调用方存在服务端、完成时原样交回。
/// 浏览器的响应也是原样的 JSON 文本，按协议库自己的规则解析，不走宿主的全局 JSON 选项。</para>
/// </remarks>
public interface IPasskeyProtocol
{
    /// <summary>部署已配置依赖方（RP ID 与允许的来源）；没配时通行密钥不可用。</summary>
    bool IsAvailable { get; }

    /// <summary>为本人生成登记选项：排除已登记的凭据，要求用户验证，尽量生成可发现凭据（用于免口令登录）。</summary>
    PasskeyCeremony BeginRegistration(PasskeyAccount account, IEnumerable<PasskeyDescriptor> existing);

    /// <summary>校验登记响应，返回可以入库的凭据。</summary>
    /// <exception cref="PasskeyVerificationException">响应不合规、挑战或来源对不上。</exception>
    Task<PasskeyCredentialData> CompleteRegistrationAsync(string state, string response, CancellationToken cancellationToken);

    /// <summary>生成断言选项；<paramref name="allowed"/> 为空时让浏览器列出本站的可发现凭据（免口令登录）。</summary>
    PasskeyCeremony BeginAssertion(IEnumerable<PasskeyDescriptor> allowed);

    /// <summary>读出断言响应里的凭据 Id 与用户句柄，以便先找到对应的凭据；响应格式不对时返回 null。</summary>
    PasskeyAssertionIdentity? ReadAssertionIdentity(string response);

    /// <summary>用已登记的凭据校验断言。</summary>
    /// <exception cref="PasskeyVerificationException">签名不对、挑战或来源对不上、未做用户验证、计数器回退。</exception>
    Task<PasskeyAssertion> CompleteAssertionAsync(
        string state,
        string response,
        PasskeyStoredKey stored,
        CancellationToken cancellationToken);
}

/// <summary>一次仪式：交给浏览器的选项，与服务端保存、完成时交回的状态。</summary>
public sealed record PasskeyCeremony(string Options, string State);

/// <summary>登记时写进凭据的账号信息。</summary>
/// <param name="UserHandle">用户句柄（用户 Id 的字节），认证器断言时原样带回。</param>
/// <param name="Name">账号名，认证器列表里区分账号用。</param>
/// <param name="DisplayName">显示名。</param>
public sealed record PasskeyAccount(byte[] UserHandle, string Name, string DisplayName);

/// <summary>已登记凭据的引用：凭据 Id 与传输方式。</summary>
public sealed record PasskeyDescriptor(byte[] CredentialId, IReadOnlyList<string> Transports);

/// <summary>断言响应声明的凭据 Id 与用户句柄（尚未校验）。</summary>
public sealed record PasskeyAssertionIdentity(byte[] CredentialId, byte[]? UserHandle);

/// <summary>校验断言用的已登记凭据。</summary>
public sealed record PasskeyStoredKey(byte[] CredentialId, byte[] PublicKey, long SignCount, byte[] UserHandle);

/// <summary>校验通过的断言：新的计数器与备份状态。</summary>
public sealed record PasskeyAssertion(byte[] CredentialId, long SignCount, bool IsBackedUp);

/// <summary>协议校验不通过。消息只进日志，不回给浏览器。</summary>
public sealed class PasskeyVerificationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
#endif
