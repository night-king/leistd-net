namespace CompanyName.ProjectName.Application.Users.Dtos;

/// <summary>用户上传的头像图片。</summary>
/// <param name="ContentType">图片类型，已按文件头核实（见 <c>AvatarPolicy</c>）。</param>
/// <param name="Content">图片内容。</param>
public sealed record UserAvatarOutputDto(string ContentType, byte[] Content);
