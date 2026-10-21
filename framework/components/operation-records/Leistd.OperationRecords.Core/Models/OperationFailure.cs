using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Leistd.OperationRecords.Models;

/// <summary>一次操作失败的原因。</summary>
/// <remarks>
/// <para>可枚举的业务规则拒绝存 <see cref="Code"/> + <see cref="Data"/>，查询时按读者的请求语言渲染（见
/// <see cref="Dtos.OperationRecordOutputDto.FailureMessage"/>）；不可枚举的技术异常走 <see cref="Detail"/>，不本地化。</para>
/// <para>不提供接受 <see cref="Exception"/> 的工厂：多租户下租户管理员直接阅读这张表，原始异常文本可能带表名、
/// 内部地址或连接串，调用方须逐次决定哪段文字可以公开。</para>
/// <para>业务异常显式传入码与参数：<c>OperationFailure.FromCode(exception.Code, exception.LocalizationData)</c>；
/// 不要带入 <see cref="Exception.Data"/> 与异常文本。</para>
/// </remarks>
public readonly record struct OperationFailure
{
    private OperationFailure(string? code, string? data, string? detail)
    {
        Code = code;
        Data = data;
        Detail = detail;
    }

    /// <summary>失败原因的稳定错误码，兼本地化资源键（如 <c>Order:AlreadyShipped</c>）。</summary>
    public string? Code { get; }

    /// <summary>失败原因文案的本地化占位参数，序列化为扁平 JSON 对象；无参数时为 <see langword="null"/>。</summary>
    public string? Data { get; }

    /// <summary>面向排查的技术说明，不本地化，仅宿主可见。</summary>
    public string? Detail { get; }

    /// <summary>没有失败原因（成功路径，或原因不适用）。</summary>
    public static OperationFailure None { get; } = new(null, null, null);

    /// <summary>是否携带了任何原因信息。</summary>
    public bool IsEmpty => Code is null && Detail is null;

    /// <summary>由错误码与可选的本地化参数构造。</summary>
    /// <param name="code">错误码兼资源键。为空白时退化为 <see cref="None"/>。</param>
    /// <param name="data">
    /// 本地化占位参数，键与词条里的占位名一一对应。值必须是标量
    /// （字符串、布尔、数值、日期、<see cref="Guid"/> 或自报格式的类型）；
    /// 其余类型只写类型名、不写内容。
    /// </param>
    public static OperationFailure FromCode(string? code, IReadOnlyDictionary<string, object?>? data = null)
        => string.IsNullOrWhiteSpace(code)
            ? None
            : new(code.Trim(), SerializeData(data), null);

    /// <summary>由技术说明构造，用于不可枚举的异常。</summary>
    /// <remarks>
    /// 调用方对内容负责：不要传 <c>exception.ToString()</c> 或原始异常消息，而应传一句确认可以公开的说明，例如“调用支付网关超时”。
    /// 日志模式下它原样进入日志，同样不能带联系方式等不能进日志的值。
    /// </remarks>
    /// <param name="detail">可公开的技术说明。</param>
    public static OperationFailure FromDetail(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? None : new(null, null, detail.Trim());

    /// <summary>同时带错误码与技术说明。</summary>
    /// <inheritdoc cref="FromCode" path="/param[@name='data']"/>
    public static OperationFailure Create(string? code, IReadOnlyDictionary<string, object?>? data, string? detail)
        => new(
            string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            SerializeData(data),
            string.IsNullOrWhiteSpace(detail) ? null : detail.Trim());

    // 不用 JsonSerializer.Serialize(data)：反射序列化会摊开复杂对象，而这一列租户可读、会进导出。
    // 只写标量，其余只写类型名；词条占位符 {X} 本就渲染不了嵌套对象。
    private static string? SerializeData(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || data.Count == 0)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in data)
            {
                writer.WritePropertyName(name);
                WriteScalar(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            // NaN 与正负无穷不是合法 JSON 数值，Utf8JsonWriter 会抛异常；按字面量写成字符串
            case float or double when !double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)):
                writer.WriteStringValue(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            case float or double:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime moment:
                writer.WriteStringValue(moment);
                break;
            case DateTimeOffset moment:
                writer.WriteStringValue(moment);
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            // 自报格式的类型（枚举、TimeSpan、自定义数值……）按区域无关字面量写出
            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            // 其余一律只写类型名、不写内容：匿名类型与 record 的 ToString() 会输出全部成员值
            default:
                writer.WriteStringValue($"[{value.GetType().Name}]");
                break;
        }
    }
}
