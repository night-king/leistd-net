namespace Leistd.Data.Connections;

/// <summary>按连接名称解析最终连接字符串。</summary>
/// <remarks>
/// 不依赖多租户：租户感知的实现由多租户组件提供；未注册任何实现时，DbContext 使用宿主配置的单连接。
/// </remarks>
public interface IConnectionStringResolver
{
    /// <summary>解析指定名称的最终连接字符串。</summary>
    /// <param name="connectionStringName">DbContext 上 <see cref="ConnectionStringNameAttribute"/> 声明的名称，未声明时为 <see cref="ConnectionStringNames.Default"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最终连接字符串。解析失败必须抛异常，不返回空值，也不回退到调用方未选择的数据库。</returns>
    Task<string> ResolveAsync(
        string connectionStringName,
        CancellationToken cancellationToken = default);
}
