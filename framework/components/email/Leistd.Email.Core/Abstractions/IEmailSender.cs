namespace Leistd.Email.Abstractions;

/// <summary>邮件发送器。</summary>
/// <remarks>
/// <para>实际投递的实现以收到投递设施（如 SMTP 服务器）的接受确认为成功；确认之后的取消或连接善后故障不改写这一结果。
/// 正常返回不保证收件人最终收到。</para>
/// <para>取得确认之前的失败通过异常报告，由调用方决定补偿；异常不保证对方没有接受这封信（例如确认回复在途中丢失）。</para>
/// <para>不需要实际投递的环境可显式注册 <see cref="Leistd.Email.NullEmailSender"/>，它正常返回但不投递，不是失败兜底。</para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>发送邮件；取得接受确认前失败则抛异常。</summary>
    /// <param name="message">待发送的邮件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
