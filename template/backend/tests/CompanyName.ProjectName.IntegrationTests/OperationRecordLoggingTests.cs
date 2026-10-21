#if (!IncludeOperationRecords)
using System.Net;
using System.Security.Claims;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.AmbientContext;
using Leistd.Ddd.Domain.Repositories;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>真实 PostgreSQL 事务与真实 Serilog 输出，验证关闭历史后的持久化边界。</summary>
public sealed class OperationRecordLoggingTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public async Task History_storage_reader_and_endpoints_are_absent_while_entity_audit_remains()
    {
        using var client = factory.CreateProjectClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        var tables = await db.Database.SqlQuery<string>(
            $"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = {DatabaseSchema.Name}").ToListAsync();
        Assert.DoesNotContain("OperationRecords", tables);
        Assert.DoesNotContain("OperationRecordArchives", tables);
        Assert.Null(scope.ServiceProvider.GetService<IOperationRecordReader>());
        Assert.NotNull(scope.ServiceProvider.GetService<IOperationRecorder>());
        using var history = await client.GetAsync("/api/v1/operation-records");
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        var user = db.Model.FindEntityType(typeof(User))!;
        foreach (var field in new[] { "CreationTime", "CreatorId", "LastModificationTime", "LastModifierId", "IsDeleted", "DeletionTime", "DeleterId", "TenantId" })
            Assert.NotNull(user.FindProperty(field));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Security_log_is_emitted_only_after_the_real_business_transaction_commits(bool commit)
    {
#if (LocalIdentity)
        using var session = await factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        Guid subject;
        using (var scope = factory.Services.CreateScope())
            subject = (await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Users.SingleAsync(u => u.Username == "admin")).Id;
#else
        var subject = Guid.NewGuid();
        using var session = factory.CreateResourceSession(subject, null);
        using var projected = await session.Client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, projected.StatusCode);
#endif
        var correlation = Guid.NewGuid().ToString("N");
        var capture = factory.Services.GetRequiredService<OperationRecordLogCapture>();
        string? before;
        var changed = $"committed-log-{correlation}";
        await using (var scope = factory.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<IAmbientContext>().Begin(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject.ToString()), new Claim("preferred_username", "snapshot-actor")], "test")),
            correlationId: correlation))
        using (var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true))
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            var user = await repository.GetOneAsync(u => u.Id == subject);
            Assert.NotNull(user);
            before = user.DisplayName;
#if (LocalIdentity)
            user.UpdateProfile(user.Username, user.Email, changed, user.PhoneNumber);
#else
            user.ProjectFromIssuer(user.Username, user.Email, changed);
#endif
            await repository.UpdateAsync(user);
            await scope.ServiceProvider.GetRequiredService<IOperationRecorder>().RecordSucceededAsync(
                OperationRecordActions.UserUpdated, OperationTarget.For(subject, changed), PermissionConstant.Users.Update);
            await uow.SaveChangesAsync();
            Assert.Empty(Records());
            if (commit) await uow.CompleteAsync();
            else await uow.RollbackAsync();
        }
        using var verification = factory.Services.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<MyProjectDbContext>().Users.SingleAsync(u => u.Id == subject);
        Assert.Equal(commit ? changed : before, persisted.DisplayName);
        if (!commit) { Assert.Empty(Records()); return; }
        var record = Assert.Single(Records());
        Assert.Equal(subject.ToString(), OperationRecordLogCapture.Field(record, "OperationActorId"));
        Assert.Equal("snapshot-actor", OperationRecordLogCapture.Field(record, "OperationActorName"));
        Assert.Equal(subject.ToString(), OperationRecordLogCapture.Field(record, "OperationTargetId"));
        Assert.Equal(changed, OperationRecordLogCapture.Field(record, "OperationTargetName"));
        Assert.Equal(PermissionConstant.Users.Update, OperationRecordLogCapture.Field(record, "OperationAuthorizationBasis"));
        Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(OperationRecordLogCapture.Field(record, "OperationRecordId")));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(OperationRecordLogCapture.Field(record, "OperationTime")).Kind);
        foreach (var field in new[] { "OperationActorTenantId", "OperationTenantId", "OperationImpersonatorId", "OperationImpersonatorName", "OperationOutcome", "OperationFailureCode", "OperationFailureDataKeys", "OperationFailureDetail", "OperationVisibility" })
            Assert.True(record.Properties.ContainsKey(field), field);
        LogEvent[] Records() => capture.Snapshot().Where(e =>
            Equals(OperationRecordLogCapture.Field(e, "OperationCorrelationId"), correlation)).ToArray();
    }
}
#endif
