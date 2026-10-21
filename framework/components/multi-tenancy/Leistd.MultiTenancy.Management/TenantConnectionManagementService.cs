using System.ComponentModel.DataAnnotations;
using Leistd.EventBus.Abstractions;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Dtos;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Stores;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Events;

namespace Leistd.MultiTenancy.Management;

internal sealed class TenantConnectionManagementService(
    ITenantConnectionDirectory directory,
    ITenantConnectionConfigurationStore store,
    ITenantConnectionConfigurationManager manager,
    ITenantStore tenants,
    ITenantDatabaseDirectory databaseDirectory,
    ILocalEventBus? eventBus = null) : ITenantConnectionManagementService
{
    public async Task<TenantDatabaseListOutputDto> GetDatabaseListAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        // 名字来自 URL，按输入归一化（非法为 400），不用目录的代码级归一化（非法为 500）
        var normalized = TenantConnectionNames.NormalizeInput(name);
        var listed = await databaseDirectory.GetDatabasesAsync(normalized, activeOnly, cancellationToken);

        return new TenantDatabaseListOutputDto
        {
            Databases = [.. listed.Databases.Select(entry => new TenantDatabaseOutputDto
            {
                Fingerprint = entry.Fingerprint,
                TenantIds = entry.TenantIds
            })],
            FailedTenants = [.. listed.FailedTenants.Select(failure => new TenantDatabaseFailureOutputDto
            {
                TenantId = failure.TenantId,
                Reason = failure.Reason
            })]
        };
    }

    public async Task<IReadOnlyList<TenantConnectionOutputDto>> GetListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entries = await directory.ListAsync(tenantId, cancellationToken)
            ?? throw new TenantNotFoundException(tenantId.ToString());

        return [.. entries.Select(entry => new TenantConnectionOutputDto { TenantId = tenantId, Name = entry.Name, Version = entry.Version })];
    }

    public async Task<TenantRuntimeConnectionOutputDto> GetRuntimeAsync(
        Guid tenantId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var lookup = await store.FindAsync(tenantId, TenantConnectionNames.NormalizeInput(name), cancellationToken)
            ?? throw new TenantNotFoundException(tenantId.ToString());

        return new TenantRuntimeConnectionOutputDto
        {
            TenantId = lookup.TenantId,
            HasAnyConnection = lookup.HasAnyConnection,
            Connection = lookup.Connection is { } connection
                ? new TenantConnectionDetailOutputDto
                {
                    Name = connection.Name,
                    ConnectionString = connection.ConnectionString,
                    Version = connection.Version
                }
                : null
        };
    }

    public async Task<TenantMigrationConnectionListOutputDto> GetMigrationListAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var list = await store.GetListAsync(TenantConnectionNames.NormalizeInput(name), cancellationToken);
        return new TenantMigrationConnectionListOutputDto
        {
            Connections =
            [
                .. list.Connections.Select(connection => new TenantMigrationConnectionOutputDto
                {
                    TenantId = connection.TenantId,
                    Name = connection.Name,
                    ConnectionString = connection.ConnectionString
                })
            ],
            FailedTenants =
            [
                .. list.FailedTenants.Select(failure => new TenantDatabaseFailureOutputDto
                {
                    TenantId = failure.TenantId,
                    Reason = failure.Reason
                })
            ]
        };
    }

    public async Task<TenantConnectionOutputDto> SetAsync(
        Guid tenantId,
        string name,
        UpsertTenantConnectionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        var configuration = await manager.SetAsync(tenantId, name, input.ConnectionString, input.ExpectedVersion, cancellationToken);

        // 组件只发事件（不含连接串），留痕方式由宿主决定
        await PublishAsync(
            configuration.TenantId,
            configuration.Name,
            input.ExpectedVersion is null ? TenantConnectionChangeKind.Registered : TenantConnectionChangeKind.Changed,
            configuration.Version,
            cancellationToken);

        return new TenantConnectionOutputDto
        {
            TenantId = configuration.TenantId,
            Name = configuration.Name,
            Version = configuration.Version
        };
    }

    public async Task RemoveAsync(Guid tenantId, string name, long expectedVersion, CancellationToken cancellationToken = default)
    {
        // 先归一化：删除没有管理器返回的权威名字，原样发事件会让 "CRM" 与 "crm" 成为两个目标标识
        var normalized = TenantConnectionNames.NormalizeInput(name);
        await manager.RemoveAsync(tenantId, normalized, expectedVersion, cancellationToken);
        await PublishAsync(tenantId, normalized, TenantConnectionChangeKind.Removed, expectedVersion, cancellationToken);
    }

    private async Task PublishAsync(
        Guid tenantId,
        string name,
        TenantConnectionChangeKind change,
        long version,
        CancellationToken cancellationToken)
    {
        if (eventBus is null)
        {
            return;
        }

        // 发事件时取显示名快照；写入已落定，查不到只能是并发删除，此时留 null 不让留痕失败
        var tenant = await tenants.FindAsync(tenantId, cancellationToken);

        await eventBus.PublishAsync(
            new TenantConnectionChangedEvent(tenantId, tenant?.DisplayName ?? tenant?.Name, name, change, version),
            cancellationToken);
    }
}
