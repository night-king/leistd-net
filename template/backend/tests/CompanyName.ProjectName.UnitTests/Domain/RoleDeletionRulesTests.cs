using CompanyName.ProjectName.Domain.Users.Entities;

namespace CompanyName.ProjectName.UnitTests.Domain;

public sealed class RoleDeletionRulesTests
{
    [Fact]
    public void Regular_role_can_be_deleted()
    {
        var role = new Role("member", "Member");

        Assert.True(role.CanBeDeleted());
    }

    [Fact]
    public void Built_in_role_cannot_be_deleted()
    {
        var role = new Role("admin", "Administrator", isStatic: true);

        Assert.False(role.CanBeDeleted());
    }
}
