using Leistd.ExceptionHandling;
using CompanyName.ProjectName.Domain.Users.Errors;

namespace CompanyName.ProjectName.Domain.Users.Policies;

/// <summary>头像取值的唯一规则：外部地址（http/https，来自外部登录提供方等），或站内上传的图片（data URL）。</summary>
/// <remarks>
/// <para>上传的图片存在 <c>User.Avatar</c> 里，不另建文件存储：头像是每人一张的小图，
/// 为它引入对象存储得不偿失。代价由两条约束兜住——体积上限（浏览器端先裁剪缩放，
/// 服务端只校验不处理），以及对外只给带版本号的地址、不把图片内联进 DTO（见 <c>AvatarUrls</c>）。</para>
/// <para>服务端不做图像处理：解码再编码需要图像库，而常见的几个都有许可证约束。
/// 这里只核对魔数与声明的类型一致，挡住"声明是图片、内容不是"的上传。</para>
/// </remarks>
public static class AvatarPolicy
{
    /// <summary>上传图片解码后的字节上限。浏览器端裁到 256×256 再编码，实际远小于它。</summary>
    public const int MaxImageBytes = 256 * 1024;

    /// <summary>外部地址的长度上限，与外部登录记录里的头像地址列同宽。</summary>
    public const int MaxUrlLength = 1024;

    private const string DataUrlPrefix = "data:";
    private const string Base64Marker = ";base64,";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>是外部 http(s) 地址。</summary>
    public static bool IsExternalUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxUrlLength &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>读取站内上传的图片；不是合法的图片 data URL（含超限、类型与内容不符）时返回 false。</summary>
    public static bool TryReadImage(string? value, out AvatarImage image)
    {
        image = default;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
            return false;

        var markerIndex = value.IndexOf(Base64Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var contentType = value[DataUrlPrefix.Length..markerIndex].ToLowerInvariant();
        var payload = value[(markerIndex + Base64Marker.Length)..];

        // 先按 base64 长度估算，超限的不必解码
        if (payload.Length / 4 * 3 > MaxImageBytes + 3)
            return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length == 0 || bytes.Length > MaxImageBytes || DetectContentType(bytes) != contentType)
            return false;

        image = new AvatarImage(contentType, bytes);
        return true;
    }

    /// <summary>校验一个待写入的头像值：空（清除）、外部地址或合法的上传图片。</summary>
    /// <exception cref="BusinessException">不合规时抛出，并说明是格式不对还是体积超限。</exception>
    public static void EnsureValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsExternalUrl(value) || TryReadImage(value, out _))
            return;

        if (value.StartsWith(DataUrlPrefix, StringComparison.Ordinal) && value.Length / 4 * 3 > MaxImageBytes + 3)
        {
            throw new BusinessException(UserErrorCodes.AvatarTooLarge, $"The avatar image cannot exceed {MaxImageBytes / 1024} KB.")
                .WithData("MaxKilobytes", MaxImageBytes / 1024);
        }

        throw new BusinessException(UserErrorCodes.AvatarInvalid, "The avatar must be a PNG, JPEG or WebP image.");
    }

    // 按文件头判断真实类型，不信任 data URL 里声明的类型。
    private static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }
}

/// <summary>站内上传的头像图片。</summary>
/// <param name="ContentType">经文件头核实的媒体类型。</param>
/// <param name="Bytes">图片内容。</param>
public readonly record struct AvatarImage(string ContentType, byte[] Bytes);
