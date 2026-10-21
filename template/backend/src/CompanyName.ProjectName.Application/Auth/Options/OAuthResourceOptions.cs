#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.Auth.Options;

/// <summary>本应用的 API 资源与 scope 目录，配置节 OAuth。</summary>
public sealed class OAuthResourceOptions
{
    public const string SectionName = "OAuth";
    /// <summary>本服务 API 的资源标识（访问令牌的受众）。同名登记为 scope：客户端申请它，签发的令牌才能调用本服务的 API。</summary>
    public string Resource { get; set; } = "companyname-projectname-api";

    /// <summary>下游 API 的资源、scope 与拥有该资源的客户端。未指定 scope 或所有者时取资源名。</summary>
    /// <remarks>下游服务把自己的 <c>Authentication:Audience</c> 设为同一个值。不能为空、不能重复，也不能与内置 scope 同名。</remarks>
    public OAuthApiResource[] ApiResources { get; set; } = [];
}

#endif
