using System.Security.Claims;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.AspNetCore;
using Leistd.OperationRecords.AspNetCore.Attributes;
using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security.Users;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Leistd.Tracing.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.AspNetCore;

public sealed class OperationFailureRecordingMiddlewareTests
{
    [Theory]
    [InlineData(true, true, false, 1)]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, false, 0)]
    [InlineData(true, true, true, 1)]
    public async Task Business_failures_preserve_the_exception_tenant_and_existing_record(
        bool authenticated, bool annotated, bool alreadyRecorded, int expectedRecords)
    {
        var store = new RecordingOperationRecordStore();
        var tenantId = Guid.NewGuid();
        using var provider = Services(store, tenantId).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([], authenticated ? "test" : null))
        };
        if (annotated)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new OperationRecordActionAttribute("orders.updated")), "orders"));
        if (alreadyRecorded)
            await scope.ServiceProvider.GetRequiredService<IOperationRecorder>().RecordFailedAsync(
                "orders.updated", OperationTarget.None, "allowed", OperationFailure.FromCode("Orders:Conflict"));
        var failure = new BusinessException("Orders:Conflict", "Business rejection").WithData("Name", "order");
        failure.Data["Secret"] = "private";
        var app = new ApplicationBuilder(scope.ServiceProvider);
        app.UseOperationFailureRecording();
        app.Run(_ => Task.FromException(failure));

        Assert.Same(failure, await Assert.ThrowsAsync<BusinessException>(() => app.Build()(context)));
        Assert.Equal(expectedRecords, store.Written.Count);
        if (expectedRecords == 1)
        {
            var record = Assert.Single(store.Written);
            Assert.Equal(tenantId, record.TenantId);
            Assert.Equal("Orders:Conflict", record.FailureCode);
            if (!alreadyRecorded)
                Assert.Equal("""{"Name":"order"}""", record.FailureData);
            Assert.Null(record.FailureDetail);
        }
    }

    [Fact]
    public async Task Technical_failures_are_rethrown_without_resolving_recording_services()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var failure = new InvalidOperationException("Technical failure");
        var app = new ApplicationBuilder(provider);
        app.UseOperationFailureRecording();
        app.Run(_ => Task.FromException(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(new DefaultHttpContext())));
    }

    private static IServiceCollection Services(IOperationRecordWriter writer, Guid tenantId)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(writer);
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant(tenantId));
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser());
        services.AddSingleton<ICorrelationIdProvider>(new FakeCorrelationIdProvider(null));
        services.AddSingleton<IClock>(new UtcClockProvider(new FakeTimeProvider()));
        services.AddSingleton<IOperationActionDefinitionManager>(new FakeOperationActionDefinitionManager());
        return services.AddOperationRecords();
    }
}
