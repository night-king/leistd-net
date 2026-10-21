using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence.Repositories;

/// <summary><see cref="IRoleRepository"/> 的 EF Core 实现。</summary>
public class EfCoreRoleRepository(
    IDbContextProvider<MyProjectDbContext> dbContextProvider,
    IUnitOfWorkManager uow)
    : EfCoreRepository<MyProjectDbContext, Role, Guid>(dbContextProvider, uow), IRoleRepository
{
    /// <inheritdoc />
    public async Task<List<Role>> GetDefaultRolesAsync(CancellationToken cancellationToken = default)
    {
        var roles = await GetDbSetAsync(cancellationToken);
        return await roles
            .Where(role => role.IsDefault)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken);
    }
}
