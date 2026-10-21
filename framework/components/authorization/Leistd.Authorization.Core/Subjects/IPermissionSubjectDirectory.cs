namespace Leistd.Authorization.Subjects;

/// <summary>按授予对象查找主体：确认它存在，并给出显示名。由宿主按自己的用户、角色模型实现。</summary>
/// <remarks>管理用例据此拒绝不存在的主体，并为审计事件提供显示名。</remarks>
/// <example>
/// <code>
/// internal sealed class RoleSubjectDirectory(IRepository&lt;Role, Guid&gt; roles) : IPermissionSubjectDirectory
/// {
///     public async Task&lt;PermissionSubjectInfo?&gt; FindAsync(string providerName, string providerKey, CancellationToken ct = default)
///         =&gt; providerName == PermissionGrantProviderNames.Role &amp;&amp; Guid.TryParse(providerKey, out var id)
///            &amp;&amp; await roles.GetByIdAsync(id, ct) is { } role
///             ? new PermissionSubjectInfo(role.DisplayName ?? role.Name)
///             : null;
/// }
/// </code>
/// </example>
public interface IPermissionSubjectDirectory
{
    /// <summary>查找主体；Key 格式不对或主体不存在时返回 <see langword="null"/>。</summary>
    /// <param name="providerName">授予对象类型，见 <see cref="Constants.PermissionGrantProviderNames"/>。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PermissionSubjectInfo?> FindAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default);
}

/// <summary>授予主体的展示信息。</summary>
/// <param name="DisplayName">显示名；没有时为 <see langword="null"/>。</param>
public sealed record PermissionSubjectInfo(string? DisplayName);
