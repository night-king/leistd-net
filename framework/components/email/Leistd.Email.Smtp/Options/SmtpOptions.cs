namespace Leistd.Email.Smtp.Options;

/// <summary>SMTP 发信选项。</summary>
/// <remarks>注册发送器时启用启动期配置校验；连接与认证在发送时验证。</remarks>
public sealed class SmtpOptions
{
    /// <summary>配置节名称 <c>Leistd:Email:Smtp</c>。</summary>
    public const string SectionName = "Leistd:Email:Smtp";

    /// <summary>SMTP 主机，必填。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP 端口，默认 587。</summary>
    public int Port { get; set; } = 587;

    /// <summary>登录用户名；为空表示匿名投递。与 <see cref="Password"/> 必须同时给出或同时留空。</summary>
    public string? Username { get; set; }

    /// <summary>登录口令；语义见 <see cref="Username"/>。</summary>
    /// <remarks>不要写进随代码分发的配置文件，用环境变量、用户机密或密钥管理服务注入。</remarks>
    public string? Password { get; set; }

    /// <summary>是否启用传输层加密，默认 <see langword="true"/>。</summary>
    /// <remarks>
    /// 启用时按端口选择握手方式：465 用连接即 TLS（隐式 TLS），其余端口用 STARTTLS。
    /// 关闭后凭据与正文以明文过网，仅适用于本机的开发用收信服务。
    /// </remarks>
    public bool EnableSsl { get; set; } = true;

    /// <summary>默认发件地址，必填。</summary>
    /// <remarks><see cref="Abstractions.EmailMessage.FromAddress"/> 未指定时使用本值。</remarks>
    public string DefaultFromAddress { get; set; } = string.Empty;

    /// <summary>默认发件显示名。</summary>
    public string? DefaultFromName { get; set; }
}
