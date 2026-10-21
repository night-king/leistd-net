namespace Leistd.Lock.Abstractions;

/// <summary>标记跨进程互斥的 <see cref="ILock"/>。</summary>
public interface IDistributedLock : ILock
{
}
