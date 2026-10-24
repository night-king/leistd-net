#if (Email)
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Leistd.Email.Abstractions;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>收下每封验证码邮件里的码，按收件地址排队；不带码的信（安全提醒等）只记收件人。</summary>
/// <remarks>
/// 绑定账号的验证码（找回口令、登录第二步、开启邮箱方式）在后台投递，发码请求返回时信未必已到：
/// 取码一律等到达，不读"此刻有几封"。要断言某地址没收到信，先等一封确定会到的信，再看收件人顺序。
/// </remarks>
public sealed partial class CodeMailbox : IEmailSender
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, CodeQueue> _inbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _codeRecipients = new();

    /// <summary>收到过验证码的地址，按到达顺序。</summary>
    public IReadOnlyList<string> CodeRecipients => [.. _codeRecipients];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var match = CodeRegex().Match(message.Body);
        if (match.Success)
        {
            _codeRecipients.Enqueue(message.To);
            Queue(message.To).Add(match.Groups[1].Value);
        }

        return Task.CompletedTask;
    }

    /// <summary>等下一封到达并取出它的码。</summary>
    public Task<string> NextCodeAsync(string email) => Queue(email).TakeAsync();

    private CodeQueue Queue(string email) => _inbox.GetOrAdd(email, _ => new CodeQueue());

    [GeneratedRegex(@">(\d{6})<", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    private sealed class CodeQueue
    {
        private readonly ConcurrentQueue<string> _items = new();
        private readonly SemaphoreSlim _available = new(0);

        public void Add(string code)
        {
            _items.Enqueue(code);
            _available.Release();
        }

        public async Task<string> TakeAsync()
        {
            Assert.True(await _available.WaitAsync(DeliveryTimeout), "No verification code email arrived in time.");
            Assert.True(_items.TryDequeue(out var code));
            return code!;
        }
    }
}
#endif
