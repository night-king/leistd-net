using System.Data;

namespace Leistd.UnitOfWork.Options;

/// <summary>工作单元的事务选项。</summary>
public interface IUnitOfWorkOptions
{
    /// <summary>是否开启事务。</summary>
    bool IsTransactional { get; }

    /// <summary>事务隔离级别，仅在 <see cref="IsTransactional"/> 为 <see langword="true"/> 时生效；<see langword="null"/> 时使用数据库默认级别。</summary>
    IsolationLevel? IsolationLevel { get; }

    /// <summary>工作单元内上下文的命令超时，按整秒向上取整，不覆盖上下文已设置的值；<see langword="null"/> 时不设置。</summary>
    TimeSpan? Timeout { get; }
}
