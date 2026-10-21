using Leistd.Authorization.Errors;
using Leistd.ExceptionHandling;

namespace Leistd.Authorization.Exceptions;

/// <summary>尝试授予未定义或已禁用的权限。</summary>
/// <remarks>
/// 由授予管理器在所有写入路径上抛出，调用方不必重复判断。错误码为 <see cref="PermissionErrorCodes.UndefinedPermission"/>。
/// </remarks>
public class UndefinedPermissionException : BusinessException
{
    /// <summary>以未定义或已禁用的权限名构造。</summary>
    /// <param name="permissionNames">未定义或已禁用的权限名。</param>
    public UndefinedPermissionException(IReadOnlyList<string> permissionNames)
        : base(PermissionErrorCodes.UndefinedPermission,
            $"The following permissions are undefined or disabled and cannot be granted: " +
            $"{string.Join(", ", permissionNames)}.")
    {
        PermissionNames = permissionNames;
        WithData("Names", string.Join(", ", permissionNames));
    }

    /// <summary>未定义或已禁用的权限名。</summary>
    public IReadOnlyList<string> PermissionNames { get; }
}
