#if (IncludeNotifications)
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
using Leistd.Settings.Dtos;
using Leistd.Settings.Management;
using Leistd.Settings.Resolution;
using Leistd.Settings.Validation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>两项保留期被同时改动时，违序规则仍成立：第二次写入等第一次提交后才校验，读到新值并被拒绝。</summary>
/// <remarks>
/// <para>每次写入各用独立的作用域与数据库连接，走设置页用例（与 <c>PUT current-tenant</c> 同一入口）。
/// 第一次写入在通过校验后挂住；第二次写入发出后，由服务端信号决定何时放行第一次：锁装饰器在第二次写入确实要等锁时发信号，
/// 屏障校验器在第二次写入通过校验时发信号，先到者为准。写入端串行时先到的必然是"等锁"；撤掉串行时第二次写入会先用旧值通过校验，
/// 两次都成功并留下违序的一组。</para>
/// <para>夹具只在本测试的服务组合里：锁装饰器包住宿主实际注册的锁，屏障校验器排在保留期校验器之后。</para>
/// </remarks>
public sealed class NotificationRetentionConcurrencyTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string HostLockKey = "settings:host";

    // 只防卡死；正常路径的每一步都由信号推进
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(SettingConstant.NotificationRetention.ReadDays, "300", SettingConstant.NotificationRetention.UnreadDays, "200", null)]
    [InlineData(SettingConstant.NotificationRetention.UnreadDays, null, SettingConstant.NotificationRetention.ReadDays, "380", "400")]
    public async Task A_concurrent_retention_change_is_validated_against_the_committed_value(
        string firstName,
        string? firstValue,
        string secondName,
        string secondValue,
        string? presetUnread)
    {
        var probe = new InterleavingProbe();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            Wrap<IDistributedLock>(services, inner => new SignallingLock(inner, probe));
            services.AddTransient<ISettingValueValidator>(_ => new BarrierValidator(probe));
        }));

        // 先启动宿主：启动耗时不计入下面只防卡死的等待
        _ = host.Services;
        Task? first = null;
        Task? second = null;
        try
        {
            if (presetUnread is not null)
            {
                await WriteAsync(host, SettingConstant.NotificationRetention.UnreadDays, presetUnread);
            }

            var run = probe.Start(firstName, secondName);
            first = Task.Run(() => WriteAsync(host, firstName, firstValue));
            // 第一次写入没走到屏障就失败时直接暴露它的异常，不等到超时
            await Task.WhenAny(run.FirstValidated, first).WaitAsync(Guard);
            if (!run.FirstValidated.IsCompleted)
            {
                await first;
            }

            second = Task.Run(() => WriteAsync(host, secondName, secondValue));

            Task observed;
            try
            {
                observed = await Task.WhenAny(run.SecondWaiting, run.SecondValidated).WaitAsync(Guard);
            }
            finally
            {
                // 任何一步失败也先放行第一次写入，并有界地等两次写入结束，之后才清理设置；原始失败照常上抛
                run.ReleaseFirst();
                await DrainAsync(first, second);
            }

            var firstFailure = await OutcomeAsync(first);
            var secondFailure = await OutcomeAsync(second);
            probe.Stop();

            Assert.True(observed == run.SecondWaiting, "The second write passed validation before waiting for the settings lock");
            Assert.Null(firstFailure);
            Assert.Equal(
                AppSettingErrorCodes.NotificationRetentionOrder,
                Assert.IsType<BusinessException>(secondFailure).Code);

            var (read, unread) = await ReadRetentionAsync(host);
            Assert.True(unread >= read, $"Stored retention is out of order: read={read}, unread={unread}");
            if (firstValue is not null)
            {
                Assert.Equal(int.Parse(firstValue, System.Globalization.CultureInfo.InvariantCulture),
                    firstName == SettingConstant.NotificationRetention.ReadDays ? read : unread);
            }
        }
        finally
        {
            probe.Stop();
            await DrainAsync(first, second);
            // 先清已读（回落 90）再清未读（回落 365），两步都合规
            await WriteAsync(host, SettingConstant.NotificationRetention.ReadDays, null);
            await WriteAsync(host, SettingConstant.NotificationRetention.UnreadDays, null);
        }
    }

    private static async Task WriteAsync(WebApplicationFactory<Program> host, string name, string? value)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingManagementService>()
            .SetForCurrentTenantAsync(new SetSettingInputDto(name, value));
    }

    private static async Task<(int Read, int Unread)> ReadRetentionAsync(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingProvider>();
        return (await settings.GetAsync<int>(SettingConstant.NotificationRetention.ReadDays),
            await settings.GetAsync<int>(SettingConstant.NotificationRetention.UnreadDays));
    }

    // 只等结束、不判结果：结果另行断言，这里吞掉的异常不会掩盖原始失败
    private static async Task DrainAsync(params Task?[] writes)
    {
        try
        {
            await Task.WhenAll(writes.OfType<Task>()).WaitAsync(Guard);
        }
        catch (Exception)
        {
        }
    }

    // 超时不算业务结果：直接让用例失败
    private static async Task<Exception?> OutcomeAsync(Task write)
    {
        try
        {
            await write.WaitAsync(Guard);
            return null;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Wrap<TService>(IServiceCollection services, Func<TService, TService> wrap) where TService : class
    {
        var descriptor = services.Last(x => x.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(TService), sp => wrap((TService)(
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!))), descriptor.Lifetime));
    }

    // 本用例这一轮交错的信号；不在一轮之内时夹具不做任何事
    private sealed class InterleavingProbe
    {
        private Interleaving? _current;

        public Interleaving? Current => Volatile.Read(ref _current);

        public Interleaving Start(string firstName, string secondName)
        {
            var run = new Interleaving(firstName, secondName);
            Volatile.Write(ref _current, run);
            return run;
        }

        public void Stop()
        {
            Current?.ReleaseFirst();
            Volatile.Write(ref _current, null);
        }
    }

    private sealed class Interleaving(string firstName, string secondName)
    {
        private readonly TaskCompletionSource _firstValidated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondValidated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string FirstName { get; } = firstName;

        public string SecondName { get; } = secondName;

        public Task FirstValidated => _firstValidated.Task;

        public Task SecondValidated => _secondValidated.Task;

        public Task SecondWaiting => _secondWaiting.Task;

        public Task FirstReleased => _releaseFirst.Task;

        public void OnFirstValidated() => _firstValidated.TrySetResult();

        public void OnSecondValidated() => _secondValidated.TrySetResult();

        // 第一次写入通过校验后才会有人在设置锁上等待：那只能是第二次写入
        public void OnLockWaiting()
        {
            if (FirstValidated.IsCompleted)
            {
                _secondWaiting.TrySetResult();
            }
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();
    }

    // 包住宿主实际注册的锁：设置锁已被持有时先发"等待"信号再进入真实等待，信号发出时这次获取确实尚未完成
    private sealed class SignallingLock(IDistributedLock inner, InterleavingProbe probe) : IDistributedLock
    {
        public async Task<ILockHandle> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key != HostLockKey || probe.Current is not { } run)
            {
                return await inner.LockAsync(key, cancellationToken);
            }

            var handle = await inner.TryLockAsync(key, TimeSpan.Zero, cancellationToken);
            if (handle is not null)
            {
                return handle;
            }

            run.OnLockWaiting();
            return await inner.LockAsync(key, cancellationToken);
        }

        public Task<ILockHandle?> TryLockAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.TryLockAsync(key, timeout, cancellationToken);
    }

    // 排在保留期校验器之后，只有通过了违序规则才会走到这里
    private sealed class BarrierValidator(InterleavingProbe probe) : ISettingValueValidator
    {
        public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
        {
            if (probe.Current is not { } run)
            {
                return;
            }

            if (context.Definition.Name == run.FirstName && !run.FirstValidated.IsCompleted)
            {
                run.OnFirstValidated();
                await run.FirstReleased.WaitAsync(Guard, cancellationToken);
            }
            else if (context.Definition.Name == run.SecondName)
            {
                run.OnSecondValidated();
            }
        }
    }
}
#endif
