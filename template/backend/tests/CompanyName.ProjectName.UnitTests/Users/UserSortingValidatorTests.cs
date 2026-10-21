#if (LocalIdentity)
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.Exceptions;
using CompanyName.ProjectName.Application.Users.Validators;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.UnitTests.Users;

public sealed class UserSortingValidatorTests
{
    private readonly UserSortingValidator validator = new();
    private static IQueryable<User> Users => Array.Empty<User>().AsQueryable();

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("Username, SecurityStamp desc")]
    [InlineData("TwoFactor.Secret")]
    [InlineData("twofactor.recoverycodes")]
    [InlineData("it.PasswordHash.Substring(0,1)")]
    [InlineData("Username.Substring(SecurityStamp.Length)")]
    [InlineData("PasswordHash == \"prefix\"")]
    [InlineData("Username, TwoFactor.LastUsedStep")]
    public void Credential_access_is_rejected_even_in_nested_selectors(string sorting)
    {
        var ordered = Users.OrderBy(sorting);

        var exception = Assert.Throws<BusinessException>(() => validator.Validate(ordered));

        Assert.Equal(UserErrorCodes.SortingCredentialsForbidden, exception.Code);
    }

    [Theory]
    [InlineData("iif(PasswordHash > \"$2b\", 0, 1)")]
    [InlineData("PasswordHash.ToString()")]
    public void Default_parser_restrictions_also_reject_credential_expressions(string sorting)
    {
        Assert.Throws<ParseException>(() => Users.OrderBy(sorting));
    }

    [Fact]
    public void Conditional_selector_is_fully_visited()
    {
        var ordered = Users.OrderBy(user => user.PasswordHash == "prefix" ? 0 : 1);

        Assert.Throws<BusinessException>(() => validator.Validate(ordered));
    }

    [Theory]
    [InlineData("DisplayName, IsSuperAdmin desc")]
    [InlineData("lastlogin.time DESC, USERNAME ascending")]
    public void Ordinary_properties_do_not_require_an_allowlist(string sorting)
    {
        validator.Validate(Users.OrderBy(sorting));
    }

    [Fact]
    public void Existing_filters_are_not_checked_as_sorting_selectors()
    {
        var ordered = Users.Where(user => user.PasswordHash != null).OrderBy("Username");

        validator.Validate(ordered);
    }
}
#endif
