using Leistd.ObjectMapping.Abstractions;
using Leistd.ObjectMapping.Extensions;
using Xunit;

namespace Leistd.ObjectMapping.Tests.Contracts;

/// <summary>任何 <see cref="IObjectMapper"/> 实现都必须兑现的行为。</summary>
/// <remarks>
/// <para><b>新增实现时必须派生本类</b>，否则换 mapper 就是一次没有安全网的替换：
/// 三个 <c>Map</c> 重载的语义（新建实例 / 写入既有实例 / 带上下文）只写在接口注释里，
/// 编译期一个字都保证不了。</para>
/// <para>只断言接口承诺的部分，不涉及任何实现的配置方式——配置差异属于实现自己的用例。</para>
/// </remarks>
public abstract class ObjectMapperContractTests
{
    /// <summary>构造被测实现，并让它认识本文件里的两组类型。</summary>
    protected abstract IObjectMapper CreateMapper();

    protected sealed record Source(string Name, int Amount);

    protected sealed class Destination
    {
        public string Name { get; set; } = "";
        public int Amount { get; set; }
        public string Untouched { get; set; } = "keep";
    }

    [Fact]
    public void Map_to_new_instance_copies_matching_members()
    {
        var result = CreateMapper().Map<Source, Destination>(new Source("order-1", 42));

        Assert.Equal("order-1", result.Name);
        Assert.Equal(42, result.Amount);
    }

    // 写入既有实例的重载必须返回同一个对象，而不是复制一份再返回——
    // 调用方普遍写成 `mapper.Map(input, entity)` 后继续用 entity，返回新对象会让写入静默丢失。
    [Fact]
    public void Map_into_existing_instance_mutates_and_returns_that_instance()
    {
        var destination = new Destination { Name = "old", Amount = 1 };

        var result = CreateMapper().Map(new Source("new", 99), destination);

        Assert.Same(destination, result);
        Assert.Equal("new", destination.Name);
        Assert.Equal(99, destination.Amount);
    }

    // 目标上没有对应来源的成员不能被清空：部分更新场景全靠这条。
    [Fact]
    public void Map_into_existing_instance_leaves_unmapped_members_alone()
    {
        var destination = new Destination { Untouched = "sentinel" };

        CreateMapper().Map(new Source("x", 1), destination);

        Assert.Equal("sentinel", destination.Untouched);
    }

    [Fact]
    public void Map_with_context_items_still_produces_a_mapped_instance()
    {
        var result = CreateMapper().Map<Source, Destination>(
            new Source("ctx", 7),
            new Dictionary<string, object> { ["tenant"] = "t1" });

        Assert.Equal("ctx", result.Name);
        Assert.Equal(7, result.Amount);
    }

    // 上下文重载不得把上下文残留到后续调用：Mapster 的 MapContext 是 AsyncLocal 的，
    // 忘记建 scope 时上一次的参数会渗到下一次。
    [Fact]
    public void Context_items_do_not_leak_into_the_next_mapping()
    {
        var mapper = CreateMapper();

        mapper.Map<Source, Destination>(new Source("a", 1), new Dictionary<string, object> { ["k"] = "v" });
        var second = mapper.Map<Source, Destination>(new Source("b", 2));

        Assert.Equal("b", second.Name);
    }

    [Fact]
    public void MapList_maps_every_element_in_order()
    {
        var mapper = CreateMapper();

        var result = mapper.MapList<Source, Destination>(
            [new Source("a", 1), new Source("b", 2), new Source("c", 3)]);

        Assert.Equal(["a", "b", "c"], result.Select(d => d.Name));
        Assert.Equal([1, 2, 3], result.Select(d => d.Amount));
    }

    [Fact]
    public void MapList_of_empty_sequence_is_empty_rather_than_null()
    {
        Assert.Empty(CreateMapper().MapList<Source, Destination>([]));
    }

    [Fact]
    public void MapList_rejects_null_arguments()
    {
        var mapper = CreateMapper();

        Assert.Throws<ArgumentNullException>(() => mapper.MapList<Source, Destination>(null!));
        Assert.Throws<ArgumentNullException>(() => ((IObjectMapper)null!).MapList<Source, Destination>([]));
    }
}
