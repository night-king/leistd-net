using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leistd.Settings.Tests.TestDoubles;

// 直接构造 DefaultSettingManager 的用例共用：真实的进程内锁，不连库的工作单元
internal static class WriteDependencies
{
    public static IDistributedLock Lock() => new MemoryLocalLock(NullLogger<MemoryLocalLock>.Instance);
}
