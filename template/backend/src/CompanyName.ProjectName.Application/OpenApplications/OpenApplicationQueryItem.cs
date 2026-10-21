#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.OpenApplications;

internal sealed class OpenApplicationQueryItem
{
    // 管理器元数据不参与动态排序。
    internal required object Application { get; init; }
    public required string ClientId { get; init; }
    public string? DisplayName { get; init; }
    public string? ApplicationType { get; init; }
    public string? ClientType { get; init; }
    public DateTimeOffset CreationTime { get; init; }
}
#endif
