#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.OpenApplications.Dtos;

/// <summary>可授予开放应用的一个 scope。</summary>
public sealed record OpenApplicationScopeOutputDto
{
    /// <summary>scope 名；授予时的权限值为 <c>scp:</c> 加上它。</summary>
    public required string Name { get; init; }

    /// <summary>展示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>可授予交换应用的 API 受众；标准与机器 scope 为 null。</summary>
    public string? Audience { get; init; }

    /// <summary>只能授予 client_credentials 的机器客户端。</summary>
    public bool MachineOnly { get; init; }
}
#endif
