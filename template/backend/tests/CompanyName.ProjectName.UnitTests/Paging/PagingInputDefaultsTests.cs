using CompanyName.ProjectName.Application.Users.Dtos;
using CompanyName.ProjectName.Application.Roles.Dtos;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.OpenApplications.Dtos;
#endif

namespace CompanyName.ProjectName.UnitTests.Paging;

public sealed class PagingInputDefaultsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_sorting_uses_the_business_dto_default(string? sorting)
    {
        Assert.Equal("CreationTime desc", new GetUserPagedInputDto { Sorting = sorting }.Sorting);
        Assert.Equal("Sort asc", new GetRolePagedInputDto { Sorting = sorting }.Sorting);
#if (OpenIddictServer)
        Assert.Equal("CreationTime desc", new GetOpenApplicationPagedInputDto { Sorting = sorting }.Sorting);
#endif
    }

    [Fact]
    public void Omitted_sorting_and_explicit_multi_key_sorting_are_preserved()
    {
        Assert.Equal("CreationTime desc", new GetUserPagedInputDto().Sorting);
        const string expression = "Username desc, Email asc";
        Assert.Equal(expression, new GetUserPagedInputDto { Sorting = expression }.Sorting);
    }
}
