using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Leistd.Email.Tests.Smtp;

/// <summary>只接一个连接的本机 SMTP 应答：按脚本回复，让测试驱动真实 MailKit 走到指定协议阶段。</summary>
internal sealed class LoopbackSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _serving;

    /// <param name="dataReply">收到正文结束符后的回复；<see langword="null"/> 表示不回复（挂住客户端）。</param>
    /// <param name="onDataEnd">收到正文结束符、回复之前调用。</param>
    /// <param name="onQuit">收到 QUIT 时调用。</param>
    /// <param name="replyToQuit">是否回复 QUIT；<see langword="false"/> 让客户端等到读超时或取消。</param>
    public LoopbackSmtpServer(
        string? dataReply = "250 2.0.0 accepted",
        Action? onDataEnd = null,
        Action? onQuit = null,
        bool replyToQuit = true)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = ServeAsync(dataReply, onDataEnd, onQuit, replyToQuit);
    }

    public int Port { get; }

    /// <summary>服务器是否已回复接受正文。</summary>
    public bool Accepted { get; private set; }

    private async Task ServeAsync(string? dataReply, Action? onDataEnd, Action? onQuit, bool replyToQuit)
    {
        using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 localhost ESMTP");
        var inData = false;
        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            if (inData)
            {
                if (line != ".")
                {
                    continue;
                }

                inData = false;
                onDataEnd?.Invoke();
                if (dataReply is null)
                {
                    await HoldAsync();
                    return;
                }

                Accepted = dataReply.StartsWith('2');
                await writer.WriteLineAsync(dataReply);
            }
            else if (line.StartsWith("EHLO", StringComparison.Ordinal) || line.StartsWith("HELO", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("250 localhost");
            }
            else if (line == "DATA")
            {
                inData = true;
                await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
            }
            else if (line == "QUIT")
            {
                onQuit?.Invoke();
                if (!replyToQuit)
                {
                    await HoldAsync();
                    return;
                }

                await writer.WriteLineAsync("221 bye");
                return;
            }
            else
            {
                // MAIL FROM、RCPT TO 等一律接受
                await writer.WriteLineAsync("250 OK");
            }
        }
    }

    // 不回复也不断开，直到客户端自己断开或夹具释放
    private async Task HoldAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _serving;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }
}
