using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace Leistd.ExceptionHandling.AspNetCore.Validation;

// 把模型状态的键换成各成员的校验模型名（跟随 JSON 命名策略），作为 errors[].field。
// 校验器按校验模型名记错误，但模型状态的键不区分大小写：查询参数、表单这类逐属性绑定的来源在校验之前已按
// C# 属性名建好条目（Roles），之后记到 roles 上的错误并入原条目，键仍是属性名；IValidatableObject 的成员名也是 C# 属性名。
// 这里按动作参数的元数据逐段换算；对不上任何属性的段（JSON 路径、自造成员名、简单参数名）原样保留。
internal static class ValidationFieldNames
{
    public static string Resolve(ActionContext context, IModelMetadataProvider metadataProvider, string key)
    {
        if (key.Length == 0)
            return key;

        foreach (var parameter in context.ActionDescriptor.Parameters)
        {
            var metadata = metadataProvider.GetMetadataForType(parameter.ParameterType);
            var prefix = parameter.BindingInfo?.BinderModelName ?? parameter.Name;

            // 参数自身的键（简单参数、整个对象读不成）：参数名不随命名策略
            if (string.Equals(key, prefix, StringComparison.OrdinalIgnoreCase))
                return key;

            // 带参数名前缀的键（input.Roles）：前缀照旧，其后逐段换算
            if (key.Length > prefix.Length
                && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && key[prefix.Length] is '.' or '['
                && TryResolvePath(metadata, key, prefix.Length, out var prefixed))
            {
                return prefixed;
            }

            // 回落到空前缀绑定的键（Roles）：首段须是该参数类型的属性
            if (TryResolvePath(metadata, key, 0, out var resolved))
                return resolved;
        }

        return key;
    }

    private static bool TryResolvePath(ModelMetadata root, string key, int start, out string resolved)
    {
        var builder = new StringBuilder(key, 0, start, key.Length);
        ModelMetadata? current = root;
        var index = start;
        var matchedAny = false;

        while (index < key.Length)
        {
            if (key[index] == '[')
            {
                // 下标或字典键原样保留，元数据进入元素类型
                var end = key.IndexOf(']', index);
                if (end < 0)
                    break;
                builder.Append(key, index, end - index + 1);
                current = current?.ElementMetadata;
                index = end + 1;
                continue;
            }

            if (key[index] == '.')
            {
                builder.Append('.');
                index++;
                continue;
            }

            var nameEnd = key.IndexOfAny(['.', '['], index);
            if (nameEnd < 0)
                nameEnd = key.Length;
            var name = key[index..nameEnd];
            var property = current is { IsComplexType: true } ? FindProperty(current, name) : null;
            if (property is null)
            {
                // 首段就对不上：不是这个参数的键
                if (!matchedAny)
                {
                    resolved = key;
                    return false;
                }

                break;
            }

            builder.Append(ValidationModelName(property));
            matchedAny = true;
            current = property;
            index = nameEnd;
        }

        // 对不上属性的剩余部分原样保留
        builder.Append(key, index, key.Length - index);
        resolved = builder.ToString();
        return matchedAny;
    }

    private static ModelMetadata? FindProperty(ModelMetadata container, string name)
    {
        foreach (var property in container.Properties)
        {
            // 已是校验模型名（如请求体的错误），或是绑定时的名称（不区分大小写，与模型状态一致）
            if (string.Equals(ValidationModelName(property), name, StringComparison.Ordinal)
                || string.Equals(property.BinderModelName ?? property.PropertyName, name, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    // 与 MVC 为属性拼校验键的取法一致（校验模型名 → 绑定名 → 属性名）。校验模型名在 ModelMetadata 上是内部成员，
    // 经默认元数据公开的 ValidationMetadata 读取。
    internal static string ValidationModelName(ModelMetadata property) =>
        (property as DefaultModelMetadata)?.ValidationMetadata.ValidationModelName
        ?? property.BinderModelName
        ?? property.PropertyName
        ?? string.Empty;
}
