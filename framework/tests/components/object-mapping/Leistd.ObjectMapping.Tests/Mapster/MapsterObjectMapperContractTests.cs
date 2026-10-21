using Leistd.ObjectMapping.Abstractions;
// 本文件所在命名空间以 .Mapster 结尾，裸写 Mapster 会先解析到它自己；
// 被测组件的注册入口必须完整限定引入。
using Leistd.ObjectMapping.Mapster;
using Leistd.ObjectMapping.Tests.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.ObjectMapping.Tests.Mapster;

/// <summary>Mapster 实现对 <see cref="IObjectMapper"/> 契约的兑现。</summary>
/// <remarks>经真实注册面构造，而不是直接 new：注册面本身也是被测契约的一部分。</remarks>
public sealed class MapsterObjectMapperContractTests : ObjectMapperContractTests, IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddLogging()
        .AddMapsterObjectMapper()
        .BuildServiceProvider();

    /// <inheritdoc />
    protected override IObjectMapper CreateMapper() => _provider.GetRequiredService<IObjectMapper>();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Mapper_is_a_singleton()
    {
        Assert.Same(CreateMapper(), CreateMapper());
    }
}
