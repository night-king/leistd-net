# 分布式锁与本地锁

协调多个执行者对同一资源的并发访问，确保同一时刻只有一个进入临界区。业务按意图注入 `IDistributedLock`（跨副本互斥）或 `ILocalLock`（进程内互斥），内存（单机）与 Redis（分布式）之间切换只改 DI 注册，不改调用代码。

## 何时使用

| 场景 | 推荐实现 |
| --- | --- |
| 多实例部署，需跨进程/跨节点互斥（如分布式定时任务、库存扣减） | `Leistd.Lock.Redis` |
| 单机部署或单元/集成测试，只需进程内互斥 | `Leistd.Lock.Memory` |
| 仅依赖抽象编写业务代码（领域服务、应用服务） | 只引用 `Leistd.Lock.Core` |

> 内存实现仅在单进程内有效，多实例部署时不能用它做分布式互斥（见[注意事项](#注意事项)）。

业务代码需要跨实例互斥时依赖 `IDistributedLock`；单副本和测试可注册内存实现，多副本必须注册 Redis 实现。

## 安装

```bash
dotnet add package Leistd.Lock.Core

dotnet add package Leistd.Lock.Redis
dotnet add package Leistd.Lock.Memory
```

## 注册

在 `Program.cs` 按部署形态注册：

```csharp
// 单副本：内存锁同时充当 IDistributedLock 的兜底
builder.Services.AddMemoryLocalLock();

// 多副本：Redis 提供 IDistributedLock；需要进程内互斥时再加内存锁，两者可共存
builder.Services.AddRedisDistributedLock("localhost:6379"); // 绑定 Leistd:Lock:Redis，可选委托在绑定后应用
builder.Services.AddMemoryLocalLock();
```

- `AddMemoryLocalLock` 绑定 `ILocalLock`；此时尚无 `IDistributedLock` 实现的话，也用它兜底 `IDistributedLock`。
- `AddRedisDistributedLock` 绑定 `IDistributedLock`，并替换内存锁的兜底，与两者的调用顺序无关。
- 宿主自己注册了 `IDistributedLock` 时，两者都不覆盖它，无论注册在内存锁之前还是之后：Redis 只移除内存锁登记的那一条兜底。
- `ILock` 只是两个接口的基接口，不注册为服务。

内存锁兜底 `IDistributedLock` 时，首次解析会记录 Warning，提示它不提供跨进程互斥。

## 使用

按意图注入 `IDistributedLock` 或 `ILocalLock`，通过 `await using` 让锁在离开作用域时自动释放：

```csharp
public class OrderService(IDistributedLock distributedLock)
{
    public async Task PlaceOrderAsync(string orderKey)
    {
        await using var handle = await distributedLock.LockAsync($"order:{orderKey}");
    }

    public async Task<bool> TryPlaceOrderAsync(string orderKey)
    {
        await using var handle = await distributedLock.TryLockAsync(
            $"order:{orderKey}", TimeSpan.FromSeconds(3));
        if (handle is null) return false;
        return true;
    }
}
```

`ILockHandle` 实现 `IAsyncDisposable`，`await using` 释放时自动解锁；需要自己控制释放时机时显式 `await handle.DisposeAsync()` 即可，同样带持有者校验。

## 接口参考

`Leistd.Lock.Core` 包的 API 位于 `Leistd.Lock.Abstractions`：

| 成员 | 说明 |
| --- | --- |
| `ILock` | 两个锁接口的基接口（下列方法的定义方），不注册为服务 |
| `ILock.LockAsync(key, ct)` | 阻塞加锁直到成功，返回 `ILockHandle`；取消时抛 `OperationCanceledException` |
| `ILock.TryLockAsync(key, timeout, ct)` | 尝试加锁，超时返回 `null`（非异常） |
| `ILockHandle.LockLost` | 持锁资格失效时被取消（租约续期失败）；长临界区应并入自己的 `CancellationToken` |
| `IDistributedLock : ILock` | 标记接口，表达"需要分布式锁"的依赖意图 |
| `ILocalLock : ILock` | 标记接口，表达"需要本地锁"的依赖意图 |
| `ILockHandle : IAsyncDisposable` | 锁句柄，`await using` 离开作用域时自动释放 |

## 实现行为

### Leistd.Lock.Memory（内存本地锁）

- 每个 key 对应一个 `SemaphoreSlim(1,1)`，按 key 互斥；`TryLockAsync` 用信号量超时等待实现。
- 随注册自动启用 `MemoryLockCleanupHostedService`：每 1 分钟扫描，没有持锁者或等待者且空闲超过 5 分钟的 key 被回收。
- 以 Singleton 注册，进程内有效；进程重启后锁状态丢失。

### Leistd.Lock.Redis（分布式锁）

- 基于 StackExchange.Redis 的原子获取与释放操作。每次加锁生成随机 token，由 `ILockHandle` 释放时校验 token 再删除，避免误删他人持有的锁。
- 锁默认过期 30 秒（`RedisLockOptions.Expiry`），持有者崩溃后自动释放。
- `LockAsync` / `TryLockAsync` 以 50ms（`RedisLockOptions.RetryInterval`）间隔轮询重试获取。
- 释放锁只经句柄（`await using` 或显式 `await handle.DisposeAsync()`），带持有者校验。组件不提供按 key 强制解锁：强制删除 key 可能让新旧执行者同时进入临界区。

## 配置项（`Leistd:Lock:Redis`）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `KeyPrefix` | 空 | 锁 key 前缀。**多个应用共用一个 Redis 实例时必须设置**：没有前缀时锁 key 就是业务键，两个不相关的应用键名撞上就会互相阻塞，而两边日志各自都正常。推荐 `应用名:环境:` |
| `Expiry` | `00:00:30` | 租约时长。句柄按租约三分之一自动续期，因此本值实际决定的是「持有者进程异常终止后锁最多被占多久」 |
| `RetryInterval` | `00:00:00.05` | 抢锁失败后的重试间隔。本实现按轮询抢锁（不要求宿主开启 `notify-keyspace-events`）；高并发争抢同一把锁时应适当放大 |

`Expiry` 与 `RetryInterval` 必须为正，在启动期校验。`KeyPrefix` 为 `null` 时归一化为空串。

```json
{
  "Leistd": { "Lock": { "Redis": { "KeyPrefix": "acme-shop:prod:", "Expiry": "00:01:00" } } }
}
```

## 超时语义

两个实现对 `TryLockAsync` 的语义完全一致：

- `TimeSpan.Zero` 表示试一次、不等待。
- 正超时内按 `RetryInterval` 轮询重试，直到拿到锁或超时。
- 计时用单调时钟（`TimeProvider.GetTimestamp/GetElapsedTime`），不受系统时钟调整影响。
- 取消令牌在每次尝试前检查，取消时抛 `OperationCanceledException`，不返回 `null`。

## 注意事项

- `TryLockAsync` 返回 `null` 表示未抢到锁，不抛异常，调用方必须判空并处理降级。
- 内存实现也绑定了 `IDistributedLock`，但互斥范围仅限单进程，多实例部署中不同节点会同时进入临界区。
- Redis 实现的过期时长、轮询间隔与 key 前缀由 `RedisLockOptions`（配置节 `Leistd:Lock:Redis`）给出。句柄会按租约的三分之一周期自动续期（续期时校验 token，不会误续别人的锁），因此临界区超过租约时长不再等于自动失去锁。
- 续期失败即失去持锁资格：`ILockHandle.LockLost` 被取消，释放时不再删除 key。长事务、数据迁移、批量初始化等临界区应把该令牌与自己的 `CancellationToken` 关联。
  进程内实现没有租约，该令牌永不取消。
