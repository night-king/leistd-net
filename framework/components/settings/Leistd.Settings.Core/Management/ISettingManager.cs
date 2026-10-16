using Leistd.Settings.Definitions;
using Leistd.Settings.Stores;

namespace Leistd.Settings.Management;

/// <summary>设置值的写入入口，写前对照定义校验。</summary>
/// <remarks>业务写入经本接口而不是直接调用 <see cref="ISettingStore"/>：未定义的名称与定义不允许的层级在写入时拒绝。</remarks>
public interface ISettingManager
{
    /// <summary>写入设置值；<paramref name="value"/> 为 <see langword="null"/> 时清除该层级的值。</summary>
    /// <remarks>清除同样经过业务校验器（以清除后会生效的值），可能因此被拒绝。</remarks>
    /// <param name="name">设置名称。</param>
    /// <param name="value">设置值；<see langword="null"/> 表示清除，使其回落到下一层。</param>
    /// <param name="scope">写入的层级，必须在定义允许的范围内。</param>
    /// <param name="userId">用户级时的用户标识；租户级传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
