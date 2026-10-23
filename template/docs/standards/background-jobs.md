# 后台任务规范

请求外的异步工作、跨实例互斥与周期任务，同时遵循 [后端开发规范](./coding-backend.md)。

## 1. 选择与放置

- 请求外的异步工作交给 `IBackgroundTaskQueue`，跨实例互斥用 `IDistributedLock`，定期维护登记为周期任务（`AddRecurringJob`，显式选 `Cluster` 或 `EveryInstance`；任务自身的开关登记时传 `isEnabled`，任务里不判断开关，一轮失败直接抛出，否则照样推进水位）；不另起线程或自造跨实例锁（只护进程内状态的 `lock` 除外）。
- 周期任务的执行记录使用 `IRecurringJobRunObserver`，由宿主实现存储，不包装任务类型。
- 周期任务（`IRecurringJob`，`*Job`）放 Application 所属模块的 `BackgroundJobs/`；常驻消费者 `*Worker` 放所属模块的 `Workers/`。不建跨模块的顶层 `Jobs/`。

## 2. 测试

Job 的截止判据与失败传播放单元测试；批量 SQL、租户过滤与 DI 接线放 PostgreSQL 集成测试；调度器自身行为由框架覆盖，不重复测。集成测试固定 `TimeProvider` 后经 DI 直接执行 Job（配置变体复用既有数据库，只在该变体关闭自动调度），用截止前、等于、后三组数据按业务定义核对 `<` 或 `<=`（时刻按[测试规范 §2.2](./testing.md#22-通用要求)对齐微秒、留毫秒间隔），批量 SQL 后换新作用域读取；涉及租户时宿主与租户都验证。不按 Job 数量增加容器、固定等待或独立 E2E，跨进程责任变化时才扩大到真实运行闭环。
