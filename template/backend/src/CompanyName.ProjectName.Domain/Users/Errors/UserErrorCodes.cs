namespace CompanyName.ProjectName.Domain.Users.Errors;

/// <summary>User 业务错误码。</summary>
public static class UserErrorCodes
{
    public const string AvatarInvalid = "User:AvatarInvalid";
    public const string AvatarTooLarge = "User:AvatarTooLarge";
    public const string EmailTaken = "User:EmailTaken";
#if (RemoteTokenAuth)
    /// <summary>本服务停用了该成员：资源服务形态下由授权阶段拒绝，答 403。</summary>
    public const string LocalAccessDisabled = "User:LocalAccessDisabled";
    /// <summary>本服务没有该主体的成员行（投影未建成）：资源服务形态下由授权阶段拒绝，答 403。</summary>
    public const string LocalMemberMissing = "User:LocalMemberMissing";
#endif
    public const string ManageRolesRequired = "User:ManageRolesRequired";
    public const string NotFound = "User:NotFound";
    public const string RolesNotFound = "User:RolesNotFound";
#if (LocalIdentity)
    /// <summary>不能删除自己：默认 400，不登记映射。</summary>
    public const string SelfDeleteForbidden = "User:SelfDeleteForbidden";
#endif
    /// <summary>不能停用自己：默认 400，不登记映射。</summary>
    public const string SelfDisableForbidden = "User:SelfDisableForbidden";
#if (LocalIdentity)
    public const string SortingCredentialsForbidden = "User:SortingCredentialsForbidden";
    public const string SuperAdminDeleteForbidden = "User:SuperAdminDeleteForbidden";
#endif
    public const string SuperAdminDisableForbidden = "User:SuperAdminDisableForbidden";
    public const string SuperAdminDisableSelfForbidden = "User:SuperAdminDisableSelfForbidden";
    public const string SuperAdminOperationForbidden = "User:SuperAdminOperationForbidden";
#if (LocalIdentity)
    public const string SuperAdminResetPasswordForbidden = "User:SuperAdminResetPasswordForbidden";
#endif
    public const string SuperAdminUpdateForbidden = "User:SuperAdminUpdateForbidden";
    public const string UsernameTaken = "User:UsernameTaken";
}
