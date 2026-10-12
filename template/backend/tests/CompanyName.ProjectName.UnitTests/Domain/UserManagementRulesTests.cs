#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.UnitTests.Domain;

public sealed class UserManagementRulesTests
{
    [Fact]
    public void Regular_user_can_be_managed_disabled_and_deleted()
    {
        var user = CreateUser();

        Assert.True(user.CanBeManagedBy(Guid.NewGuid()));
        Assert.True(user.CanBeDisabled());
        Assert.True(user.CanBeDeleted());
    }

    /// <summary>头像规则在实体里守：任何入口（本人上传、管理员编辑、外部登录同步）写进来的值都经同一道校验。</summary>
    [Fact]
    public void An_invalid_avatar_is_rejected_and_leaves_the_current_one()
    {
        var user = CreateUser();
        user.SetAvatar("https://example.com/avatar.png");

        var error = Assert.Throws<BusinessException>(() => user.SetAvatar("javascript:alert(1)"));

        Assert.Equal(UserErrorCodes.AvatarInvalid, error.Code);
        Assert.Equal("https://example.com/avatar.png", user.Avatar);
    }

    [Fact]
    public void Built_in_super_admin_can_only_be_managed_by_itself()
    {
        var user = CreateUser();
        user.MarkAsSuperAdmin();

        Assert.True(user.CanBeManagedBy(user.Id));
        Assert.False(user.CanBeManagedBy(Guid.NewGuid()));
        Assert.False(user.CanBeManagedBy(null));
    }

    [Fact]
    public void Built_in_super_admin_cannot_be_disabled_or_deleted()
    {
        var user = CreateUser();
        user.MarkAsSuperAdmin();

        Assert.False(user.CanBeDisabled());
        Assert.False(user.CanBeDeleted());
    }

    /// <summary>任何人都不能停用自己：停用当场作废本人会话，操作者把自己关在门外。别人照常可以停用他。</summary>
    [Fact]
    public void A_user_cannot_be_disabled_by_themselves_but_can_by_another_administrator()
    {
        var user = CreateUser();

        Assert.False(user.CanBeDisabledBy(user.Id));
        Assert.True(user.CanBeDisabledBy(Guid.NewGuid()));
        // 没有操作者（系统作业）不算本人
        Assert.True(user.CanBeDisabledBy(null));
    }

    [Fact]
    public void The_built_in_super_admin_cannot_be_disabled_by_anyone()
    {
        var user = CreateUser();
        user.MarkAsSuperAdmin();

        Assert.False(user.CanBeDisabledBy(user.Id));
        Assert.False(user.CanBeDisabledBy(Guid.NewGuid()));
    }
#if (LocalIdentity)

    [Fact]
    public void A_user_cannot_be_deleted_by_themselves_but_can_by_another_administrator()
    {
        var user = CreateUser();

        Assert.False(user.CanBeDeletedBy(user.Id));
        Assert.True(user.CanBeDeletedBy(Guid.NewGuid()));
        Assert.True(user.CanBeDeletedBy(null));

        user.MarkAsSuperAdmin();
        Assert.False(user.CanBeDeletedBy(Guid.NewGuid()));
    }
#endif

#if (LocalIdentity)
    [Fact]
    public void Enabling_two_factor_twice_is_rejected_and_keeps_the_secret_in_use()
    {
        var user = CreateUser();
        user.EnableTwoFactor("secret-in-use", ["code-hash"], usedStep: 1);
        var stamp = user.SecurityStamp;

        var error = Assert.Throws<BusinessException>(() => user.EnableTwoFactor("other-secret", ["other-hash"], usedStep: 2));

        Assert.Equal(AuthErrorCodes.TwoFactorAlreadyEnabled, error.Code);
        Assert.Equal("secret-in-use", user.TwoFactor!.Secret);
        Assert.Equal(1, user.RecoveryCodesLeft);
        Assert.Equal(1, user.TwoFactor.LastUsedStep);
        Assert.Equal(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Replacing_recovery_codes_without_two_factor_is_rejected_and_writes_nothing()
    {
        var user = CreateUser();

        var error = Assert.Throws<BusinessException>(() => user.ReplaceRecoveryCodes(["code-hash"]));

        Assert.Equal(AuthErrorCodes.TwoFactorNotEnabled, error.Code);
        Assert.Null(user.TwoFactor);
        Assert.Equal(0, user.RecoveryCodesLeft);
    }

    [Fact]
    public void Recovery_codes_are_replaced_while_two_factor_is_enabled()
    {
        var user = CreateUser();
        user.EnableTwoFactor("secret", ["old-a", "old-b"], usedStep: 1);

        user.ReplaceRecoveryCodes(["new-a"]);

        Assert.Equal(1, user.RecoveryCodesLeft);
        Assert.False(user.TryConsumeRecoveryCode("old-a"));
        Assert.True(user.TryConsumeRecoveryCode("new-a"));
    }
#if (Email)

    [Fact]
    public void A_verified_email_rejects_another_verification_code()
    {
        var user = CreateUser();
        user.EnsureEmailUnconfirmed();

        user.ConfirmEmail();
        var error = Assert.Throws<BusinessException>(() => user.EnsureEmailUnconfirmed());

        Assert.Equal(AuthErrorCodes.EmailAlreadyVerified, error.Code);
    }
#endif

#endif
    [Fact]
    public void Assigning_roles_skips_roles_already_held_and_duplicates()
    {
        var user = CreateUser();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        user.AssignRoles([first, first]);
        user.AssignRoles([first, second]);

        Assert.Equal([first, second], user.GetRoleIds());
        Assert.Equal(2, user.Roles.Count);
        Assert.All(user.Roles, membership => Assert.Equal(user.Id, membership.UserId));
    }

    [Fact]
    public void Replacing_roles_revokes_dropped_memberships_keeps_retained_rows_and_adds_missing_ones()
    {
        var user = CreateUser();
        var (dropped, kept, added) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        user.AssignRoles([dropped, kept]);
        var keptRow = user.Roles.Single(membership => membership.RoleId == kept);

        user.ReplaceRoles([kept, added]);

        Assert.Equal([kept, added], user.GetRoleIds());
        Assert.False(user.IsInRole(dropped));
        // 撤销是软删除：行留在集合里作历史，保存时由审计拦截器补删除时间
        Assert.True(user.Roles.Single(membership => membership.RoleId == dropped).IsDeleted);
        Assert.Same(keptRow, user.Roles.Single(membership => membership.RoleId == kept));
        Assert.False(keptRow.IsDeleted);
    }

    [Fact]
    public void A_revoked_role_can_be_assigned_again_as_a_new_membership()
    {
        var user = CreateUser();
        var roleId = Guid.NewGuid();
        user.AssignRoles([roleId]);

        user.RemoveRole(roleId);
        Assert.False(user.IsInRole(roleId));
        user.AssignRoles([roleId]);

        Assert.True(user.IsInRole(roleId));
        Assert.Equal(2, user.Roles.Count(membership => membership.RoleId == roleId));
        Assert.Single(user.Roles, membership => membership.RoleId == roleId && membership.IsDeleted);
    }

    [Fact]
    public void Removing_all_roles_revokes_every_membership()
    {
        var user = CreateUser();
        user.AssignRoles([Guid.NewGuid(), Guid.NewGuid()]);

        user.RemoveAllRoles();

        Assert.Empty(user.GetRoleIds());
        Assert.All(user.Roles, membership => Assert.True(membership.IsDeleted));
    }

    private static User CreateUser() =>
#if (LocalIdentity)
        new("management-user", "management@example.com", "password-hash");
#else
        new(
            Guid.Parse("01991a40-8a00-7000-8000-000000000002"),
            "management-user",
            "management@example.com");
#endif
}
