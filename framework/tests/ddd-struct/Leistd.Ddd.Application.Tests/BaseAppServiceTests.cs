using System.Reflection;
using Leistd.Ddd.Application.AppServices;
using Leistd.Ddd.Application.Contracts.AppServices;
using Leistd.Ddd.Application.Extensions;
using Leistd.ObjectMapping.Abstractions;
using Xunit;
using Leistd.Data.Paging;

namespace Leistd.Ddd.Application.Tests;

/// <summary>应用服务基类与分页映射扩展。</summary>
public class BaseAppServiceTests
{
    private sealed class OrderAppService : BaseAppService;

    [Fact]
    public void Deriving_from_the_base_satisfies_the_app_service_marker()
    {
        Assert.IsAssignableFrom<IAppService>(new OrderAppService());
    }

    /// <remarks>
    /// 基类刻意不注入任何东西——注释里写死了"不可以加 IServiceProvider 或任何延迟服务定位器"。
    /// 这条约束只写在文档里就会在某次"顺手加个 LazyServiceProvider"时失效，
    /// 而失效的症状（真实依赖从构造签名里消失）编译期完全看不出来。
    /// </remarks>
    [Fact]
    public void Base_class_exposes_no_service_locator()
    {
        var members = typeof(BaseAppService)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m is not ConstructorInfo)
            .ToArray();

        Assert.Empty(members);

        var ctor = Assert.Single(typeof(BaseAppService).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Empty(ctor.GetParameters());
    }

    private sealed record OrderEntity(Guid Id, string Code);
    private sealed record OrderDto(Guid Id, string Code);

    private sealed class PassThroughMapper : IObjectMapper
    {
        public int Calls { get; private set; }

        public TDestination Map<TSource, TDestination>(TSource source)
        {
            Calls++;
            var order = (OrderEntity)(object)source!;
            return (TDestination)(object)new OrderDto(order.Id, order.Code);
        }

        public TDestination Map<TSource, TDestination>(TSource source, IDictionary<string, object> contextItems)
            => Map<TSource, TDestination>(source);

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
            => Map<TSource, TDestination>(source);
    }

    // 分页映射只换条目类型，总数必须原样带过去——把 TotalCount 写成当前页条数
    // 会让前端的分页器永远只显示一页，而且每一页都"看起来正常"。
    [Fact]
    public void Mapping_a_paged_result_preserves_the_total_count()
    {
        var source = new PagedResult<OrderEntity>(
            999,
            [new OrderEntity(Guid.CreateVersion7(), "A-1"), new OrderEntity(Guid.CreateVersion7(), "A-2")]);

        var mapped = new PassThroughMapper().MapPagedResult<OrderEntity, OrderDto>(source);

        Assert.Equal(999, mapped.TotalCount);
        Assert.Equal(["A-1", "A-2"], mapped.Items.Select(i => i.Code));
    }

    [Fact]
    public void Mapping_an_empty_page_calls_the_mapper_zero_times()
    {
        var mapper = new PassThroughMapper();

        var mapped = mapper.MapPagedResult<OrderEntity, OrderDto>(new PagedResult<OrderEntity>(0, []));

        Assert.Equal(0, mapper.Calls);
        Assert.Empty(mapped.Items);
        Assert.Equal(0, mapped.TotalCount);
    }

    [Fact]
    public void Mapping_rejects_null_arguments()
    {
        var mapper = new PassThroughMapper();

        Assert.Throws<ArgumentNullException>(() =>
            mapper.MapPagedResult<OrderEntity, OrderDto>(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ((IObjectMapper)null!).MapPagedResult<OrderEntity, OrderDto>(new PagedResult<OrderEntity>(0, [])));
    }
}
