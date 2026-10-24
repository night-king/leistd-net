#if (LocalIdentity)
using System.Text.Json;
using CompanyName.ProjectName.Domain.Auth.Passkeys;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Infrastructure.Auth.Passkeys;

/// <summary>用 Fido2NetLib 实现 WebAuthn 协议。</summary>
/// <remarks>
/// <para>选项组合：用户验证必需（通行密钥一次即具备"持有 + 本人"）、证明取 none（不验认证器型号）、
/// 可发现凭据取 preferred（能免口令登录的认证器就生成可发现凭据，不能的仍可作第二步）。</para>
/// <para>选项、状态与浏览器响应都按 Fido2 自己的序列化上下文读写：COSE 算法要保持数字，
/// <c>public-key</c> 等字面量也不能被宿主的全局枚举转换器改写。</para>
/// </remarks>
internal sealed class Fido2PasskeyProtocol(IOptions<PasskeyOptions> options) : IPasskeyProtocol
{
    // 登记响应不在 Fido2 的源生成上下文里，按它自带的 JSON 特性反射读取；用独立的默认选项，不受宿主全局选项影响
    private static readonly JsonSerializerOptions AttestationJson = new(JsonSerializerDefaults.General);

    private readonly Lazy<IFido2?> _fido2 = new(() => Create(options.Value));

    public bool IsAvailable => options.Value.IsConfigured;

    public PasskeyCeremony BeginRegistration(PasskeyAccount account, IEnumerable<PasskeyDescriptor> existing)
    {
        var created = Fido2().RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = account.UserHandle, Name = account.Name, DisplayName = account.DisplayName },
            ExcludeCredentials = [.. existing.Select(Descriptor)],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        var json = created.ToJson();
        return new PasskeyCeremony(json, json);
    }

    public async Task<PasskeyCredentialData> CompleteRegistrationAsync(string state, string response, CancellationToken cancellationToken)
    {
        var original = Read(() => CredentialCreateOptions.FromJson(state), "registration state");
        var attestation = Read(
            () => JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(response, AttestationJson),
            "registration response");
        RegisteredPublicKeyCredential credential;
        try
        {
            // 唯一性由调用方按凭据 Id 查库兜住（领域服务与唯一索引）；这里不重复查
            credential = await Fido2().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = static (_, _) => Task.FromResult(true)
            }, cancellationToken);
        }
        catch (Fido2VerificationException exception)
        {
            throw new PasskeyVerificationException($"Passkey registration was rejected: {exception.Code}.", exception);
        }

        return new PasskeyCredentialData(
            credential.Id,
            credential.PublicKey,
            credential.User.Id,
            credential.SignCount,
            credential.AaGuid,
            [.. (credential.Transports ?? []).Select(TransportName)],
            credential.IsBackupEligible,
            credential.IsBackedUp);
    }

    public PasskeyCeremony BeginAssertion(IEnumerable<PasskeyDescriptor> allowed)
    {
        var assertion = Fido2().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. allowed.Select(Descriptor)],
            UserVerification = UserVerificationRequirement.Required
        });
        var json = assertion.ToJson();
        return new PasskeyCeremony(json, json);
    }

    public PasskeyAssertionIdentity? ReadAssertionIdentity(string response)
    {
        try
        {
            var assertion = JsonSerializer.Deserialize(response, FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse);
            return assertion?.RawId is { Length: > 0 } id ? new PasskeyAssertionIdentity(id, assertion.Response?.UserHandle) : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<PasskeyAssertion> CompleteAssertionAsync(
        string state,
        string response,
        PasskeyStoredKey stored,
        CancellationToken cancellationToken)
    {
        var original = Read(() => AssertionOptions.FromJson(state), "assertion state");
        var assertion = Read(
            () => JsonSerializer.Deserialize(response, FidoModelSerializerContext.Default.AuthenticatorAssertionRawResponse),
            "assertion response");
        VerifyAssertionResult result;
        try
        {
            result = await Fido2().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = original,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = checked((uint)stored.SignCount),
                // 凭据与用户句柄都必须是这一把：别人的凭据、或带着别人句柄的断言一律不认
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    args.CredentialId.AsSpan().SequenceEqual(stored.CredentialId)
                    && (args.UserHandle is null || args.UserHandle.AsSpan().SequenceEqual(stored.UserHandle)))
            }, cancellationToken);
        }
        catch (Fido2VerificationException exception)
        {
            throw new PasskeyVerificationException($"Passkey assertion was rejected: {exception.Code}.", exception);
        }

        return new PasskeyAssertion(result.CredentialId, result.SignCount, result.IsBackedUp);
    }

    private IFido2 Fido2() =>
        _fido2.Value ?? throw new InvalidOperationException($"{PasskeyOptions.SectionName} is not configured; check IsAvailable first.");

    private static IFido2? Create(PasskeyOptions options) =>
        options.IsConfigured
            ? new Fido2(new Fido2Configuration
            {
                RPID = options.RelyingPartyId!.Trim(),
                RPName = options.RelyingPartyName,
                Origins = new HashSet<string>(options.ConfiguredOrigins, StringComparer.OrdinalIgnoreCase)
            })
            : null;

    private static T Read<T>(Func<T?> read, string what)
    {
        try
        {
            return read() ?? throw new PasskeyVerificationException($"The passkey {what} is empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException or FormatException)
        {
            throw new PasskeyVerificationException($"The passkey {what} is malformed.", exception);
        }
    }

    private static PublicKeyCredentialDescriptor Descriptor(PasskeyDescriptor descriptor) =>
        new(PublicKeyCredentialType.PublicKey, descriptor.CredentialId,
            [.. descriptor.Transports.Select(ParseTransport).OfType<AuthenticatorTransport>()]);

    // 与 WebAuthn 的 AuthenticatorTransport 字面量一致
    private static string TransportName(AuthenticatorTransport transport) => transport switch
    {
        AuthenticatorTransport.Usb => "usb",
        AuthenticatorTransport.Nfc => "nfc",
        AuthenticatorTransport.Ble => "ble",
        AuthenticatorTransport.SmartCard => "smart-card",
        AuthenticatorTransport.Hybrid => "hybrid",
        AuthenticatorTransport.Internal => "internal",
        _ => transport.ToString().ToLowerInvariant()
    };

    private static AuthenticatorTransport? ParseTransport(string name) => name switch
    {
        "usb" => AuthenticatorTransport.Usb,
        "nfc" => AuthenticatorTransport.Nfc,
        "ble" => AuthenticatorTransport.Ble,
        "smart-card" => AuthenticatorTransport.SmartCard,
        "hybrid" => AuthenticatorTransport.Hybrid,
        "internal" => AuthenticatorTransport.Internal,
        _ => null
    };
}
#endif
