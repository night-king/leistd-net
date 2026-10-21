using System.Text.Json.Serialization;

namespace Leistd.ExceptionHandling;

/// <summary>单条字段级错误：框架内唯一的字段错误形状。</summary>
/// <remarks>
/// 供 Problem Details、响应信封与远端错误还原共用。
/// 属性名遵循宿主序列化策略；未设置的 <c>Code</c> 从 JSON 中省略。
/// </remarks>
/// <param name="Detail">本地化后的人类可读消息。</param>
/// <param name="Field">出错字段路径（如 MVC ModelState 键 <c>Address.Street</c>）。</param>
/// <param name="Code">错误码兼展示词条键，前端可据此分支；抛出方未设置时为 <see langword="null"/>。</param>
public sealed record ErrorItem(
    string Detail,
    string Field,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code);
