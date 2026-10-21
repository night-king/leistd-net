using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>设置自己的头像。</summary>
public record SetAvatarInputDto
{
    /// <summary>
    /// 图片的 data URL（PNG / JPEG / WebP，浏览器端已裁剪缩放）；为空表示清除头像。
    /// 本人入口只收上传的图片，外部地址只来自外部登录提供方；体积与真实类型由 <c>AvatarPolicy</c> 核对。
    /// </summary>
    [Display(Name = "Avatar")]
    [RegularExpression("^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/=]*$", ErrorMessage = "{0} must be a PNG, JPEG or WebP image.")]
    public string? Avatar { get; init; }
}
