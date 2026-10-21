using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Resource.Abstractions;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.Tests.EndToEnd;

public sealed record OrderDto(string ResourceKey, string Code, string OrganizationId, string OwnerId);

public sealed record PagedOrders(long TotalCount, IReadOnlyList<OrderDto> Items);

/// <summary>订单应用服务：按设计文档 §6.5 的标准执行链落地。</summary>
/// <remarks>
/// 功能权限由端点上的 <c>[Authorize(Policy = ...)]</c> 前置把关，本服务负责其余两层：
/// 集合先经数据范围翻译成 SQL 谓词，单实例先加载再做资源判定。
/// </remarks>
public sealed class OrderAppService(
    PipelineDbContext dbContext,
    IDataScopeApplier dataScope,
    IResourceAuthorizationService resourceAuthorization,
    IPermissionSubjectProvider subjectProvider,
    IResourceGrantStore resourceGrantStore)
{
    /// <summary>列表：先施加可见范围，再叠加分页。总数与当前页共用同一个范围入口。</summary>
    public async Task<PagedOrders> GetPagedListAsync(int offset, int limit, CancellationToken ct)
    {
        var scoped = await VisibleQueryAsync(DataOperations.Read, ct);

        var totalCount = await scoped.LongCountAsync(ct);
        var items = await scoped
            .OrderBy(order => order.Code)
            .Skip(offset)
            .Take(limit)
            .Select(order => new OrderDto(order.ResourceKey, order.Code, order.OrganizationId, order.OwnerId))
            .ToListAsync(ct);

        return new PagedOrders(totalCount, items);
    }

    /// <summary>导出：必须与列表共用同一个范围入口，否则导出会多出用户看不到的数据。</summary>
    public async Task<IReadOnlyList<OrderDto>> ExportAsync(CancellationToken ct)
    {
        var scoped = await VisibleQueryAsync(DataOperations.Export, ct);
        return await scoped
            .OrderBy(order => order.Code)
            .Select(order => new OrderDto(order.ResourceKey, order.Code, order.OrganizationId, order.OwnerId))
            .ToListAsync(ct);
    }

    /// <summary>详情：在可见范围内定位，范围外当作不存在。</summary>
    /// <remarks>
    /// <para>只经 <see cref="VisibleQueryAsync"/> 判可见性，与列表返回同样的 DTO。
    /// 不再叠加实例 Read 判定：Handler 或 ACL Granted 的口径不同于集合的（数据范围 OR ACL 允许）AND NOT ACL 拒绝。</para>
    /// <para>实例判定用于写路径的资源状态规则，例如已归档不可改。</para>
    /// </remarks>
    public async Task<OrderDto?> GetAsync(string resourceKey, CancellationToken ct)
    {
        var scoped = await VisibleQueryAsync(DataOperations.Read, ct);
        var order = await scoped.SingleOrDefaultAsync(x => x.ResourceKey == resourceKey, ct);

        return order == null
            ? null
            : new OrderDto(order.ResourceKey, order.Code, order.OrganizationId, order.OwnerId);
    }

    /// <summary>更新：范围用 Update 而非 Read——能看不等于能改。</summary>
    public async Task<bool> UpdateAsync(string resourceKey, string code, CancellationToken ct)
    {
        var scoped = await VisibleQueryAsync(DataOperations.Update, ct);
        var order = await scoped.AsTracking().SingleOrDefaultAsync(x => x.ResourceKey == resourceKey, ct);
        if (order == null)
        {
            return false;
        }

        if (!await resourceAuthorization.IsGrantedAsync(order, ResourceOperations.Update))
        {
            return false;
        }

        order.Code = code;
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>批量：先在范围内定位目标，再核对数量，禁止静默跳过越权项。</summary>
    public async Task<bool> ArchiveManyAsync(IReadOnlyCollection<string> resourceKeys, CancellationToken ct)
    {
        var scoped = await VisibleQueryAsync(DataOperations.Update, ct);
        var targets = await scoped.AsTracking().Where(x => resourceKeys.Contains(x.ResourceKey)).ToListAsync(ct);

        if (targets.Count != resourceKeys.Count)
        {
            return false;
        }

        // 集合入口只答"哪些看得见/改得动"，答不了领域规则（已归档不可再改）。
        // 逐项跑实例授权，任一拒绝整批拒绝——静默跳过越权项会让调用方以为全做完了。
        foreach (var order in targets)
        {
            if (!await resourceAuthorization.IsGrantedAsync(order, ResourceOperations.Update))
            {
                return false;
            }
        }

        foreach (var order in targets)
        {
            order.IsArchived = true;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>分享给我的：ACL 作为集合入口合并进查询，而不是逐行判定。</summary>
    public async Task<IReadOnlyList<OrderDto>> GetSharedWithMeAsync(CancellationToken ct)
    {
        var subject = await subjectProvider.GetCurrentSubjectAsync(ct);
        if (subject == null)
        {
            return [];
        }

        var grantedKeys = await resourceGrantStore.QueryGrantedResourceKeysAsync(
            Order.Resource,
            ResourceOperations.Read,
            subject.UserId,
            subject.RoleIds,
            ct);

        return await dbContext.Orders
            .Where(order => grantedKeys.Contains(order.ResourceKey))
            .OrderBy(order => order.Code)
            .Select(order => new OrderDto(order.ResourceKey, order.Code, order.OrganizationId, order.OwnerId))
            .ToListAsync(ct);
    }

    /// <summary>可见集合 = （数据范围 OR ACL 允许）AND NOT ACL 拒绝。</summary>
    /// <remarks>
    /// 只用数据范围会漏掉"别人分享给我"的资源；只用 ACL 允许集合又减不掉数据范围放行、
    /// 却被 ACL 显式拒绝的那一份——而"分享给部门、排除这一个人"正是显式拒绝的唯一用途。
    /// 列表、总数、导出必须共用这一个入口，否则总数与实际可见数据对不上，
    /// 或者列表里出现详情接口坚称不存在的资源。
    /// </remarks>
    private async Task<IQueryable<Order>> VisibleQueryAsync(string operation, CancellationToken ct)
    {
        var subject = await subjectProvider.GetCurrentSubjectAsync(ct);
        if (subject == null)
            return dbContext.Orders.Where(_ => false);

        // 超管旁路授权层，集合与单实例必须同一口径：实例判定已经让超管跳过 ACL，
        // 集合这边再减一次拒绝集合，就会出现"列表里看不见、按 ID 却打得开"。
        // 数据范围那一层本身也已为超管旁路（DefaultDataScopeApplier），这里补齐 ACL 侧。
        // 旁路的只是授权层——租户、软删除这类硬边界由查询自身承担，不在此处。
        if (subject.IsSuperAdmin)
            return dbContext.Orders;

        var scoped = await dataScope.ApplyAsync(dbContext.Orders.AsQueryable(), Order.Resource, operation, ct);

        var sharedKeys = await resourceGrantStore.QueryGrantedResourceKeysAsync(
            Order.Resource, operation, subject.UserId, subject.RoleIds, ct);
        var deniedKeys = await resourceGrantStore.QueryDeniedResourceKeysAsync(
            Order.Resource, operation, subject.UserId, subject.RoleIds, ct);

        var scopedKeys = scoped.Select(order => order.ResourceKey);

        // 注意：ACL 的两个集合入口内部带 AsNoTracking，而 EF 的跟踪行为由整棵查询树共享——
        // 把它们嵌进来之后整个查询变成不跟踪，写入路径必须显式 AsTracking()，
        // 否则改完实体 SaveChanges 什么也不会写，接口却照样返回成功。
        return dbContext.Orders
            .Where(order => (scopedKeys.Contains(order.ResourceKey) || sharedKeys.Contains(order.ResourceKey))
                            && !deniedKeys.Contains(order.ResourceKey));
    }
}

public static class OrderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // 功能权限：权限名即策略名，由 PermissionPolicyProvider 动态构建。
        app.MapGet("/orders", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.GetPagedListAsync(0, 50, ct)))
            .RequireAuthorization(OrderPermissions.Read);

        app.MapGet("/orders/export", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.ExportAsync(ct)))
            .RequireAuthorization(OrderPermissions.Export);

        // 同一份数据的另一个入口，用"任一满足"策略把关。
        app.MapGet("/orders/report", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.ExportAsync(ct)))
            .RequireAuthorization(OrderPermissions.ExportOrUpdate);

        // 宿主显式注册了同名但更严格的策略，动态权限策略不得覆盖它。
        app.MapGet("/orders/approve", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.ExportAsync(ct)))
            .RequireAuthorization(OrderPermissions.Approve);

        // 故意写错的策略名：含空段，任何主体都不该通过。
        app.MapGet("/orders/empty-segment", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.ExportAsync(ct)))
            .RequireAuthorization(OrderPermissions.Read + "|");

        app.MapGet("/orders/shared", async (OrderAppService service, CancellationToken ct) =>
                Results.Ok(await service.GetSharedWithMeAsync(ct)))
            .RequireAuthorization(OrderPermissions.Read);

        app.MapGet("/orders/{key}", async (string key, OrderAppService service, CancellationToken ct) =>
            {
                var order = await service.GetAsync(key, ct);
                return order is null ? Results.NotFound() : Results.Ok(order);
            })
            .RequireAuthorization(OrderPermissions.Read);

        app.MapPut("/orders/{key}", async (
                string key,
                UpdateOrderRequest request,
                OrderAppService service,
                CancellationToken ct) =>
            {
                var updated = await service.UpdateAsync(key, request.Code, ct);
                return updated ? Results.NoContent() : Results.Forbid();
            })
            .RequireAuthorization(OrderPermissions.Update);

        app.MapPost("/orders/archive", async (
                ArchiveOrdersRequest request,
                OrderAppService service,
                CancellationToken ct) =>
            {
                var archived = await service.ArchiveManyAsync(request.ResourceKeys, ct);
                return archived ? Results.NoContent() : Results.Forbid();
            })
            .RequireAuthorization(OrderPermissions.Update);
    }
}

public sealed record UpdateOrderRequest(string Code);

public sealed record ArchiveOrdersRequest(IReadOnlyCollection<string> ResourceKeys);
