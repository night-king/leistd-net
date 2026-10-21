using Leistd.ExceptionHandling;
using System.Globalization;
using System.Text.Json;
using Leistd.ServiceClient.Exceptions;

namespace Leistd.ServiceClient.Http;

/// <summary>服务调用响应读取扩展：裸载荷读取、信封解包与远端错误还原。</summary>
/// <remarks>
/// <see cref="ReadContentAsync{T}"/> 读取裸载荷；<see cref="ReadResultAsync{T}"/> 用于远端信封。
/// 两者均通过 <see cref="CreateRemoteErrorAsync"/> 还原远端错误。
/// </remarks>
public static class HttpResponseMessageExtensions
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    // 异常消息与 RemoteServiceException.ResponseBody 中原始响应体的最大保留长度。
    private const int MaxBodySnippetLength = 4096;

    /// <summary>
    /// 读取 <c>{code, message, data}</c> 信封并返回 <c>data</c>，用于调用产出信封的服务。
    /// 非 2xx 或 <c>code != 0</c> 抛 <see cref="RemoteServiceException"/>；
    /// 响应体为空或不是有效 JSON 抛 <see cref="ServiceClientException"/>。
    /// </summary>
    /// <remarks>被调方直出裸载荷时用 <see cref="ReadContentAsync{T}"/>。</remarks>
    /// <typeparam name="T">信封 <c>data</c> 的类型。</typeparam>
    /// <param name="response">HTTP 响应。</param>
    /// <param name="jsonOptions">序列化选项；默认 <see cref="JsonSerializerDefaults.Web"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<T?> ReadResultAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRemoteSuccessAsync(response, cancellationToken);

        var envelope = await DeserializeAsync<ResultEnvelope<T>>(response, jsonOptions, cancellationToken);
        EnsureEnvelopeSuccess(response, envelope);
        return envelope.Data;
    }

    /// <summary>读取无数据负载的信封；非 2xx 或 <c>code != 0</c> 抛 <see cref="RemoteServiceException"/>。</summary>
    /// <param name="response">HTTP 响应。</param>
    /// <param name="jsonOptions">序列化选项；默认 <see cref="JsonSerializerDefaults.Web"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task ReadResultAsync(
        this HttpResponseMessage response,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRemoteSuccessAsync(response, cancellationToken);

        var envelope = await DeserializeAsync<ResultEnvelope<object>>(response, jsonOptions, cancellationToken);
        EnsureEnvelopeSuccess(response, envelope);
    }

    /// <summary>
    /// 直接把响应体反序列化为 <typeparamref name="T"/>，调用本框架服务的常规路径。
    /// 非 2xx 抛 <see cref="RemoteServiceException"/>。文件流场景请直接使用
    /// <see cref="HttpResponseMessage.Content"/>，先调用 <see cref="EnsureRemoteSuccessAsync"/> 检查错误。
    /// </summary>
    /// <typeparam name="T">响应内容类型。</typeparam>
    /// <param name="response">HTTP 响应。</param>
    /// <param name="jsonOptions">序列化选项；默认 <see cref="JsonSerializerDefaults.Web"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<T?> ReadContentAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRemoteSuccessAsync(response, cancellationToken);
        return await DeserializeAsync<T>(response, jsonOptions, cancellationToken);
    }

    /// <summary>确保远端响应成功，否则抛出 <see cref="RemoteServiceException"/>。</summary>
    public static async Task EnsureRemoteSuccessAsync(
        this HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await CreateRemoteErrorAsync(response, cancellationToken);
    }

    /// <summary>从非 2xx 响应创建 <see cref="RemoteServiceException"/>；无法解析时保留截断后的原始响应体。</summary>
    public static async Task<RemoteServiceException> CreateRemoteErrorAsync(
        this HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RemoteServiceException(
                $"{Describe(response)} the remote returned an error and the response body could not be read: {ex.Message}",
                (int)response.StatusCode,
                innerException: ex);
        }

        var snippet = Truncate(body);
        string? errorCode = null;
        string? message = null;
        string? traceId = null;
        List<ErrorItem>? errors = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    errorCode = TryGetErrorCode(root);
                    message = TryGetString(root, "message") ?? TryGetString(root, "detail") ?? TryGetString(root, "title");
                    traceId = TryGetString(root, "traceId");
                    errors = TryGetErrors(root);
                }
            }
            catch (JsonException)
            {
                // 网关可能返回 HTML，异常仍需保留可诊断的原始片段。
            }
        }

        return new RemoteServiceException(
            $"{Describe(response)} the remote returned an error: {message ?? snippet}" +
            (traceId is null ? string.Empty : $" (remote traceId: {traceId})"),
            (int)response.StatusCode,
            errorCode,
            traceId,
            errors,
            snippet);
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        JsonSerializerOptions? jsonOptions,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new ServiceClientException($"{Describe(response)} response body could not be read: {ex.Message}", ex,
                ServiceClientFailureClassifier.Classify(ex));
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ServiceClientException($"{Describe(response)} response body is empty",
                failureKind: ServiceClientFailureKind.InvalidResponse);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(body, jsonOptions ?? DefaultJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ServiceClientException($"{Describe(response)} response deserialization failed: {ex.Message}", ex,
                ServiceClientFailureKind.InvalidResponse);
        }

        return value ?? throw new ServiceClientException($"{Describe(response)} response body is empty",
            failureKind: ServiceClientFailureKind.InvalidResponse);
    }

    private static void EnsureEnvelopeSuccess<T>(HttpResponseMessage response, ResultEnvelope<T> envelope)
    {
        if (envelope.Code == 0)
        {
            return;
        }

        throw new RemoteServiceException(
            $"{Describe(response)} remote business failure: code={envelope.Code} {envelope.Message}",
            (int)response.StatusCode,
            envelope.Code.ToString(CultureInfo.InvariantCulture),
            errors: envelope.Errors);
    }

    // 远端信封仅作为反序列化形状，不使客户端依赖本服务的响应组件。
    private sealed class ResultEnvelope<T>
    {
        public int Code { get; init; }

        public string? Message { get; init; }

        public T? Data { get; init; }

        // 信封的失败形态带字段级明细，形状与 Problem Details 的 errors 一致。
        public List<ErrorItem>? Errors { get; init; }
    }

    private static string Describe(HttpResponseMessage response)
    {
        var request = response.RequestMessage;
        return $"{request?.Method} {(int)response.StatusCode} {request?.RequestUri}";
    }

    private static string Truncate(string value) =>
        value.Length <= MaxBodySnippetLength ? value : value[..MaxBodySnippetLength];

    // Problem Details 把业务码放在字符串 code，数字信封放在 errorCode。
    // 信封里的数字 code 是 HTTP 状态，不是业务码，不参与还原。
    private static string? TryGetErrorCode(JsonElement root)
    {
        if (root.TryGetProperty("errorCode", out var envelopeCode) &&
            envelopeCode.ValueKind == JsonValueKind.String)
        {
            return envelopeCode.GetString();
        }

        return root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    // 两种形状都认：Leistd 的数组 [{field, detail, code}]（逐字段带错误码），
    // 与官方 HttpValidationProblemDetails 的字典 {字段: [消息…]}（AddValidation、MVC 默认校验产出）。
    // 字典的每条消息各成一项，保留字段名与全部消息，不在解析时取舍。
    private static List<ErrorItem>? TryGetErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errorsElement))
        {
            return null;
        }

        return errorsElement.ValueKind switch
        {
            JsonValueKind.Array => ReadArray(errorsElement),
            JsonValueKind.Object => ReadDictionary(errorsElement),
            _ => null
        };
    }

    private static List<ErrorItem> ReadArray(JsonElement errors)
    {
        var items = new List<ErrorItem>();
        foreach (var item in errors.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // ErrorItem 的位置参数是 (Detail, Field, Code)，与 JSON 里 field/detail 的书写顺序相反；
            // 且两者非空，远端漏字段时补空串而不是抛——解析远端载荷要容错。
            items.Add(new ErrorItem(
                TryGetString(item, "detail") ?? string.Empty,
                TryGetString(item, "field") ?? string.Empty,
                TryGetString(item, "code")));
        }

        return items;
    }

    private static List<ErrorItem> ReadDictionary(JsonElement errors)
    {
        var items = new List<ErrorItem>();
        foreach (var field in errors.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in field.Value.EnumerateArray())
                {
                    if (message.ValueKind == JsonValueKind.String)
                    {
                        items.Add(new ErrorItem(message.GetString() ?? string.Empty, field.Name, null));
                    }
                }
            }
            else if (field.Value.ValueKind == JsonValueKind.String)
            {
                items.Add(new ErrorItem(field.Value.GetString() ?? string.Empty, field.Name, null));
            }
        }

        return items;
    }
}
