using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence.Repositories;

/// <summary><see cref="IUserRepository"/> 的 EF Core 实现。</summary>
public class EfCoreUserRepository(
    IDbContextProvider<MyProjectDbContext> dbContextProvider,
    IUnitOfWorkManager uow)
    : EfCoreRepository<MyProjectDbContext, User, Guid>(dbContextProvider, uow), IUserRepository
{
    /// <inheritdoc />
    /// <remarks>
    /// 成员关系与角色一次连接查询取回，两边各自带租户与软删除的全局过滤器。
    /// </remarks>
    public async Task<List<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextAsync(cancellationToken);
        var query =
            from userRole in dbContext.Set<UserRole>()
            join role in dbContext.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            orderby role.Name
            select role.Name;

        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<User?> GetWithRolesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var query = await GetQueryableWithRolesAsync(cancellationToken);
        return await query.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>成员关系与用户各自带租户与软删除的全局过滤器。</remarks>
    public async Task<IQueryable<User>> GetQueryableWithRolesAsync(CancellationToken cancellationToken = default)
    {
        var query = await GetQueryableAsync(cancellationToken);
        return query.Include(user => user.Roles);
    }
}
