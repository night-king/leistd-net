using System.Net;

namespace Leistd.TestBase.Doubles;

/// <summary>捕获出站请求并按委托应答的终端处理器，替代真实网络。</summary>
/// <example>
/// <code>
/// var handler = new CapturingHttpMessageHandler();
/// var client = new HttpClient(handler) { BaseAddress = new Uri("https://svc.test") };
/// await client.GetAsync("/ping");
/// Assert.Equal("/ping", handler.Requests.Single().RequestUri!.AbsolutePath);
/// </code>
/// </example>
/// <remarks>
/// 请求体在 <c>SendAsync</c> 里就读成字符串存进 <see cref="RequestBodies"/>：
/// <see cref="HttpRequestMessage.Content"/> 在 <c>HttpClient</c> 返回后可能已被释放，
/// 断言阶段再读会拿到空流。
/// </remarks>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    /// <summary>按发出顺序记录的请求。</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>与 <see cref="Requests"/> 一一对应的请求体；无请求体时为 <c>null</c>。</summary>
    public List<string?> RequestBodies { get; } = [];

    /// <summary>应答生成器；缺省恒返回 200。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));

        var response = Responder(request);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>恒抛异常的终端处理器：用于验证调用方对传输层故障的处理。</summary>
public sealed class ThrowingHttpMessageHandler(Func<CancellationToken, Exception> exceptionFactory)
    : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw exceptionFactory(cancellationToken);
}
