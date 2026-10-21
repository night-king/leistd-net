using Mapster;

namespace Leistd.ObjectMapping.Mapster.Options;

/// <summary>配置 Mapster 对象映射。</summary>
public class MapsterOptions
{
    /// <summary>映射配置操作，按添加顺序作用于组件自己的 <see cref="TypeAdapterConfig"/>。</summary>
    public List<Action<TypeAdapterConfig>> Configurators { get; } = [];

    /// <summary>为 <see langword="true"/> 时在首次解析映射器时编译全部配置，配置错误立即抛出；默认 <see langword="false"/>。</summary>
    public bool ValidateMappings { get; set; }
}
