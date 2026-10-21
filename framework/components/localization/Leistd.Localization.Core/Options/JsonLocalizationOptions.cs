using System.Reflection;
using Microsoft.Extensions.Localization;

namespace Leistd.Localization.Options;

/// <summary>Leistd JSON 本地化选项。</summary>
/// <remarks>
/// 资源为随程序集嵌入的 JSON 文件（顶层 <c>culture</c> + <c>texts</c> 两段结构），
/// 按 <see cref="ResourcesPath"/> 下的 <c>{culture}.json</c> 命名；查表时按 culture 回落到 <see cref="DefaultCulture"/>。
/// </remarks>
public sealed class JsonLocalizationOptions
{
    /// <summary>承载嵌入 JSON 资源的程序集；同一键在多个程序集出现时后登记者覆盖前者。</summary>
    /// <remarks>只决定从哪些程序集加载词条，不决定 <see cref="IStringLocalizer{T}"/> 的路由（见 <see cref="JsonResourceTypes"/>）。</remarks>
    public IList<Assembly> ResourceAssemblies { get; } = [];

    /// <summary>使用 JSON 词条的资源标记类型。</summary>
    /// <remarks>未登记的强类型本地化器仍使用宿主的默认工厂；集合为空时不接管任何类型。</remarks>
    public ISet<Type> JsonResourceTypes { get; } = new HashSet<Type>();

    /// <summary>嵌入资源相对程序集根的逻辑目录，默认 <c>Resources</c>（程序集 <c>Foo</c> 下 <c>Resources/en.json</c> 的清单名为 <c>Foo.Resources.en.json</c>）。</summary>
    public string ResourcesPath { get; set; } = "Resources";

    /// <summary>支持的语言，首项为默认与回落语言；默认 <c>["en", "zh-CN"]</c>。</summary>
    /// <remarks>
    /// 支持语言的唯一事实源：<see cref="DefaultCulture"/>、请求默认区域性与资源回落都取首项，
    /// 请求可选区域性与启动预热取全部。列表为空或含无效文化名时启动失败。
    /// </remarks>
    public IList<string> SupportedCultures { get; set; } = ["en", "zh-CN"];

    /// <summary>默认与回落语言，即 <see cref="SupportedCultures"/> 的首项。</summary>
    public string DefaultCulture => SupportedCultures[0];
}
