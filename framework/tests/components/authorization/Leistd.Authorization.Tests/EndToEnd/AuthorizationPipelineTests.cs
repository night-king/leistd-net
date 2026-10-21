using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Resource.Grants;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Resource.Abstractions;

namespace Leistd.Authorization.Tests.EndToEnd;

/// <summary>三层授权串起来之后的行为：功能权限 → 数据范围 → 资源实例授权。</summary>
public class AuthorizationPipelineTests : IAsyncLifetime
{
    private PipelineHost _host = default!;

    private const string OwnOrder = "order-own";
    private const string ColleagueOrder = "order-colleague";
    private const string OutsideOrder = "order-outside";
    private const string ArchivedOrder = "order-archived";

    public async Task InitializeAsync()
    {
        _host = await PipelineHost.StartAsync();

        await _host.ExecuteAsync(async db =>
        {
            db.Orders.AddRange(
                new Order
                {
                    ResourceKey = OwnOrder, Code = "A-001",
                    OwnerId = PipelineFixtures.OwnerUserId, OrganizationId = PipelineFixtures.OrganizationId
                },
                new Order
                {
                    ResourceKey = ColleagueOrder, Code = "A-002",
                    OwnerId = PipelineFixtures.ColleagueUserId, OrganizationId = PipelineFixtures.OrganizationId
                },
                new Order
                {
                    ResourceKey = OutsideOrder, Code = "B-001",
                    OwnerId = PipelineFixtures.OutsiderUserId, OrganizationId = "org-2"
                },
                new Order
                {
                    ResourceKey = ArchivedOrder, Code = "A-003", IsArchived = true,
                    OwnerId = PipelineFixtures.OwnerUserId, OrganizationId = PipelineFixtures.OrganizationId
                });
            await db.SaveChangesAsync();
        });

        // 组织角色持有读取、更新与导出权限。
        await _host.GrantAsync(
            PermissionGrantProviderNames.Role,
            PipelineFixtures.OrgRoleId,
            OrderPermissions.Read,
            OrderPermissions.Update,
            OrderPermissions.Export);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Missing_functional_permission_is_rejected_before_any_data_is_touched()
    {
        // 没有任何授予的主体：第一层就应拦下，不该走到数据范围。
        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/orders", PipelineFixtures.OwnerUserId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    // 任一满足：持有其中任意一个权限都放行。
    [InlineData(OrderPermissions.Export)]
    [InlineData(OrderPermissions.Update)]
    public async Task Any_of_policy_accepts_each_listed_permission(string permission)
    {
        var userId = $"any-of-{permission}";
        await _host.GrantAsync(PermissionGrantProviderNames.User, userId, permission);

        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/orders/report", userId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Explicitly_registered_policy_wins_over_the_dynamic_permission_policy()
    {
        const string userId = "explicit-policy";
        await _host.GrantAsync(PermissionGrantProviderNames.User, userId, OrderPermissions.Approve);

        // 宿主给 Orders.Approve 注册了更严格的同名策略（权限之外还要求一个 Claim）。
        // 动态权限策略若把它盖掉，只有权限就能通过——那等于悄悄放宽了宿主的授权要求。
        var withoutClaim = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/orders/approve", userId));
        Assert.Equal(HttpStatusCode.Forbidden, withoutClaim.StatusCode);

        var request = _host.Request(HttpMethod.Get, "/orders/approve", userId);
        request.Headers.Add(TestAuthenticationHandler.ClaimsHeader, PipelineFixtures.ApprovalClaim);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Any_of_policy_rejects_a_name_with_an_empty_segment()
    {
        const string userId = "empty-segment";
        await _host.GrantAsync(PermissionGrantProviderNames.User, userId, OrderPermissions.Read);

        // "Orders.Read|" 拆出一个空段，空段不是已定义权限，因此整个策略名不按权限策略处理，
        // 回退后也找不到同名策略。此时应在请求期直接报"策略不存在"而不是因为
        // "其中一段命中"就放行——写错的策略名必须大声失败。
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _host.Client.SendAsync(_host.Request(HttpMethod.Get, "/orders/empty-segment", userId)));

        Assert.Contains(OrderPermissions.Read + "|", exception.Message);
    }

    [Fact]
    public async Task Any_of_policy_still_rejects_a_subject_holding_none_of_them()
    {
        const string userId = "any-of-none";
        // 持有同一权限族里的其他权限，但不在策略列出的集合内。
        await _host.GrantAsync(PermissionGrantProviderNames.User, userId, OrderPermissions.Read);

        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/orders/report", userId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_is_challenged_rather_than_forbidden()
    {
        var response = await _host.Client.GetAsync("/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Data_scope_limits_the_list_to_the_assigned_organization()
    {
        var page = await GetAsync<PagedOrders>("/orders", PipelineFixtures.OwnerUserId);

        // 组织内 3 条可见，org-2 的那条不可见——总数与条目都必须一致。
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        Assert.DoesNotContain(page.Items, order => order.ResourceKey == OutsideOrder);
    }

    [Fact]
    public async Task Export_shares_the_same_scope_entry_as_the_list()
    {
        var page = await GetAsync<PagedOrders>("/orders", PipelineFixtures.OwnerUserId);
        var exported = await GetAsync<List<OrderDto>>("/orders/export", PipelineFixtures.OwnerUserId);

        // 导出多出一条就意味着用户能拿到列表里看不到的数据。
        Assert.Equal(page.TotalCount, exported.Count);
        Assert.DoesNotContain(exported, order => order.ResourceKey == OutsideOrder);
    }

    [Fact]
    public async Task Detail_outside_the_scope_is_indistinguishable_from_missing()
    {
        var response = await _host.Client.SendAsync(_host.Request(
            HttpMethod.Get, $"/orders/{OutsideOrder}", PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId));

        // 水平越权防护：范围外返回 404 而不是 403，避免泄漏资源存在性。
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Detail_answers_the_same_question_as_the_list()
    {
        var page = await GetAsync<PagedOrders>("/orders", PipelineFixtures.OwnerUserId);
        Assert.Contains(page.Items, order => order.ResourceKey == ColleagueOrder);

        // 列表列得出来、详情就必须打得开：同一个 Read 操作只能有一个答案。
        // 两边 DTO 字段一样，详情再返回 404 挡不住任何存在性泄漏，只会自相矛盾。
        var colleague = await GetAsync<OrderDto>($"/orders/{ColleagueOrder}", PipelineFixtures.OwnerUserId);
        Assert.Equal(ColleagueOrder, colleague.ResourceKey);

        var own = await GetAsync<OrderDto>($"/orders/{OwnOrder}", PipelineFixtures.OwnerUserId);
        Assert.Equal("A-001", own.Code);
    }

    [Fact]
    public async Task Read_scope_does_not_imply_update_scope()
    {
        // 读取范围是整个组织，更新范围只有本人：同事的订单看得到、改不了。
        var response = await _host.Client.SendAsync(WithBody(
            _host.Request(HttpMethod.Put, $"/orders/{ColleagueOrder}",
                PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId),
            new UpdateOrderRequest("HACKED")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await _host.ExecuteAsync(async db =>
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(x => x.ResourceKey == ColleagueOrder);
            Assert.Equal("A-002", order.Code);
        });
    }

    [Fact]
    public async Task Own_order_can_be_updated_through_the_full_chain()
    {
        var response = await _host.Client.SendAsync(WithBody(
            _host.Request(HttpMethod.Put, $"/orders/{OwnOrder}",
                PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId),
            new UpdateOrderRequest("A-001-UPDATED")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await _host.ExecuteAsync(async db =>
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(x => x.ResourceKey == OwnOrder);
            Assert.Equal("A-001-UPDATED", order.Code);
        });
    }

    [Fact]
    public async Task Domain_rule_denial_beats_scope_and_ownership()
    {
        // 已归档订单：在更新范围内、也是本人所有，但领域规则拒绝——拒绝优先。
        var response = await _host.Client.SendAsync(WithBody(
            _host.Request(HttpMethod.Put, $"/orders/{ArchivedOrder}",
                PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId),
            new UpdateOrderRequest("SHOULD-NOT-APPLY")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Batch_operation_rejects_the_whole_request_instead_of_skipping_denied_items()
    {
        var response = await _host.Client.SendAsync(WithBody(
            _host.Request(HttpMethod.Post, "/orders/archive",
                PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId),
            new ArchiveOrdersRequest([OwnOrder, ColleagueOrder])));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // 静默跳过越权项会让调用方以为整批都成功了，因此本人的那条也不能被处理。
        await _host.ExecuteAsync(async db =>
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(x => x.ResourceKey == OwnOrder);
            Assert.False(order.IsArchived);
        });
    }

    [Fact]
    public async Task Acl_grant_is_merged_into_the_collection_query()
    {
        // 组织外的订单通过 ACL 分享给我：集合查询里应当出现，且不依赖数据范围。
        await _host.ReplaceResourceGrantsAsync(
            OutsideOrder,
            new ResourceGrant(
                ResourceOperations.Read,
                PermissionGrantProviderNames.User,
                PipelineFixtures.OwnerUserId,
                ResourceGrantEffect.Granted));

        var shared = await GetAsync<List<OrderDto>>("/orders/shared", PipelineFixtures.OwnerUserId);

        var order = Assert.Single(shared);
        Assert.Equal(OutsideOrder, order.ResourceKey);
    }

    [Fact]
    public async Task Acl_denial_removes_the_resource_from_the_collection_query()
    {
        await _host.ReplaceResourceGrantsAsync(
            OutsideOrder,
            new ResourceGrant(
                ResourceOperations.Read,
                PermissionGrantProviderNames.Role,
                PipelineFixtures.OrgRoleId,
                ResourceGrantEffect.Granted),
            new ResourceGrant(
                ResourceOperations.Read,
                PermissionGrantProviderNames.User,
                PipelineFixtures.OwnerUserId,
                ResourceGrantEffect.Prohibited));

        var shared = await GetAsync<List<OrderDto>>("/orders/shared", PipelineFixtures.OwnerUserId);

        Assert.Empty(shared);
    }

    [Fact]
    public async Task Acl_denial_also_removes_a_resource_that_data_scope_would_show()
    {
        // 数据范围放行（同组织）、ACL 显式拒绝这个人——"分享给部门、排除这一个人"就是这个形状。
        // 只用"ACL 允许减 ACL 拒绝"组合列表时减不掉它：它根本不在 ACL 允许集合里。
        await _host.ReplaceResourceGrantsAsync(
            ColleagueOrder,
            new ResourceGrant(
                ResourceOperations.Read,
                PermissionGrantProviderNames.User,
                PipelineFixtures.OwnerUserId,
                ResourceGrantEffect.Prohibited));

        var page = await GetAsync<PagedOrders>("/orders", PipelineFixtures.OwnerUserId);

        Assert.DoesNotContain(page.Items, x => x.ResourceKey == ColleagueOrder);

        // 列表与详情必须同口径：列表放出来、详情却坚称不存在，等于列表已经泄漏了存在性。
        var detail = await _host.Client.SendAsync(_host.Request(
            HttpMethod.Get, $"/orders/{ColleagueOrder}", PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId));
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task Super_admin_sees_the_same_set_in_the_list_and_by_id()
    {
        // 超管旁路授权层，集合与单实例必须给同一个答案。集合这边照减 ACL 拒绝集合的话，
        // 就会出现"列表里没有、按 ID 却打得开"——同一条 ACL 例外，两个入口两种结论。
        await _host.ReplaceResourceGrantsAsync(
            ColleagueOrder,
            new ResourceGrant(
                ResourceOperations.Read,
                PermissionGrantProviderNames.User,
                PipelineFixtures.OutsiderUserId,
                ResourceGrantEffect.Prohibited));

        var page = await SuperAdminGetAsync<PagedOrders>("/orders");
        Assert.Contains(page.Items, x => x.ResourceKey == ColleagueOrder);

        // 组织外的那条也在内：超管连数据范围一起旁路。
        Assert.Contains(page.Items, x => x.ResourceKey == OutsideOrder);

        var detail = await SuperAdminGetAsync<OrderDto>($"/orders/{ColleagueOrder}");
        Assert.Equal(ColleagueOrder, detail.ResourceKey);
    }

    [Fact]
    public async Task Super_admin_is_still_bound_by_domain_rules()
    {
        // 旁路的是授权层，不是领域不变量：已归档的订单谁都不能改，超管也不例外。
        var response = await _host.Client.SendAsync(WithBody(
            SuperAdminRequest(HttpMethod.Put, $"/orders/{ArchivedOrder}"),
            new { code = "Z-999" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Batch_is_rejected_as_a_whole_when_a_domain_rule_denies_one_item()
    {
        // 已归档订单在数据范围内、功能权限也齐备，只有领域规则拦得住它。
        // 集合入口答不了这种规则，批量必须逐项跑实例授权，且任一拒绝整批拒绝——
        // 静默跳过越权项会让调用方以为全做完了。
        var response = await _host.Client.SendAsync(WithBody(
            _host.Request(HttpMethod.Post, "/orders/archive",
                PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId),
            new ArchiveOrdersRequest([OwnOrder, ArchivedOrder])));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await _host.ExecuteAsync(async db =>
        {
            var own = await db.Orders.AsNoTracking().SingleAsync(x => x.ResourceKey == OwnOrder);
            Assert.False(own.IsArchived);
        });
    }

    [Fact]
    public async Task Revoking_the_functional_permission_takes_effect_on_the_next_request()
    {
        var before = await _host.Client.SendAsync(_host.Request(
            HttpMethod.Get, "/orders", PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>();
            await manager.ReplaceGrantsAsync(
                PermissionGrantProviderNames.Role,
                PipelineFixtures.OrgRoleId,
                []);
        }

        var after = await _host.Client.SendAsync(_host.Request(
            HttpMethod.Get, "/orders", PipelineFixtures.OwnerUserId, PipelineFixtures.OrgRoleId));
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    private async Task<T> GetAsync<T>(string url, string userId)
    {
        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, url, userId, PipelineFixtures.OrgRoleId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private HttpRequestMessage SuperAdminRequest(HttpMethod method, string url)
    {
        var request = _host.Request(method, url, PipelineFixtures.OutsiderUserId);
        request.Headers.Add(TestAuthenticationHandler.ClaimsHeader, PipelineFixtures.SuperAdminClaim);

        return request;
    }

    private async Task<T> SuperAdminGetAsync<T>(string url)
    {
        var response = await _host.Client.SendAsync(SuperAdminRequest(HttpMethod.Get, url));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static HttpRequestMessage WithBody<T>(HttpRequestMessage request, T body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }
}
