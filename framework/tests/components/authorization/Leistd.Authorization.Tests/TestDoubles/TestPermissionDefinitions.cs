using Leistd.MultiTenancy.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Leistd.Authorization.Definitions;

namespace Leistd.Authorization.Tests.TestDoubles;

/// <summary>测试用权限定义：一个启用组和一个被禁用的父权限，覆盖父子、禁用与未定义三类边界。</summary>
/// <remarks>
/// <code>
/// App（组）
///   App.Orders
///     App.Orders.Read
///     App.Orders.Write
///       App.Orders.Write.Batch
///     App.Orders.Delete
///   App.Reports          [IsEnabled = false]
///     App.Reports.View
/// </code>
/// </remarks>
public sealed class TestPermissionDefinitionProvider : IPermissionDefinitionProvider
{
    public const string Orders = "App.Orders";
    public const string OrdersRead = "App.Orders.Read";
    public const string OrdersWrite = "App.Orders.Write";
    public const string OrdersWriteBatch = "App.Orders.Write.Batch";
    public const string OrdersDelete = "App.Orders.Delete";
    public const string Reports = "App.Reports";
    public const string ReportsView = "App.Reports.View";
    public const string Undefined = "App.Orders.NotDefined";

    public void Define(IPermissionDefinitionContext context)
    {
        var group = context.GetOrAddGroup("App", "应用权限");

        var orders = group.AddPermission(Orders, MultiTenancySides.Both, "订单管理");
        orders.AddChild(OrdersRead, "查看订单");
        var write = orders.AddChild(OrdersWrite, "编辑订单");
        write.AddChild(OrdersWriteBatch, "批量编辑订单");
        orders.AddChild(OrdersDelete, "删除订单");

        var reports = group.AddPermission(Reports, MultiTenancySides.Both, "报表");
        reports.IsEnabled = false;
        reports.AddChild(ReportsView, "查看报表");
    }
}

internal static class TestPermissionDefinitions
{
    public static PermissionDefinitionManager CreateManager(params IPermissionDefinitionProvider[] providers)
    {
        var providerList = providers.Length == 0
            ? [new TestPermissionDefinitionProvider()]
            : providers;

        return new PermissionDefinitionManager(
            providerList,
            NullLogger<PermissionDefinitionManager>.Instance);
    }
}
