using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Leistd.Data.Paging;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Data.Tests;

/// <summary>分页请求与结果的对外契约：组件的存储、用例、端点与业务项目的列表接口都直接用它。</summary>
public class PagingContractTests
{
    private static IReadOnlyList<ValidationResult> Validate(PageRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Default_request_is_valid_and_returns_a_bounded_page()
    {
        var request = new PageRequest();

        Assert.Equal(0, request.Offset);
        Assert.Equal(10, request.Limit);
        Assert.Null(request.Sorting);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void Derived_defaults_are_visible_through_the_base_type_and_serialization()
    {
        PageRequest request = new ExpandedPageRequest();

        Assert.Equal(20, request.Limit);
        Assert.Equal("Name desc", request.Sorting);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.Equal(20, json.RootElement.GetProperty(nameof(PageRequest.Limit)).GetInt32());
        Assert.Equal("Name desc", json.RootElement.GetProperty(nameof(PageRequest.Sorting)).GetString());
    }

    [Fact]
    public void Derived_records_keep_equality_and_with_semantics()
    {
        PageRequest request = new ExpandedPageRequest();
        var copy = request with { Limit = 30 };

        Assert.IsType<ExpandedPageRequest>(copy);
        Assert.Equal(new ExpandedPageRequest { Limit = 30 }, copy);
        Assert.Equal(20, request.Limit);
        Assert.NotEqual(request, copy);
    }

    [Fact]
    public void Overrides_can_expand_or_narrow_validation_without_changing_base_requests()
    {
        Assert.Empty(Validate(new ExpandedPageRequest { Limit = 1500 }));
        Assert.NotEmpty(Validate(new ExpandedPageRequest { Limit = 2001 }));
        Assert.Empty(Validate(new NarrowPageRequest { Limit = 50 }));
        Assert.NotEmpty(Validate(new NarrowPageRequest { Limit = 51 }));
        Assert.NotEmpty(Validate(new PageRequest { Limit = 1500 }));
        Assert.NotEmpty(Validate(new InheritedPageRequest { Limit = 1500 }));
    }

    [Fact]
    public void Mvc_metadata_uses_the_overridden_validation_range()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(ExpandedPageRequest));

        var limit = metadata.Properties.Single(property => property.PropertyName == nameof(PageRequest.Limit));
        var range = Assert.Single(limit.ValidatorMetadata.OfType<RangeAttribute>());
        Assert.Equal(1, range.Minimum);
        Assert.Equal(2000, range.Maximum);
    }

    private sealed record ExpandedPageRequest : PageRequest
    {
        [Range(1, 2000)]
        public override int Limit { get; init; } = 20;
        public override string? Sorting { get; init; } = "Name desc";
    }

    private sealed record NarrowPageRequest : PageRequest
    {
        [Range(1, 50)]
        public override int Limit { get; init; } = 20;
    }

    private sealed record InheritedPageRequest : PageRequest;

    // 边界值必须放行：写成 Range(1, 999) 之类的差一错误只有边界用例能发现。
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, PageRequest.MaximumLimit)]
    [InlineData(int.MaxValue, 10)]
    public void Boundary_values_are_accepted(int offset, int limit)
    {
        Assert.Empty(Validate(new PageRequest { Offset = offset, Limit = limit }));
    }

    // 越界产生验证错误而不是被静默夹紧：夹紧会让调用方以为拿到了它请求的那一页。
    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, PageRequest.MaximumLimit + 1)]
    public void Out_of_range_values_produce_validation_errors(int offset, int limit)
    {
        Assert.NotEmpty(Validate(new PageRequest { Offset = offset, Limit = limit }));
    }

    // Sorting 不在此约束——可排序字段由各应用服务自行校验，这里不得抢先拒绝。
    [Theory]
    [InlineData("")]
    [InlineData("name desc")]
    [InlineData("'; drop table users --")]
    public void Sorting_is_not_validated_here(string sorting)
    {
        Assert.Empty(Validate(new PageRequest { Sorting = sorting }));
    }

    [Fact]
    public void Paged_result_keeps_total_count_independent_of_page_size()
    {
        var result = new PagedResult<string>(1000, ["a", "b"]);

        Assert.Equal(1000, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
    }

    // 空条目必须是空集合而不是 null：调用方普遍直接 foreach / .Count。
    [Fact]
    public void Null_items_become_an_empty_collection()
    {
        var result = new PagedResult<string>(0, (IEnumerable<string>)null!);

        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
    }

    // 从序列构造时必须当场物化：延迟求值的序列会在响应序列化阶段才执行，
    // 那时数据库上下文往往已经释放。
    [Fact]
    public void Sequence_constructor_materializes_immediately()
    {
        var evaluated = 0;
        IEnumerable<int> Lazy()
        {
            evaluated++;
            yield return 1;
        }

        var result = new PagedResult<int>(1, Lazy());

        Assert.Equal(1, evaluated);
        Assert.Equal([1], result.Items);
    }

    // record 的值相等语义：条目集合是引用比较，同内容不同实例不相等。
    // 写断言的人容易误以为 PagedResult 会逐项比较，这里把真实语义钉住。
    [Fact]
    public void Equality_compares_the_items_reference_not_the_contents()
    {
        IReadOnlyList<string> items = ["a"];

        Assert.Equal(new PagedResult<string>(1, items), new PagedResult<string>(1, items));
        Assert.NotEqual(new PagedResult<string>(1, ["a"]), new PagedResult<string>(1, ["a"]));
    }
}
