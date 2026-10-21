#if (OpenIddictServer)
namespace CompanyName.ProjectName.Application.Auth.Options;

/// <summary>一个 API 资源的申请范围和交换发起方归属。</summary>
public sealed class OAuthApiResource
{
    public string Name { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public string? OwnerClientId { get; set; }
    public string ScopeName => Scope ?? Name;
    public string Owner => OwnerClientId ?? Name;
}
#endif
