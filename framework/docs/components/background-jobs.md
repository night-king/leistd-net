# 后台作业：周期任务与进程内队列

周期任务按对齐到 UTC 的时段执行，登记时显式选择"全集群一份"或"每副本一份"；进程内队列把不该占着请求的工作挪到后台，并带上入队时的上下文。

## 何时使用

| 场景 | 用法 |
| --- | --- |
| 定期清理、归档、扫描共享数据，多副本只该跑一份 | `AddRecurringJob<TJob>(name, schedule, RecurringJobScope.Cluster)` |
| 定期刷新本进程内状态（缓存、配置），每个副本都要跑 | `RecurringJobScope.EveryInstance` |
| 记录每次执行的结果 | 注册 `IRecurringJobRunObserver`，由宿主决定存储与查询 |
| 组件自带维护任务 | 组件在自己的 `Add*` 里登记，宿主只注册调度器；登记了任务而没有调度器时，宿主启动记一条 Warning |
| 请求里触发、丢了也只是少做一次的工作（非关键通知、缓存预热） | 注入 `IBackgroundTaskQueue` 入队 |
| 必须完成、失败要重试的作业 | 不用本组件的队列：与业务同事务写库，或引入持久化作业调度器 |

## 安装

```bash
# 契约：组件声明任务只需要它
dotnet add package Leistd.BackgroundJobs.Core

# 进程内调度器与队列
dotnet add package Leistd.BackgroundJobs.InProcess

# 多副本共享的完成水位
dotnet add package Leistd.BackgroundJobs.EntityFrameworkCore
```

## 注册

```csharp
builder.Services.AddRedisDistributedLock(redisConnectionString);   // 集群任务需要分布式锁
builder.Services.AddInProcessBackgroundJobs();
builder.Services.AddBackgroundJobsEfCore<AppDbContext>();          // 多副本部署

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ConfigureBackgroundJobs();
}
```

## 使用

```csharp
public sealed class LeadRecycleJob(LeadService leads) : IRecurringJob
{
    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        => leads.RecycleStaleAsync(cancellationToken);
}

builder.Services.AddRecurringJob<LeadRecycleJob>(
    "crm.lead-recycle",
    RecurringJobSchedule.DailyAt(new TimeOnly(18, 0)),
    RecurringJobScope.Cluster);
```

排期来自选项时用工厂重载，调度器启动时取一次：

```csharp
using Microsoft.Extensions.Options;

builder.Services.AddRecurringJob<LeadRecycleJob>(
    "crm.lead-recycle",
    sp => RecurringJobSchedule.DailyAt(sp.GetRequiredService<IOptions<LeadOptions>>().Value.RecycleAt),
    RecurringJobScope.Cluster);
```

任务有自己的开关（如来自选项或宿主设置）时，登记时交给调度器，不要在任务里读到关闭就返回——那样调度器会把这一轮记成完成，监控也显示为正常。开关每轮执行前在本轮的作用域里求值，任务实例也从这个作用域解析，读 `IOptionsSnapshot` 时两边看到同一份配置：

```csharp
using Microsoft.Extensions.Options;

builder.Services.AddRecurringJob<LeadArchiveJob>(
    "crm.lead-archive",
    RecurringJobSchedule.DailyAt(new TimeOnly(19, 0)),
    RecurringJobScope.Cluster,
    isEnabled: sp => sp.GetRequiredService<IOptionsSnapshot<LeadOptions>>().Value.ArchiveEnabled);

// 任务里同样用 IOptionsSnapshot 读其余参数，不再判断开关
public sealed class LeadArchiveJob(IOptionsSnapshot<LeadOptions> options, LeadService leads) : IRecurringJob
{
    public Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
        => leads.ArchiveClosedAsync(options.Value.ArchiveAfter, cancellationToken);
}
```

记录每次真正开始的执行时，宿主提供存储并按 Scoped 登记观察者；组件内部登记的任务也会回调。下面的 IJobRunStore 代表宿主自己的执行记录存储：`InsertAsync` 按给定 Id 插入开始记录，`SaveAsync` 按 Id 写入终态、记录不存在时补建。Id 在 Started 之前生成，Started 被取消、超时或插入结果未知时，Completed 仍落到同一条记录，不会留下停在“执行中”的重复行：

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;

builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecurringJobRunObserver, JobRunRecorder>());
```

```csharp
using System.Diagnostics;

public sealed class JobRunRecorder(IServiceScopeFactory scopes) : IRecurringJobRunObserver
{
    private readonly Guid recordId = Guid.CreateVersion7();

    public async Task OnRunStartedAsync(RecurringJobRun run, CancellationToken cancellationToken)
    {
        // 持锁期间执行：除了停机与失锁的令牌，还要有自己的上限
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRunStore>()
            .InsertAsync(recordId, run, Activity.Current?.TraceId.ToString(), timeout.Token);
    }

    public async Task OnRunCompletedAsync(RecurringJobRun run, RecurringJobRunResult result)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRunStore>()
            .SaveAsync(recordId, run, result, timeout.Token);
    }
}
```

作业监控读取本进程每个任务的实际状态：

```csharp
var statuses = await statusReader.GetStatusesAsync(cancellationToken);   // IRecurringJobStatusReader
foreach (var status in statuses.Where(s => s.State != RecurringJobScheduleState.Active))
{
    logger.LogInformation("{Job} is {State}", status.Name, status.State);
}
```

入队工作项拿到的是执行时新建作用域的服务提供器：

```csharp
if (!queue.TryQueue((services, ct) => services.GetRequiredService<WelcomeMailer>().SendAsync(userId, ct)))
{
    logger.LogWarning("The background queue is full; the welcome mail for {UserId} was dropped.", userId);
}
```

## 接口参考

| 成员 | 说明 |
| --- | --- |
| `IRecurringJob.ExecuteAsync(context, ct)` | 执行一次；每次在新作用域里解析实例，令牌在停机或失去集群锁时取消 |
| `RecurringJobContext` | `JobName` 与所属时段起点 `Slot` |
| `RecurringJobSchedule.Every(interval)` / `DailyAt(timeOfDayUtc)` | 排期；时段按 UTC 对齐，间隔不小于 1 秒 |
| `RecurringJobScope` | `Cluster`（锁 + 水位，全集群一份）/ `EveryInstance`（每副本一份），没有默认值 |
| `AddRecurringJob<TJob>(name, schedule \| scheduleFactory, scope, isEnabled?)` | Core 包：登记任务；同名同类型幂等（保留首次登记的排期、范围与开关），同名不同类型抛出 |
| `RecurringJobDefinition.IsEnabled` | 任务自身的开关，`null` 为总是开启；自研调度器须按下文“实现行为”的同一语义求值 |
| `IRecurringJobStatusReader.GetStatusesAsync(ct)` | InProcess 包实现：按登记顺序返回本进程每个任务的 `RecurringJobStatus` |
| `RecurringJobStatus` | `Name`、`Scope`、`State`、`Schedule`（实际生效的排期，未排期时为空）、`CurrentSlot`（读取时刻所在时段）、`LastCompletedSlot`（集群任务取共享水位，与本进程是否排期无关；每副本任务为空） |
| `RecurringJobScheduleState` | `NotStarted` / `DisabledInProcess` / `DisabledByName` / `InvalidSchedule` / `SwitchedOff` / `Active`；只有 `Active` 会按排期执行 |
| `IRecurringJobRunObserver` | 开始与结束回调；按登记顺序、同实例配对，在本次任务作用域中执行 |
| `RecurringJobRun` | 任务名、范围、时段及开始时刻 |
| `RecurringJobRunResult` | 固定终态、结束时刻、单调耗时及异常；耗时含 Started、任务和水位写入，不含准入及 Completed |
| `RecurringJobRunOutcome` | `Succeeded` / `Failed` / `Canceled`（停机）/ `LockLost` |
| `IRecurringJobStateStore` | 集群任务的完成水位 |
| `IBackgroundTaskQueue.QueueAsync` / `TryQueue` | 入队；满时等待或返回 `false` |
| `AddInProcessBackgroundJobs(configure?, configSectionPath?)` | InProcess 包：注册调度器、队列与进程内水位；绑定 `configSectionPath`（默认 `Leistd:BackgroundJobs`）及其 `InProcess` 子节，校验消息按实际路径报键；幂等，换用另一配置节时抛出 |
| `RecurringJobSchedulerMarker` | Core 包：调度器实现登记的标记（`TryAddSingleton<RecurringJobSchedulerMarker>()`）；自研调度器不登记时，启动检查会误报"没有调度器" |
| `AddBackgroundJobsEfCore<TDbContext>()` / `ConfigureBackgroundJobs(modelBuilder)` | EF 包：共享水位存储，与注册顺序无关地替换进程内实现；同一上下文重复调用幂等，已注册其他水位存储或另一上下文的 EF 存储时抛出 |

## 配置项（Leistd:BackgroundJobs）

| 键 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `Enabled` | `bool` | `true` | 本进程是否执行周期任务；一次性进程或只让部分副本跑任务时关掉，队列不受影响 |
| `DisabledJobs` | `string[]` | 空 | 本进程停用的任务名 |
| `InProcess:QueueCapacity` | `int` | `256` | 队列容量；满了就让生产者慢下来 |

## 实现行为

- 时段对齐 UTC：`Every` 从 UTC 纪元按间隔切分，`DailyAt` 按每天的 UTC 时刻切分；不同副本、重启前后算出同一时段。
- 到点按墙钟判定：计时器提前唤醒时补等到时段起点再执行，不会把本时段当成上一时段处理。
- 集群任务零等待抢锁并比对水位：抢不到锁或本时段已完成即跳过，这两种跳过只记 Debug 日志（多副本下每轮都会出现）。锁丢失时取消传给任务的令牌。
- 任务自身的开关每轮到点后、抢锁之前在本轮作用域里求值：关闭时本轮不执行、不记水位；只在开关切换时各记一条 Information 日志。重新打开后在下一次正常到点时恢复，不补跑关闭期间的时段，也不因打开而立即执行。已按开启放行的一轮照常做完，不被中途关闭取消。开关求值抛出按本轮失败处理，不当成关闭。
- 总开关、停用名单与排期在调度器启动时定一次；状态读取用的是这同一份结果，任务自身的开关在读取时求值。读取时开关求值或水位读取失败，整次读取抛出；取消照常传播。
- 失败不记水位：执行阶段的异常记日志并经观察者报告，本轮不重试，最迟在下一个调度时段重做。任务必须幂等，并按截止时间扫描历史积压；要分钟级恢复就排成短周期。
- Started 在开关放行、抢锁成功且确认时段未完成后，任务实例解析前回调；Completed 在任务与水位写入的终态确定后回调。Started 抛出也会配对 Completed；Started 期间取消后，后续观察者与任务不再开始，未收到 Started 的实例不收到 Completed。
- 停机优先判 `Canceled`，其次失锁判 `LockLost`；其他异常（含任务自己的超时取消）判 `Failed`，保留原异常。任务返回后、写水位前再检查取消，即使任务吞掉取消也不写完成；水位写入成功后终态固定。取消/失锁携带捕获的取消异常（若有），否则为空。
- 抢锁失败、时段已完成、开关关闭以及开始前开关求值/读水位失败均不回调，基础设施故障仍记日志。观察者集合解析失败时记一条 Warning，本次无回调，任务继续；单个回调抛出时记带实现类型的 Warning，不影响任务、水位及其他回调。
- 周期执行从开作用域至 Completed 结束拥有独立根 Activity，名称为 "Leistd.RecurringJob"，源为 `Leistd.BackgroundJobs`；带任务名、时段、终态标签，Failed/LockLost 标为 Error。结束后还原原 Activity；源返回 null（无监听者或采样拒绝）时手建 Activity，仅保证本地 TraceId 关联。跳过也有 Activity，但没有观察者回调。
- 登记了集群任务却没有 `IDistributedLock` 时宿主启动失败。
- 按间隔排期的任务启动后在不超过 30 秒（或一个间隔）的随机延迟内先执行一次；按每日时刻排期的只在排定时刻执行。
- 队列入队时经 `IAmbientContext.Capture()` 捕获主体、租户与关联标识，执行时还原；工作单元与请求对象不随行。未注册环境上下文时工作项在空上下文里执行。
- 工作项在自己的 Activity 里执行，父链路是入队时的那一条（没有则开新的根链路）。有监听者时经 `ActivitySource` `Leistd.BackgroundJobs` 创建，OpenTelemetry 订阅这个名字即可导出。
- 停机时队列先关写入口；正在执行的工作项收到取消令牌，尚未开始的项被丢弃。

## 注意事项

- 多副本部署要注册共享水位（`AddBackgroundJobsEfCore`）与 Redis 分布式锁；进程内水位与内存锁只对单副本成立。
- 观察者推荐 Scoped，有状态的 Transient 在本次仅解析一次；Singleton 会被多个任务并发调用，须线程安全，不能捕获 Scoped 或用字段存“当前执行”。
- Started 与 Completed 都在持锁期间执行，两者都要为 I/O 自设有限超时。Started 收到停机与失锁的联合令牌，但不响应令牌的慢回调会一直占着集群锁，其他副本这一时段全部跳过；自设超时触发时按回调失败记 Warning，任务照常执行。Completed 不接收执行令牌，慢回调会延长本轮执行、持锁时间与停机等待。
- Started 的写入可能被取消、超时或结果未知，Completed 不能依赖 Started 的返回值：记录 Id 由观察者预先生成，Completed 按 Id 幂等写入，如上例。
- 观察者在宿主上下文回调，但任务可能已在共享作用域解析过租户库 DbContext。执行记录写入应像上例另建作用域并使用宿主库，不复用任务的 DbContext；共享的普通 Scoped 统计服务仍可在 Completed 读取。
- 实例标识由宿主提供，执行记录与遥测的 `service.instance.id` 共用该值；部署保证唯一时可用 Pod/机器名，同机多进程用启动时生成的 GUID 或其他唯一组合。机器名加应用名不能区分同应用的多个进程。
- 自研调度器如需支持观察者，应自行实现上述回调与隔离契约；Core 登记本身不触发回调。
- 任务执行时没有请求主体与租户；逐个租户库处理时用多租户组件的 `ITenantDatabaseRunner`。
- 队列不持久：进程退出时未执行的工作项丢失，`TryQueue` 的返回值必须处理。
- 任务名同时是锁与水位的键，发布后改名会让旧水位作废。
- 状态读取描述的是调用方所在进程：任务跑在独立 Worker 时，在 Worker 里读取或由它上报；某个进程总开关关闭不代表整个集群都不跑。集群任务的水位来自共享存储，在与调度器相同的宿主上下文里读取（不在租户作用域内）。
- `CurrentSlot` 只是当前所在时段：任务可能正在执行、在等启动抖动，或刚在本时段之后重新打开。是否错过窗口由监控按自己的宽限判定，框架不内置告警。
- `AddRecurringJob` 末尾新增了可选参数 `isEnabled`：普通源码调用不用改，但方法签名变了，依赖它的组件与业务程序集要一起升级并重新编译；自研调度器要实现开关语义。

## 相关

- [分布式锁与本地锁](./lock.md)
- [多租户](./multi-tenancy.md)
