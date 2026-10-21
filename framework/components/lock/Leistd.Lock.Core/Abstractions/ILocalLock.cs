namespace Leistd.Lock.Abstractions;

/// <summary>标记仅在当前进程内互斥的 <see cref="ILock"/>。</summary>
public interface ILocalLock : ILock
{
}
