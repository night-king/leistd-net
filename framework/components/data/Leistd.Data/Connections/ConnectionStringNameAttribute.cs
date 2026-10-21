namespace Leistd.Data.Connections;

/// <summary>指定 DbContext 解析连接字符串时使用的连接名称。</summary>
/// <remarks>
/// 未声明的 DbContext 使用 <see cref="ConnectionStringNames.Default"/>。
/// 典型用法是把控制面 DbContext 钉在一个独立的命名连接上，
/// 而业务 DbContext 继续走会随租户变化的默认连接。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ConnectionStringNameAttribute : Attribute
{
    /// <summary>使用指定连接名称创建特性。</summary>
    public ConnectionStringNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>获取传递给 <see cref="IConnectionStringResolver"/> 的连接名称。</summary>
    public string Name { get; }
}
