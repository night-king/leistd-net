using System.Net;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Dtos;
using Leistd.MultiTenancy.ServiceClient.Options;
using Leistd.ServiceClient.Http;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ServiceClient.Stores;

// 回源控制面的机器端点，与端点共用 Core 里的线上 DTO。
// 只有带 Tenant:NotFound 的 404 翻成 null；其它 404（如前缀配错）与其余错误原样上抛，
// 不把“路由不对”或“控制面不可达”表现成“租户不存在”。
internal sealed class RemoteTenantConnectionStore(
    HttpClient httpClient,
    IOptions<RemoteTenantConnectionClientOptions> options)
    : ITenantConnectionConfigurationStore, ITenantDatabaseDirectory
{
    public async Task<TenantConnectionLookupResult?> FindAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"{Prefix}/runtime/{tenantId}?name={Uri.EscapeDataString(name)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var error = await response.CreateRemoteErrorAsync(cancellationToken);
            if (error.ErrorCode == MultiTenancyErrorCodes.NotFound)
            {
                return null;
            }

            throw error;
        }

        // 空响应体由 ReadContentAsync 抛无效响应（对外 502），不会落到这里
        var remote = (await response.ReadContentAsync<TenantRuntimeConnectionOutputDto>(cancellationToken: cancellationToken))!;

        return new TenantConnectionLookupResult
        {
            TenantId = remote.TenantId,
            HasAnyConnection = remote.HasAnyConnection,
            Connection = remote.Connection is { } connection
                ? new TenantConnectionConfiguration
                {
                    TenantId = remote.TenantId,
                    Name = connection.Name,
                    ConnectionString = connection.ConnectionString,
                    Version = connection.Version
                }
                : null
        };
    }

    public async Task<TenantMigrationConnectionListResult> GetListAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"{Prefix}/migration?name={Uri.EscapeDataString(name)}", cancellationToken);
        // 空响应体由 ReadContentAsync 抛出，不会被当成“没有目标”
        var remote = (await response.ReadContentAsync<TenantMigrationConnectionListOutputDto>(cancellationToken: cancellationToken))!;
        return new TenantMigrationConnectionListResult(
            [.. remote.Connections.Select(x => new TenantMigrationConnection(x.TenantId, x.Name, x.ConnectionString))],
            [.. remote.FailedTenants.Select(x => new TenantDatabaseFailure(x.TenantId, x.Reason))]);
    }

    public async Task<TenantDatabaseListResult> GetDatabasesAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        // 只需读路由权限：响应不含连接串
        using var response = await httpClient.GetAsync(
            $"{Prefix}/databases?name={Uri.EscapeDataString(name)}&activeOnly={(activeOnly ? "true" : "false")}",
            cancellationToken);
        // 空响应体同样上抛，不当成“没有库”
        var remote = (await response.ReadContentAsync<TenantDatabaseListOutputDto>(cancellationToken: cancellationToken))!;
        return new TenantDatabaseListResult(
            [.. remote.Databases.Select(x => new TenantDatabaseEntry(x.Fingerprint, x.TenantIds))],
            [.. remote.FailedTenants.Select(x => new TenantDatabaseFailure(x.TenantId, x.Reason))]);
    }

    private string Prefix => "/" + options.Value.RoutePrefix.Trim('/');
}
