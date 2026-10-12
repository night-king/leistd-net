using System.ComponentModel.DataAnnotations;
using Leistd.Data.Paging;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Events;
using Leistd.MultiTenancy.Management.Provisioning;

namespace Leistd.MultiTenancy.Management;

internal sealed class TenantManagementService(
    ITenantManager tenantManager,
    ITenantConnectionConfigurationManager connectionManager,
    ICurrentTenant currentTenant,
    IUnitOfWorkManager unitOfWorkManager,
    IServiceScopeFactory scopeFactory,
    ITenantDatabaseErrorDescriber databaseErrorDescriber,
    IEnumerable<ITenantActivationGuard> activationGuards,
    ILogger<TenantManagementService> logger,
    ITenantProvisioner? provisioner = null,
    ILocalEventBus? eventBus = null) : ITenantManagementService
{
    public async Task<PagedResult<TenantOutputDto>> GetPagedAsync(
        GetTenantPagedInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        var page = await tenantManager.GetPagedAsync(input.Keyword, input, cancellationToken);
        return new PagedResult<TenantOutputDto>(page.TotalCount, page.Items.Select(ToOutput));
    }

    public async Task<TenantOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => ToOutput(await tenantManager.FindAsync(id, cancellationToken) ?? throw new TenantNotFoundException(id.ToString()));

    public async Task<TenantOutputDto> CreateAsync(
        CreateTenantInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        // 整批校验排在任何库操作之前，避免先建租户再靠补偿撤销
        var connections = NormalizeConnections(input.Connections);

        // 登记版本仅用于补偿时删除；返回值必须接住，删除是带版本的乐观并发接口
        List<(string Name, long Version)> registered = [];
        TenantConfiguration tenant;
        using (var controlUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            tenant = await tenantManager.CreateAsync(
                input.Name, input.DisplayName, isActive: false, input.Description, cancellationToken);

            // 与登记租户同一个工作单元，开通钩子首次执行时即看到完整的连接集合
            foreach (var (name, connectionString) in connections)
            {
                var connection = await connectionManager.SetAsync(
                    tenant.Id,
                    name,
                    connectionString,
                    expectedVersion: null,
                    cancellationToken);
                registered.Add((name, connection.Version));
            }

            await controlUnitOfWork.CompleteAsync(cancellationToken);
        }

        var context = new TenantProvisioningContext(tenant, input);
        TenantConfiguration activated;
        try
        {
            if (provisioner is not null)
            {
                using (currentTenant.Change(tenant.Id, tenant.Name))
                using (var tenantUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
                {
                    await provisioner.ProvisionAsync(context, cancellationToken);
                    await tenantUnitOfWork.CompleteAsync(cancellationToken);
                }
            }

            using var activationUnitOfWork = unitOfWorkManager.Begin(requiresNew: true);
            activated = await tenantManager.SetActiveAsync(tenant.Id, true, cancellationToken);
            await activationUnitOfWork.CompleteAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Tenant {TenantId} initialization failed; rolling back the tenant and its provisioned data", tenant.Id);
            await CompensateAsync(context, registered);

            if (databaseErrorDescriber.Describe(exception) is { } described)
            {
                throw described;
            }

            throw;
        }

        logger.LogInformation("Tenant {TenantName} ({TenantId}) created and initialized", tenant.Name, tenant.Id);
        await PublishAsync(activated, TenantChangeKind.Created, cancellationToken);
        return ToOutput(activated);
    }

    public async Task<TenantOutputDto> UpdateAsync(Guid id, UpdateTenantInputDto input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        var tenant = await tenantManager.UpdateAsync(id, input.Name, input.DisplayName, input.Description, cancellationToken);
        await PublishAsync(tenant, TenantChangeKind.Updated, cancellationToken);
        return ToOutput(tenant);
    }

    public async Task<TenantOutputDto> SetActivationAsync(
        Guid id,
        UpdateTenantActivationInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

        // 上一行已拒绝缺失；不能取默认值，否则漏传的请求会被当成停用
        var isActive = input.IsActive!.Value;

        if (isActive)
        {
            // 先判存在性，否则不存在的租户会被启用前置条件拒绝，而不是 404
            var existing = await tenantManager.FindAsync(id, cancellationToken) ?? throw new TenantNotFoundException(id.ToString());
            foreach (var guard in activationGuards)
            {
                await guard.EnsureCanActivateAsync(existing, cancellationToken);
            }
        }

        var tenant = await tenantManager.SetActiveAsync(id, isActive, cancellationToken);
        await PublishAsync(tenant, TenantChangeKind.ActivationChanged, cancellationToken);
        return ToOutput(tenant);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // 删除幂等：不存在视为成功，不发事件。快照同时供事件取删除前的名字
        var doomed = await tenantManager.FindAsync(id, cancellationToken);
        if (doomed is null)
        {
            return;
        }

        try
        {
            await tenantManager.DeleteAsync(id, cancellationToken);
        }
        catch (TenantNotFoundException missing) when (Guid.TryParse(missing.TenantIdOrName, out var missingId) && missingId == id)
        {
            // 快照之后被并发删除：结果与先到一步相同，删除不是本次做的，不发事件
            return;
        }

        logger.LogInformation("Tenant {TenantId} deleted (soft delete; business data retained)", id);
        await PublishAsync(doomed, TenantChangeKind.Deleted, cancellationToken);
    }

    // 整批归一化名字、校验连接串语法；重名在写库前拒绝，否则第二条会覆盖第一条
    private static List<(string Name, string ConnectionString)> NormalizeConnections(
        IReadOnlyList<CreateTenantConnectionInputDto> connections)
    {
        List<(string Name, string ConnectionString)> normalized = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connection in connections)
        {
            // DataAnnotations 不递归进集合，null 元素在此拒绝
            if (connection is null)
            {
                throw new ValidationException(
                    new ValidationResult("Connection entries must not be null.", [nameof(connections)]),
                    validatingAttribute: null,
                    value: connections);
            }

            // 父对象的验证不进集合元素，逐条执行元素自己的注解（必填、长度）
            Validator.ValidateObject(connection, new ValidationContext(connection), validateAllProperties: true);

            var name = TenantConnectionNames.NormalizeInput(connection.Name);
            if (!seen.Add(name))
            {
                throw new BusinessException(MultiTenancyErrorCodes.ConnectionNameDuplicated, $"The connection name '{name}' was given more than once.")
                    .WithData("Name", name);
            }

            normalized.Add((name, TenantConnectionStrings.NormalizeInput(connection.ConnectionString)));
        }

        return normalized;
    }

    // 回滚失败的创建：清开通数据 → 删连接登记 → 删租户（已删租户的连接行无法再删，故连接在前）。
    // 每步用新作用域、独立捕获并记录，前一步失败仍继续；用独立令牌，不随调用方取消而中止。
    private async Task CompensateAsync(TenantProvisioningContext context, IReadOnlyList<(string Name, long Version)> registered)
    {
        var tenant = context.Tenant;
        if (provisioner is not null)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;
                using (services.GetRequiredService<ICurrentTenant>().Change(tenant.Id, tenant.Name))
                using (var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true))
                {
                    await services.GetRequiredService<ITenantProvisioner>().PurgeAsync(context, CancellationToken.None);
                    await unitOfWork.CompleteAsync(CancellationToken.None);
                }
            }
            catch (Exception purgeError)
            {
                logger.LogError(purgeError, "Failed to purge provisioned data for tenant {TenantId}; residual data requires manual inspection", tenant.Id);
            }
        }

        // 逐条独立捕获，一条失败不放弃后续步骤
        foreach (var (name, version) in registered)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var services = scope.ServiceProvider;
                using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
                await services.GetRequiredService<ITenantConnectionConfigurationManager>()
                    .RemoveAsync(tenant.Id, name, version, CancellationToken.None);
                await unitOfWork.CompleteAsync(CancellationToken.None);
            }
            catch (Exception removeError)
            {
                logger.LogError(
                    removeError,
                    "Failed to remove the '{ConnectionName}' connection registration for tenant {TenantId}; an orphan row now points at a deleted tenant",
                    name,
                    tenant.Id);
            }
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            await services.GetRequiredService<ITenantManager>().DeleteAsync(tenant.Id, CancellationToken.None);
            await unitOfWork.CompleteAsync(CancellationToken.None);
        }
        catch (Exception deleteError)
        {
            logger.LogError(deleteError, "Failed to delete tenant {TenantId}; its name cannot be reused until manually cleaned up", tenant.Id);
        }
    }

    private async Task PublishAsync(TenantConfiguration tenant, TenantChangeKind change, CancellationToken cancellationToken)
    {
        if (eventBus is not null)
        {
            await eventBus.PublishAsync(new TenantChangedEvent(tenant.Id, tenant.DisplayName ?? tenant.Name, change), cancellationToken);
        }
    }


    private static TenantOutputDto ToOutput(TenantConfiguration tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        DisplayName = tenant.DisplayName,
        Description = tenant.Description,
        IsActive = tenant.IsActive,
        CreationTime = tenant.CreationTime
    };
}
