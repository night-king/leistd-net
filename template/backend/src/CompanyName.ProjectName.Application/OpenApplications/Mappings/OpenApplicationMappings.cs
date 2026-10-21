#if (LocalIdentity)
using System.Text.Json;
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
using Mapster;
using OpenIddict.Abstractions;

namespace CompanyName.ProjectName.Application.OpenApplications.Mappings;

/// <summary>开放应用映射配置。</summary>
/// <remarks>
/// 源用 <see cref="OpenIddictApplicationDescriptor"/> 而不是 OpenIddict 的应用实体：实体是
/// <c>object</c>，字段只能逐个 <c>await</c> 取。<c>Id</c> 不在 descriptor 上，经
/// <see cref="IdKey"/> 由调用方传入。
/// </remarks>
public class OpenApplicationMappings : IRegister
{
    /// <summary>MapContext 参数名：应用 Id（不在 descriptor 上）。</summary>
    public const string IdKey = "OpenApplicationId";

    /// <summary>扩展属性里记录创建时间的键。</summary>
    public const string CreationTimePropertyName = "creationTime";

    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<OpenIddictApplicationDescriptor, OpenApplicationOutputDto>()
            .Map(dest => dest.Id, src => ResolveId())
            .Map(dest => dest.ClientId, src => src.ClientId ?? string.Empty)
            // 类型字段在 OpenIddict 侧可空，对外契约不可空：兜到与创建时相同的默认值
            .Map(dest => dest.ApplicationType, src => src.ApplicationType ?? OpenIddictConstants.ApplicationTypes.Web)
            .Map(dest => dest.ClientType, src => src.ClientType ?? OpenIddictConstants.ClientTypes.Public)
            .Map(dest => dest.RedirectUris, src => ToStrings(src.RedirectUris))
            .Map(dest => dest.PostLogoutRedirectUris, src => ToStrings(src.PostLogoutRedirectUris))
            // ClientSecret 必须显式忽略：源与目标同名，Mapster 会按约定自动映射，
            // 而 PopulateAsync 填的是**存储值**（通常是散列）。DTO 的契约是"仅创建/重置时
            // 返回一次明文"，由那两条路径在映射之后显式赋值，查询路径一律为 null。
            // 只对外暴露"有没有配过密钥"这一个布尔量
            // ! 不能删：Ignore 的表达式返回 object，可空成员在此会触发 CS8603；这里只是点名成员，不取值
            .Ignore(dest => dest.ClientSecret!)
            .Map(dest => dest.HasClientSecret, src => !string.IsNullOrEmpty(src.ClientSecret))
            .Map(dest => dest.CreationTime, src => ReadCreationTime(src.Properties))
            .Map(dest => dest.SessionBound, src => OpenApplicationSettings.ReadSessionBound(src.Settings));
    }

    private static string ResolveId()
        => MapContext.Current?.Parameters.TryGetValue(IdKey, out var id) == true && id is string value
            ? value
            : string.Empty;

    private static List<string> ToStrings(IEnumerable<Uri> uris)
        => [.. uris.Select(uri => uri.ToString())];

    /// <summary>从扩展属性里读取创建时间；缺失或无法解析时为 <see cref="DateTimeOffset.MinValue"/>。</summary>
    /// <remarks>
    /// 公开是因为分页路径的筛选/排序也要用同一份解析规则，而那里构造的是内部中间类型、
    /// 不走本映射。解析规则只应有一处。
    /// </remarks>
    public static DateTimeOffset ReadCreationTime(IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue(CreationTimePropertyName, out var value))
        {
            return DateTimeOffset.MinValue;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var dateTime))
        {
            return dateTime;
        }

        return value.TryGetDateTimeOffset(out dateTime) ? dateTime : DateTimeOffset.MinValue;
    }
}
#endif
