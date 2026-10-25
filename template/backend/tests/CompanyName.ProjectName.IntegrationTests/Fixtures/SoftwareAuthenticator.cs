#if (LocalIdentity)
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>测试用的软件认证器：按服务端给的选项造出浏览器会交回的通行密钥响应（ES256、证明为 none）。</summary>
/// <remarks>
/// 响应经服务端的真实协议校验（挑战、来源、RP ID 摘要、签名、计数器），不打桩：
/// 造错了任何一项，测试里看到的就是服务端会拒绝的那种失败。
/// </remarks>
public static class SoftwareAuthenticator
{
    /// <summary>测试宿主配置的页面来源，与 <c>Passkeys:Origins</c> 一致。</summary>
    public const string Origin = "https://localhost";

    /// <summary>按创建选项登记一把新凭据，返回浏览器会交回的响应 JSON 与握着私钥的通行密钥。</summary>
    public static (JsonObject Response, SoftwarePasskey Passkey) Create(JsonElement options, bool discoverable = true, bool backupEligible = false)
    {
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        var challenge = options.GetProperty("challenge").GetString()!;
        var userHandle = Base64Url.DecodeFromChars(options.GetProperty("user").GetProperty("id").GetString()!);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var passkey = new SoftwarePasskey(credentialId, key, rpId, discoverable ? userHandle : null, backupEligible);

        var clientData = ClientData("webauthn.create", challenge);
        var parameters = key.ExportParameters(false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);   // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);  // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);  // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();

        var attested = new List<byte>();
        attested.AddRange(new byte[16]); // AAGUID：证明为 none 时全零
        attested.AddRange([(byte)(credentialId.Length >> 8), (byte)credentialId.Length]);
        attested.AddRange(credentialId);
        attested.AddRange(cose.Encode());
        var authData = passkey.AuthenticatorData(0x40, attested.ToArray());

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        var response = new JsonObject
        {
            ["id"] = Base64Url.EncodeToString(credentialId),
            ["rawId"] = Base64Url.EncodeToString(credentialId),
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
                ["transports"] = new JsonArray("internal")
            },
            ["clientExtensionResults"] = new JsonObject()
        };
        return (response, passkey);
    }

    internal static byte[] ClientData(string type, string challenge, string origin = Origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));
}

/// <summary>经接口登记通行密钥的便捷方法（已登录的会话）。</summary>
public static class PasskeyRequests
{
    /// <summary>取创建选项、用软件认证器创建并交回，返回登记结果与通行密钥。</summary>
    public static async Task<(JsonElement Result, SoftwarePasskey Passkey)> RegisterAsync(
        HttpClient session,
        string name = "Test passkey",
        bool discoverable = true)
    {
        var options = await BeginAsync(session);
        var (response, passkey) = SoftwareAuthenticator.Create(options, discoverable);
        using var complete = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/passkeys", new { credential = response, name });
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
        return (await complete.Content.ReadFromJsonAsync<JsonElement>(), passkey);
    }

    /// <summary>取登记用的创建选项。</summary>
    public static async Task<JsonElement> BeginAsync(HttpClient session, object? proof = null)
    {
        using var begin = await session.PostAsJsonAsync("/api/v1/auth/me/two-factor/passkeys/options", proof ?? new { });
        Assert.True(begin.StatusCode == HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
    }
}

/// <summary>握着私钥的一把软件通行密钥。</summary>
public sealed class SoftwarePasskey(byte[] credentialId, ECDsa key, string rpId, byte[]? userHandle, bool backupEligible)
{
    private uint _signCount;

    /// <summary>凭据 Id（base64url）。</summary>
    public string Id { get; } = Base64Url.EncodeToString(credentialId);

    /// <summary>可发现凭据会在断言里带回用户句柄（免口令登录靠它认出用户）。</summary>
    public byte[]? UserHandle => userHandle;

    /// <summary>按断言选项签一次名，返回浏览器会交回的响应 JSON。</summary>
    /// <param name="options">服务端给的断言选项。</param>
    /// <param name="signCount">指定计数器（测回退时用）；不给时在上一次的基础上加一。</param>
    /// <param name="origin">页面来源（测来源不符时用）。</param>
    /// <param name="userVerified">是否做了用户验证。</param>
    public JsonObject Sign(JsonElement options, uint? signCount = null, string origin = SoftwareAuthenticator.Origin, bool userVerified = true)
    {
        _signCount = signCount ?? _signCount + 1;
        var clientData = SoftwareAuthenticator.ClientData("webauthn.get", options.GetProperty("challenge").GetString()!, origin);
        var authData = AuthenticatorData(0, [], userVerified);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new JsonObject
        {
            ["id"] = Id,
            ["rawId"] = Id,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString(authData),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = userHandle is null ? null : Base64Url.EncodeToString(userHandle)
            },
            ["clientExtensionResults"] = new JsonObject()
        };
    }

    // 标志位：UP 0x01、UV 0x04、BE 0x08、BS 0x10，登记时另加 AT 0x40
    internal byte[] AuthenticatorData(byte extraFlags, byte[] attested, bool userVerified = true)
    {
        var flags = (byte)(0x01 | (userVerified ? 0x04 : 0) | (backupEligible ? 0x18 : 0) | extraFlags);
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, _signCount);
        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, .. counter, .. attested];
    }
}
#endif
