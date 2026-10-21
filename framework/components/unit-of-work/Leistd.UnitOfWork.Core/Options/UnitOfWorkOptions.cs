using System.Data;

namespace Leistd.UnitOfWork.Options;

/// <summary><see cref="IUnitOfWorkOptions"/> 的可变实现，用于装配默认选项与单次工作单元的选项。</summary>
public class UnitOfWorkOptions : IUnitOfWorkOptions
{
    /// <summary>默认配置节路径。</summary>
    public const string SectionName = "Leistd:UnitOfWork";

    /// <inheritdoc />
    public bool IsTransactional { get; set; } = true;

    /// <inheritdoc />
    public IsolationLevel? IsolationLevel { get; set; }

    /// <inheritdoc />
    public TimeSpan? Timeout { get; set; }

    /// <summary>创建选项：开启事务，其余未设置。</summary>
    public UnitOfWorkOptions()
    {
    }

    /// <summary>复制一份选项；默认选项是共享实例，按次修改前必须先复制。</summary>
    public UnitOfWorkOptions Clone()
    {
        return new UnitOfWorkOptions
        {
            IsTransactional = IsTransactional,
            IsolationLevel = IsolationLevel,
            Timeout = Timeout
        };
    }
}
