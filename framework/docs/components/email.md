# 邮件发送

提供统一的 `IEmailSender` 抽象、SMTP 投递与显式空发送器。

## 何时使用

| 场景 | 推荐实现 |
| --- | --- |
| 生产环境经 SMTP 发信（验证码、密码重置、系统通知） | `Leistd.Email.Smtp` |
| 本地开发或演示，没有可用 SMTP，且不需要看邮件内容 | `Leistd.Email.Core` 的 `AddNullEmailSender()` |
| 本地要看到邮件内容（验证码、重置链接） | 仍用 `Leistd.Email.Smtp`，指向本机邮件捕获器：`docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit` |
| 只写发信调用、不关心投递介质 | 只引用 `Leistd.Email.Core` 中的接口 |
| 需要队列、重试、退避 | 本组件不提供，由宿主组合 |
| 需要按模板渲染正文 | 本组件不提供。正文由调用方给出字符串 |

## 安装

```bash
dotnet add package Leistd.Email.Core

dotnet add package Leistd.Email.Smtp
```

## 注册

在 `Program.cs` 注册其中一种实现：

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddNullEmailSender();
}
else
{
    builder.Services.AddSmtpEmailSender();
}
```

两者都绑定 `IEmailSender`。`AddSmtpEmailSender` 绑定 `Leistd:Email:Smtp` 配置节（可传入自定义路径），可选的委托在绑定之后应用；配置在启动期校验（`ValidateOnStart`）。重复调用不会产生两个实例。

## 使用

注入 `IEmailSender`，构造 `EmailMessage` 发送：

```csharp
public class RegistrationService(IEmailSender emailSender)
{
    public async Task SendCodeAsync(string email, string code, CancellationToken ct)
    {
        await emailSender.SendAsync(new EmailMessage
        {
            To = email,
            Subject = "Account Registration Verification Code",
            Body = $"<p>Your verification code is <b>{code}</b>.</p>",
        }, ct);
    }
}
```

不指定发件人时使用配置里的默认发件身份；需要按业务线区分发件人时给出 `FromAddress`：

```csharp
await emailSender.SendAsync(new EmailMessage
{
    To = customer.Email,
    Subject = "Invoice",
    Body = html,
    FromAddress = "billing@acme.com",
    FromName = "Acme Billing",
}, ct);
```

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `IEmailSender` | 发信入口，业务层只依赖它 |
| `IEmailSender.SendAsync(message, ct)` | 发送一封邮件 |
| `EmailMessage.To` | 收件人地址（必填） |
| `EmailMessage.Subject` | 主题（必填） |
| `EmailMessage.Body` | 正文（必填） |
| `EmailMessage.IsBodyHtml` | 正文是否为 HTML，默认 `true` |
| `EmailMessage.FromAddress` | 发件地址；`null` 时用 `DefaultFromAddress` |
| `EmailMessage.FromName` | 发件显示名；语义与 `FromAddress` 成对，见「注意事项」 |
| `NullEmailSender` | 不投递、只按 Warning 记录的实现，由 `AddNullEmailSender()` 注册 |
| `SmtpEmailSender` | SMTP 实现，由 `AddSmtpEmailSender()` 注册 |

## 配置项（`Leistd:Email:Smtp`）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Host` | 空 | SMTP 主机。**必填**，留空时宿主启动失败 |
| `Port` | `587` | SMTP 端口，取值 1–65535 |
| `Username` | 空 | 登录用户名。留空表示匿名投递 |
| `Password` | 空 | 登录口令。**不要写进随代码分发的配置文件**，用环境变量、用户机密或密钥管理服务注入 |
| `EnableSsl` | `true` | 是否启用传输层加密。465 用连接即 TLS，其余端口用 STARTTLS |
| `DefaultFromAddress` | 空 | 默认发件地址。**必填**，`EmailMessage.FromAddress` 未指定时使用 |
| `DefaultFromName` | 空 | 默认发件显示名 |

```json
{
  "Leistd": {
    "Email": {
      "Smtp": {
        "Host": "smtp.acme.com",
        "Port": 587,
        "EnableSsl": true,
        "DefaultFromAddress": "noreply@acme.com",
        "DefaultFromName": "Acme Notifications"
      }
    }
  }
}
```

`Host`、`Port`、`DefaultFromAddress` 与「`Username`/`Password` 成对」四项在启动期校验，任一不过阻止宿主启动；地址按发送路径同一套 MimeKit 规则解析。

## 实现行为

实际投递的实现收到投递设施的接受确认后，`SendAsync` 正常返回，不保证收件人最终收到；收到确认之后的取消或断开故障不会再让它抛出。
尚未收到确认时，SMTP 连接、认证或投递失败均原样抛出，重试和补偿由调用方决定；
异常不保证服务器没有接受（例如接受回复在途中丢失），按失败补偿的调用方要能容忍偶发的"信已到、却按失败处理"。
显式注册的 `NullEmailSender` 例外：它正常返回但不投递。`NullEmailSender` 只能显式注册，不会在 SMTP 配置或发送失败时自动回落。

### Leistd.Email.Core（`NullEmailSender`）

- 不连接任何服务器，不投递，返回成功。
- 按 Warning 级别记录脱敏后的收件人与主题，不记录正文；要看内容请用 SMTP 指向本机邮件捕获器。
- 必须由宿主主动注册。

### Leistd.Email.Smtp（`SmtpEmailSender`）

- 每次发送新建一个 `SmtpClient`，发完 `QUIT` 断开，不复用连接。收到接受确认后，`QUIT` 被取消不影响成功；`QUIT` 超时记 Warning，发送仍算成功。
- `EnableSsl` 为 `true` 时按端口选握手方式：465 用隐式 TLS，其余用 STARTTLS。
- 仅在 `Username` 非空时认证；用户名与口令由校验保证成对。
- 每封信取 `IOptionsMonitor<SmtpOptions>.CurrentValue`，配置源重载后下一封信即用新值。新值同样经校验，不合规时发信抛 `OptionsValidationException`，
  触发重载的一方也会收到包着它的 `AggregateException`。

## 注意事项

- 给出 `FromAddress` 而不给 `FromName` 时显示名为空，不使用 `DefaultFromName`。
- `IsBodyHtml` 取错不会报错，只会让收件人看到转义后的 HTML 源码或没有排版的信。
- 组件不排队、不重试、不退避；一次 `SendAsync` 就是一次同步投递尝试，放在请求路径上时计入响应时间。
- `Password` 属于凭据，不要写进随代码分发的配置文件，也不要经面向界面的设置接口读写。
- 投递日志的 `{To}` 记脱敏后的收件人（`TextRedactor.RedactEmail`：`zhangsan@example.com` → `zha***@example.com`，规则见[核心原语](core.md)），
  取不出域名时也不回落成原文。按收件人追查时用操作记录或业务侧标识。
- 主题原样记录：不要把人名等个人数据放进主题（通知邮件的主题来自 `NotificationInputDto.Title`）。

## 相关

- [通知](./notifications.md)
- [异常处理](./exception-handling.md)
