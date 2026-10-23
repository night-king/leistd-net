# 周期任务执行观察

状态：已完成（PR #166） · 风险档：高（新增公共 API、调度器运行时语义） · 关联：https://github.com/zengqinglei/leistd-net/pull/166

## 1. 需求

### 问题与背景

CRM V2 三个服务（crm、identity、foundation）基于 Leistd.BackgroundJobs 1.1.2。它们的后台作业页要回答两件事：每次触发是否正常；不正常的原因是什么。框架目前做不到：

- 调度器失败时不写水位，异常只进日志，每次执行的结果与原因都不保留（`RecurringJobRunner.cs:68-82`）。
- `IRecurringJobStatusReader` 只提供登记信息、开关、排期、当前时段与集群完成水位。

三仓的临时方案是包装类型 `AddRecordedRecurringJob<TJob>` → `RecordedRecurringJob<TJob>`：在任务前后各写一条 `JobRun`（crm `backend/src/Crm.Infrastructure/BackgroundJobs/RecordedRecurringJob.cs`，identity、foundation 各有一份等价实现）。这个方案有三个缺口：

1. **框架组件登记的任务包不到。** 例如 `operation-records.archive`、`notifications.retention`、`settings.host-refresh`，以及模板的 OpenIddict 清理，这些任务在页面上只能显示水位。
2. **三份等价包装各自维护。** 包括首写放在 try 内、取消补写、记录失败与任务隔离、开 Activity 等。
3. **包装层看不到调度器内部的事实。** 失锁取消在包装里与停机无法区分；完成水位写入失败时，任务已"成功"，而这一时段会被重做。

业务侧的建议（协调目录 `framework-feedback-job-run-observer.md`）是由调度器提供 `IRecurringJobRunObserver`。本规格在此基础上独立定型。

另外，阅读源码时发现一个现存缺陷：`RecurringJobRunner` 把**所有**非停机引起的 `OperationCanceledException` 都当作"失去集群锁"（`RecurringJobRunner.cs:72-76`）。任务内 HttpClient 超时抛出的 `TaskCanceledException` 也会被记成失锁；每副本任务不持锁，同样会记成失锁。

### 目标

- 调度器在每次**真正开始执行**的周期任务前后回调宿主登记的观察者。登记来源（业务还是框架组件）不影响回调。
- 回调给出：任务名、范围、时段、开始时刻、结果（成功 / 失败 / 停机取消 / 失锁）、耗时、失败异常。
- 每次执行都有独立的 Activity，观察者在其中回调，`Activity.Current` 即本次执行的链路。
- 观察者失败不影响任务、水位与其他观察者。
- 修正失锁误判。

### 非目标

| 不做 | 原因 |
| --- | --- |
| 框架内置执行记录（实体、迁移、保留期、查询 API） | 见 §2"备选与取舍 C" |
| 跳过事件（抢锁失败、本时段已完成、开关关闭） | 见 §2"跳过不回调" |
| 实例标识字段、实例名选项 | 见 §2"实例标识" |
| 执行管道 / 中间件（能改写结果的包装） | 见 §2"备选与取舍 B" |
| Metrics（Meter 计数、耗时直方图） | 没有消费者；以后需要时另加，与本契约正交 |
| 模板接入观察者或作业页 | 模板没有作业监控页，没有消费点 |

### 功能要求与验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| A1 | Given 登记两个观察者和一个成功的集群任务，When 到点执行，Then 每个观察者按登记顺序各收到一次 Started 与一次 Completed(`Succeeded`)；Completed 在水位写入之后；两次回调是同一实例 | 单测 `RecurringJobRunObserverTests` |
| A2 | Given 任务抛出 `BusinessException`，Then Completed(`Failed`) 的 `Exception` 是该异常；水位不写；调度器的 Error 日志照旧 | 单测 |
| A3 | Given 任务执行中宿主停机，Then Completed(`Canceled`)；Given 执行中失去集群锁，Then Completed(`LockLost`)；Given 任务在失锁后**捕获取消并正常返回**（默认内存水位），Then 仍为 `LockLost` 且水位不写；Given 停机与失锁同时发生，Then `Canceled` | 单测（停机用 stoppingToken；失锁用可控 `IDistributedLock` 替身触发 `LockLost`） |
| A4 | Given 任务抛出与调度令牌无关的 `TaskCanceledException`（模拟 HttpClient 超时），Then 结果为 `Failed`，日志不出现"lost its cluster lock"；每副本任务同样如此 | 单测（先写，修复前失败） |
| A5 | Given 任务成功但水位写入抛出，Then Completed(`Failed`)，`Exception` 为存储异常 | 单测（抛出的水位存储替身） |
| A6 | Given 抢锁失败 / 本时段已完成 / 开关关闭 / 开关求值抛出 / 读水位抛出，Then 观察者不回调；后两种照旧记 Error | 单测 |
| A7 | Given 某个观察者的 Started 或 Completed 抛出，Then 任务照常执行、水位照常写入，其余观察者照常回调，记一条带观察者类型的 Warning；Given 观察者集合解析抛出（任一实现构造失败，官方容器整体失败），Then 本次不调用任何观察者，任务与水位照常，记一条 Warning | 单测 |
| A8 | Given 第一个观察者的 Started 期间停机令牌被取消，Then 后续观察者不再收到 Started，任务不执行；**已收到 Started 的观察者**（含抛出者）都收到 Completed(`Canceled`)，未收到 Started 的不收到 Completed | 单测 |
| A9 | Given 观察者为 Scoped，且任务从同一作用域解析一个 Scoped 服务并写入数据，Then 观察者在 Completed 时读得到这份数据（扇出租户统计的接法）；Given 两个任务重叠执行，Then 各自的 Scoped 观察者状态互不覆盖 | 单测 |
| A10 | Given 回调时，Then `Activity.Current` 非空：Started、任务、Completed 三处的 `TraceId` 相同；不同执行的 `TraceId` 互不相同；不继承调度循环残留的 Activity；Failed/LockLost 时状态为 Error；执行结束（成功、异常、跳过）后调度循环的 `Activity.Current` 还原。分三种情形：无监听者（手建）、监听者接受采样（由 `Leistd.BackgroundJobs` 源创建）、监听者采样返回 `None`（手建，只保证本地关联） | 单测 |
| A11 | Given 每副本任务在两个副本上执行，Then 两边各有 Started/Completed，`Scope` 为 `EveryInstance` | 单测（两个容器模拟两副本）+ 端到端 E2 |
| A12 | Given 框架组件在自己的 `Add*` 里登记的任务（`settings.host-refresh`）与宿主自己登记的任务，When 在生成项目里到点执行，Then 宿主观察者都收到回调 | 端到端 E1；单测另以组件同款登记方式（不经宿主）覆盖 |
| A13 | Given 两个副本共享 PostgreSQL 与 Redis，When 集群任务到点，Then 只有一个副本回调 `Succeeded`，另一个副本不回调；删除 Redis 锁键后，执行中的副本回调 `LockLost` | 端到端 E2、E3 |
| A14 | Given 现有调用方（未注册观察者），Then 行为、日志与 1.1.2 一致（失锁误判修正、失锁后正常返回不再记完成、新增 Activity 除外）；现有测试只做内部枚举改名的机械更新，行为断言不改 | 现有测试套件（含队列测试，覆盖共享的 Activity 辅助） |

### 已确认的决策

- 用户 2026-10-23：业务建议仅供参考，按框架规范、开发规范、最佳实践终局独立分析；方案先交 Codex 审查，由 Codex 实施并做端到端验证，Claude 审查代码后做最终汇报。

## 2. 方案

### 现状事实

- 单次执行：`RecurringJobRunner.RunOnceAsync`（`framework/components/background-jobs/Leistd.BackgroundJobs.InProcess/Recurring/RecurringJobRunner.cs`）。执行流程是：开作用域 → 开关 → 范围分支（每副本直接执行）→ 零等待抢锁 → 读水位 → 执行 → 写水位。结果用内部枚举 `RecurringJobRunOutcome`（`Completed / SkippedDisabled / SkippedLocked / SkippedCompleted / Failed / Canceled`）表示，调度循环不读取它（`RecurringJobScheduler.cs:113`）。
- 周期任务**没有** Activity；队列工作项有（`BackgroundTaskQueueWorker.cs:30-86`，ActivitySource `Leistd.BackgroundJobs`，无监听者时手动建 Activity）。
- 组件任务：`OperationRecordArchiveJob`、`NotificationRetentionJob<T>` 经 `ITenantDatabaseRunner` 逐库处理，有库失败时汇总抛出；`HostSettingRefreshJob` 为每副本任务。它们都只经 `AddRecurringJob<TJob>` 登记，不知道宿主的记录方式。
- 业务扇出统计：identity `IJobRunContext`（Scoped）由包装在任务作用域里开启，`TenantFanOutJobRunner` 写入租户总数与失败数，包装在结束时一并落库（`Identity.Application/Ops/BackgroundJobs/JobRunContext.cs`）。

### 设计

#### 公共契约（Core 包，命名空间 `Leistd.BackgroundJobs.Recurring`）

```csharp
/// 观察每次真正开始执行的周期任务。
public interface IRecurringJobRunObserver
{
    Task OnRunStartedAsync(RecurringJobRun run, CancellationToken cancellationToken);
    Task OnRunCompletedAsync(RecurringJobRun run, RecurringJobRunResult result);
}

public sealed record RecurringJobRun(
    string JobName, RecurringJobScope Scope, DateTimeOffset Slot, DateTimeOffset StartedAt);

public sealed record RecurringJobRunResult(
    RecurringJobRunOutcome Outcome, DateTimeOffset CompletedAt, TimeSpan Duration, Exception? Exception);

public enum RecurringJobRunOutcome
{
    Succeeded = 1,   // 任务完成；集群任务的水位已写入
    Failed = 2,      // 任务、任务解析或水位写入抛出；Exception 非空
    Canceled = 3,    // 宿主停机
    LockLost = 4     // 执行中失去集群锁
}
```

- **注册**：宿主用 `services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecurringJobRunObserver, JobRunRecorder>())` 注册，按开发规范 §6.6 的"多实现并存"处理，不另设 `AddXxx` 入口。
- **解析**：每次执行在本次的作用域里用 `GetServices<IRecurringJobRunObserver>()` 解析一次，Started 与 Completed 用同一批实例。观察者因此能读到任务在同一作用域里写入的 Scoped 服务（A9，扇出统计照此接入）。官方容器构造集合时，任一实现构造失败，整个集合都会失败，所以解析失败的处理是：本次不调用任何观察者，记一条 Warning，任务与水位照常（A7）。不为逐个实现隔离解析而扫描 `ServiceDescriptor` 或手动构造。
- **生命周期**（XML 与文档写明）：

  | 生命周期 | 用法 |
  | --- | --- |
  | Scoped（推荐） | 每次执行一个实例，可以在字段里保存本次状态，如自己写入的记录 id |
  | Transient | 本次执行内只解析一次并一直用到 Completed，效果同 Scoped |
  | Singleton | 会被并行运行的多个任务同时调用，必须线程安全，不能在字段里存"当前执行"，也不能捕获 Scoped 服务 |

- **回调时机**：
  - Started 在开关为开、集群任务已取得锁并确认本时段未完成之后、解析任务实例之前回调，按登记顺序逐个调用。
  - Completed 在终态确定之后回调；集群任务的"终态"包含水位写入。
  - **配对规则**：收到 Started 的观察者（包括 Started 抛出的观察者）必定收到一次且只收到一次 Completed；没收到 Started 的不收到 Completed。某个观察者的 Completed 失败不影响其余观察者（A7、A8）。
- **令牌**：Started 收到本次执行的令牌，即停机与失锁的联合令牌。某个观察者的 Started 返回或抛出后，若令牌已取消，就不再调用后续观察者的 Started，任务也不开始，结果按取消原因判定（A8）。Completed 不收令牌：停机时也必须让观察者记下终态。文档要求观察者自己给 I/O 设有限超时，并写明慢回调会延长本轮执行和停机等待；框架不加超时选项。
- **结果判定**：取消类结果只由取消信号得出。

  | 情形 | Outcome |
  | --- | --- |
  | 抛出非 `OperationCanceledException` 的异常（任务、任务解析、水位写入），无论此时令牌状态如何 | `Failed`，保留原异常，记 Error |
  | `OperationCanceledException`（含任务返回后令牌检查抛出的），且 `stoppingToken` 已取消 | `Canceled` |
  | 同上，且 `handle.LockLost` 已取消 | `LockLost` |
  | `OperationCanceledException`，但两个令牌都未取消（如 HttpClient 超时） | `Failed` |
  | 任务返回，写水位前令牌未取消，水位写入成功（每副本任务没有水位） | `Succeeded` |

  停机期间的真实故障按失败上报，与 1.1.2 一致，不被停机掩盖。

  - 任务返回后、写水位前检查一次令牌：停机或失锁已经发生时不写水位，按上表判 `Canceled` / `LockLost`（A3）。检查与写入之间的竞态无法消除，本轮不引入 fencing 或事务协议。
  - 停机与失锁同时出现时判 `Canceled`。宿主停机期间断开 Redis 也可能触发失锁，按失锁上报会在每次发布时制造假告警。
  - 水位写入成功后终态固定，Completed 期间发生的取消不改写结果。
  - `Exception` 的填法：`Failed` 时非空；`Canceled` / `LockLost` 时，如果有捕获到的 `OperationCanceledException` 就带上，否则为 `null`。XML 中写明。
  - 调度器自己的日志随之修正：失锁 Warning 只在判为 `LockLost` 时记；其余取消按失败记 Error（A4）。
- **跳过不回调**：抢锁失败、本时段已完成、开关关闭、开关求值或读水位失败，都发生在 Started 之前，不算一次执行。
  - 抢锁失败和本时段已完成在 N 副本下每个时段各出现 N-1 次，记成事件只会淹没真实执行。
  - 开关状态已经由 `IRecurringJobStatusReader` 给出（`SwitchedOff`）。
  - "该触发却没执行"由消费方根据排期、水位与执行记录推算（业务现有 `missedSlots24h`）。
  - 开关求值和读水位失败属于调度基础设施故障，照旧记 Error，在页面上表现为未触发。文档不写"所有异常都经观察者报告"。
- **观察者隔离**：每个观察者的每次回调各自 `try/catch`。异常记 Warning，带任务名、观察者类型与异常，不影响任务、水位和其余观察者。例外：Started 中因本次令牌取消而抛出的 `OperationCanceledException` 按取消处理（A8），不记 Warning。
- **时刻与耗时**：`StartedAt` 取调用第一个 Started 之前的时刻；`CompletedAt` 取调用第一个 Completed 之前的时刻。`Duration` 用 `TimeProvider.GetTimestamp` 单调计时，覆盖 Started 回调、任务和水位写入，不含抢锁、读水位和 Completed 回调本身。
- **Activity**：每次执行开一个根 Activity，名称为 `Leistd.RecurringJob`，ActivitySource 复用 `Leistd.BackgroundJobs`。
  - 先清空 `Activity.Current`，与队列一致，避免挂到调度循环的残留上下文；在 `finally` 中还原原值，覆盖成功、异常与跳过三条路径。
  - 源创建成功时用它创建的 Activity。返回 `null`（无监听者，或监听者采样返回 `None`）时手动创建，只保证日志与观察者能拿到本地 `TraceId`，不保证经该源导出。
  - 标签：`leistd.job.name`、`leistd.job.slot`、`leistd.job.outcome`。`Failed` / `LockLost` 时 `SetStatus(Error)`。
  - 范围：从开作用域起（开关与锁的日志也带链路）到 Completed 回调结束。跳过的时段也会产生 Activity，只在被采样时导出，代价可以接受。
  - 把 Activity 的创建提取成 `internal` 的共享辅助。它有队列与周期任务两个真实消费者，共用 Source 与"返回 null 时手建"的逻辑，不复制。
- **租户与作用域**：调度器在宿主上下文中运行，任务里 `ICurrentTenant.Change` 的 AsyncLocal 变化不回流到调用方，所以 Started 与 Completed 都在宿主上下文中回调。文档写明一个风险：任务可能在租户切换后从本次作用域解析出绑定租户库的 Scoped `DbContext`。写库的观察者应当用自己的作用域（`IServiceScopeFactory`）或明确的宿主库上下文，不复用本次作用域里的 `DbContext`。
- **实例标识**：调度器没有实例概念，锁和水位都不需要它。实例标识是部署事实，框架不新增字段或选项。文档推荐宿主自己提供一个进程实例标识，执行记录和遥测（OpenTelemetry `service.instance.id`）共用这一个值：
  - 部署能保证唯一时用 Pod 名或机器名（`Environment.MachineName`）。
  - 同一台机器上会跑同一应用的多个进程时（本地开发、单机多副本），用进程启动时生成的 GUID，或满足该部署唯一性的组合。机器名加应用名并不唯一。

#### 调度器改动点（InProcess 包）

| 文件 | 改动 |
| --- | --- |
| `Recurring/RecurringJobRunner.cs` | 加入回调与 Activity；结果判定按上表（含写水位前的令牌检查）；内部枚举改名 `RecurringJobRunnerResult`，避免与公共 `RecurringJobRunOutcome` 同名；观察者的解析、配对回调与隔离写成 runner 内的私有方法（唯一调用方，不新增调用器类或 DI 注册） |
| Activity 共享辅助（新 internal 类型） | ActivitySource 与"返回 null 时手建"逻辑；队列 worker 与 runner 共用 |
| `DependencyInjection.cs` | 不变（观察者由宿主注册） |

Core 包新增 4 个公共类型，带 XML 注释。`IRecurringJob.ExecuteAsync` 的 cancellationToken 注释补一句："失锁时观察者收到 `LockLost`"。

#### 文档同步

- `framework/docs/components/background-jobs.md`：
  - "何时使用"加一行"记录每次执行的结果"。
  - "使用"加一个最小观察者示例（Started 插入、Completed 补全、`Activity.Current?.TraceId`、自建作用域写库）。
  - "接口参考"加四个类型。
  - "实现行为"加回调时机、结果判定、跳过不回调、隔离、Activity 的条目；同时把"失败不记水位：异常只记日志"改为"异常记日志并经观察者报告"。
  - "注意事项"加 `DbContext` 复用风险和实例标识推荐。
- 示例要能被 `scripts/extract-doc-snippets.py` 编译。新增占位类型时，在其符号表登记，参照 `statusReader` 的做法。
- 模板：源码不改。实施时 `git grep -n 'RecurringJob' template/.agents template/docs`，项目 Skill 或规范里讲周期任务的地方补一句"执行记录用 `IRecurringJobRunObserver`"；没有讲周期任务的地方不改。

### 备选与取舍

- **A 观察者（采用）。** 只观察，不能改变控制流，水位与重试语义不受影响。同类先例：Quartz 的 `IJobListener`（`JobToBeExecuted` / `JobWasExecuted`），Hangfire 的 `IServerFilter`（`OnPerforming` / `OnPerformed`）。
- **B 执行管道 / 中间件（否决）。** 包装器能吞异常、改写结果、跳过调用，等于把水位正确性交给宿主代码，而业务需求只是"看"。观察者满足全部需求，权限更小。
- **C 框架内置执行记录（本轮不做）。** 三仓在实体、存储、保留期、查询上仍各有一份，内置看上去能消除这部分重复。不做的理由在职责与成本：
  - 记录形状里有业务语义：扇出租户统计、错误码口径、保留策略、页面查询契约。这些属于宿主，不属于调度器。
  - 内置意味着框架为每个使用者新增一张表、一条迁移、一个保留期任务和一套查询 API，而本次的通用需求只是"让宿主看见每次执行"。

  观察者是这一层的完整契约。宿主怎么存执行记录是宿主的事；本轮不对内置存储做演进承诺。
- **D `DiagnosticListener` / 只靠 Activity（否决作为唯一手段）。** 回调同步，不进 DI 作用域，拿不到 Scoped 服务与写库能力。Activity 保留作为链路关联手段。

### 兼容与回滚

- 公共 API 只增不改。未注册观察者的宿主有三处行为变化：每次执行多一个 Activity，日志因此带上 TraceId/SpanId；失锁误判被修正；失锁或停机后任务正常返回的那一轮不再记完成水位。三者都是修正或补充，不需要调用方做任何改动。
- 自研调度器（实现 `RecurringJobSchedulerMarker` 的宿主）不受影响。要支持观察者需要自行回调，文档写明。
- 提交类型 `feat(background-jobs):`，Minor：**1.1.2 → 1.2.0**，不是破坏性变更，不加 `!`。
- 回滚：直接回退本 PR；业务侧在 1.2.0 之前保留包装方案。

## 3. 计划

分支 `zengqinglei/recurring-job-run-observer`，自最新 `origin/develop` 拉出，目标 `develop`。一个 PR。

| # | 任务 | 产物 | 验证 |
| --- | --- | --- | --- |
| 1 | 回归测试先行：A4 失锁误判 | `RecurringJobExecutionTests` 新用例（修复前失败） | 运行，确认红 |
| 2 | Core 公共类型与 XML 注释 | 4 个类型 | 构建；命名空间与 csproj 闸门 |
| 3 | runner 回调、结果判定、Activity、观察者隔离；队列复用 Activity 辅助 | InProcess 改动 | `Leistd.BackgroundJobs.Tests` 全部通过，含 A1–A11 新用例 |
| 4 | 组件文档与示例 | `background-jobs.md` | `check-all -Only` 文档与片段相关闸门（名称以 `-List` 为准） |
| 5 | 打包与隔离消费 | `pack-local-feed.ps1` 按仓库约定产出的 `1.1.3-local.<时间戳>`（候选版本取 `VERSION` 的下一补丁；正式版号由发布流水线按 `feat` 推算为 1.2.0，不为本地验证改 `VERSION` 或脚本） | 包内 XML 与文档检查；`framework/build/test-package-consumption.ps1 -PackageIds Leistd.BackgroundJobs.Core,Leistd.BackgroundJobs.InProcess` |
| 6 | 受影响组件回归 | — | operation-records、notifications、settings 测试项目；队列测试（共享 Activity 辅助）；片段提取符号表有变化时跑该脚本已有自测 |
| 7 | 端到端（本地包） | 验证记录写入规格 §4 | 见下文"端到端" |
| 8 | 模板 Skill/文档按需补一句 | 视 grep 结果 | `-GenerateOnly` 产物检查（仅在有改动时） |

### 测试矩阵（单测 `Leistd.BackgroundJobs.Tests`）

| 维度 | 取值 |
| --- | --- |
| 范围 | Cluster / EveryInstance |
| 结果 | Succeeded / Failed（任务异常、解析失败、水位写入失败、无关取消）/ Canceled / LockLost（含失锁后正常返回）/ 停机与失锁同时 |
| 跳过 | 锁被占 / 已完成 / 开关关 / 开关抛出 / 读水位抛出 → 零回调 |
| 观察者 | 0 个 / 2 个按序 / Started 抛出 / Completed 抛出 / 集合解析抛出 / Scoped 共享状态 / 两任务重叠 / Started 中取消（配对规则） |
| 链路 | 无监听者手建 / 监听者接受采样由 Source 创建 / 采样 `None` 手建 / 不继承残留 / 结束后还原 / Error 状态 |

测试使用 `FakeTimeProvider` 与可控锁替身，不等待真实时间（质量规范"时间边界与等待"）。

### 端到端（本地包，Codex 执行）

用 `.tmp/local-feed` 中的本地候选包生成一个全功能场景（含操作记录、通知、OpenIddict）的模板项目。在生成项目里临时加入两样东西，只用于验证，不提交到模板：

- 一个日志观察者，把每次回调写成结构化日志。
- 一个短周期集群探针任务 `e2e.probe`，`Every(15s)`，可按开关长跑或抛出。

模板与组件的集群任务都是按每日时刻排期（`DailyAt`），启动后不会立即执行，所以用两种方式覆盖：

- "组件登记的任务同样回调"用 `settings.host-refresh` 验证。它是每副本任务，周期取 `HostSettingOptions.RefreshInterval`，可以调短。
- 集群语义用探针任务验证。

PostgreSQL 与 Redis 按全局约定先探测 `windows` 上下文、回退 `orbstack`，用完删除自己创建的容器。

| 编号 | 场景 | 判据 |
| --- | --- | --- |
| E1 | 单副本启动 | 日志观察者收到 `settings.host-refresh`（组件登记）与 `e2e.probe` 的 Started/Completed(`Succeeded`)；同一次执行的 Started、任务日志、Completed 的 TraceId 一致；探针抛出时回调 `Failed` 且带异常类型 |
| E2 | 两副本（两个进程，同库同 Redis，各自在启动时生成实例 GUID 并写进观察者日志） | 每条回调日志带实例标识、JobName、Slot、TraceId；集群任务每个时段只有一个实例回调 `Succeeded`，另一实例该时段零回调；`settings.host-refresh` 两个实例各自回调 |
| E3 | 单执行副本（另一副本关掉总开关或不启动，排除接管），探针长跑期间删除 Redis 锁键 | 该副本回调 `LockLost`；按探针任务名与该时段查水位，未前进 |
| E4 | 探针长跑期间对进程发 SIGTERM（优雅停机），等待进程退出 | 回调 `Canceled` 后进程退出；强杀不承诺 Completed，不作为判据 |

判据必须能证伪：每条都同时检查"该出现的出现了"和"不该出现的没出现"（如 E2 的另一副本零回调）。

### 版本与发布

- PR 合入 `develop` 后发布 `1.2.0-beta.<N>`，实际后缀以包源为准。正式版 `1.2.0` 需要单独授权 `develop` → `main`。
- 业务侧在 beta 发布前可以先用本地包 `1.1.3-local.<时间戳>`（`.tmp/local-feed`）试接。这个版本号只是本地候选的约定，不代表发布版本。

### 业务项目迁移（三仓各自执行）

1. 升级 `Leistd.*` 到 `1.2.0`（试点期为 `1.2.0-beta.<N>`）。
2. 新增 `JobRunRecorder : IRecurringJobRunObserver`，按 Scoped 用 `TryAddEnumerable` 注册：
   - Started：插入 `Running` 记录。`Instance` 取宿主的进程实例标识：k8s 下用 Pod 名，同机多进程时用启动时生成的 GUID（见 §2"实例标识"）。`TraceId = Activity.Current?.TraceId`。
   - Completed：`Succeeded` → 成功；`Failed` → 记 `Exception.GetType().Name` 与 `(Exception as BusinessException)?.Code`；`Canceled` → Cancelled；`LockLost` → Cancelled，失败类型可写 `LockLost`，由业务决定是否单列。
   - 写库用自建作用域落宿主库，写入失败只记日志。原有的"切回宿主"守卫保留。
3. identity 的 `IJobRunContext`：在 Started 里开启，在 Completed 里读取扇出统计，`TenantFanOutJobRunner` 不改。
4. 所有 `AddRecordedRecurringJob<T>` 改回 `AddRecurringJob<T>`；任务名不变，水位沿用。删除 `RecordedRecurringJob*`、`RecordedRecurringJobRegistration/Extensions`、`RecordedJobMarker`，以及包装里自建 Activity 的代码（crm `CrmBackgroundActivity` 的周期任务用法）。
5. 作业页：所有任务都有执行明细。接口的 `recorded` 字段删除或恒为 `true`；前端删除"框架任务，暂无执行明细"分支；crm Worker 心跳去掉 recorded 标记。附录 A 契约同步修改。
6. 测试：包装测试改写为观察者映射测试（四种结果 → JobRun 字段）。另加一条集成测试：框架组件任务（如 `settings.host-refresh`）到点后产生 `JobRun`。

### 不做的事

见 §1"非目标"。另外，本轮不改 `IRecurringJobStatusReader`，"最近一次执行"由观察者自己的存储提供。

## 4. 结果

### 实际交付

PR #166（分支 `zengqinglei/recurring-job-run-observer`，基于 `origin/develop` `fff81b68`）。

- **Core**：新增 `IRecurringJobRunObserver`、`RecurringJobRun`、`RecurringJobRunResult`、`RecurringJobRunOutcome`；`IRecurringJob` 的 XML 补充失锁说明。
- **InProcess**：
  - `RecurringJobRunner` 加入观察者回调、结果判定、写水位前的令牌检查和每次执行的 Activity；内部枚举改名 `RecurringJobRunnerResult`。
  - 新增 internal 类型 `BackgroundJobDiagnostics`，队列与周期任务共用；队列 worker 改为复用它。
- **文档**：`framework/docs/components/background-jobs.md`（示例、接口参考、实现行为、注意事项）；`template/docs/standards/background-jobs.md` 补一句；`scripts/extract-doc-snippets.py` 为示例加业务存储桩。
- **测试**：新增 `RecurringJobRunObserverTests` 和测试替身 `ObserverTestHost`；A4 回归用例加在 `RecurringJobExecutionTests`。

### 与计划的偏差

- 本地包版本按仓库约定为 `1.1.3-local.<时间戳>`，没有用最初写的 `1.2.0-local`（阶段 1 审查意见 7）。
- 阶段 1 审查（Codex）七条意见全部采纳，方案据此修订：
  - 集合解析失败时整组跳过；
  - 生命周期约束写明；
  - 写水位前检查令牌，停机优先于失锁；
  - 删除独立的调用器类；
  - Activity 采样兜底与耗时口径写明；
  - 实例标识改由宿主提供；
  - 补齐验收边界。
- 代码审查（Claude）发现一处回归：停机期间的非取消异常被判为 `Canceled`，且不记日志。已修正为"只有取消异常才判取消类结果"，结果判定表同步修改。

### 验收记录（证据在 `.tmp/run-observer/evidence/`，未提交）

| 项 | 结果 |
| --- | --- |
| A4、R1 回归先红 | 修复前断言失败（`a4-before.log`、`review-r1-before.log`），修复后通过 |
| `Leistd.BackgroundJobs.Tests` | 101/101 通过 |
| 受影响组件 | operation-records 253/253、settings 176/176、notifications 107/107（Redis 背板用真实 Redis 补跑） |
| 静态闸门 | 12 项定向闸门通过 |
| 模板生成 | `identity-all-features -GenerateOnly` 通过 |
| 打包与消费 | 73 包；完整隔离消费与 149 段示例编译通过；最终包 `1.1.3-local.20261023102536` 对两个目标包做了隔离消费 |
| 端到端 E1–E4 | 37 条断言通过（真实 PostgreSQL、Redis，两个进程）。E1：组件登记的任务也回调，三处 TraceId 一致。E2：每个时段只有一个实例执行，另一实例零回调。E3：删除锁键后为 `LockLost`，水位不前进。E4：SIGTERM 后为 `Canceled`，退出码 0 |

E1–E4 跑在 R1 修复之前。R1 只影响"取消信号与非取消异常同时出现"的分类，端到端覆盖的路径不受影响，因此沿用原证据，由单测回归。

### 遗留项

- PR #166 合入 develop 后发布 `1.2.0-beta.<N>`；正式版 1.2.0 需要单独授权。
- 三仓迁移按 §3"业务项目迁移"执行，由业务侧负责。
- 远端 CI、模板全量矩阵与前端测试未在本地执行，由 PR CI 承担。

### 已上收的常驻文档

`framework/docs/components/background-jobs.md`、`template/docs/standards/background-jobs.md`。
