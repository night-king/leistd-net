using Leistd.UnitOfWork.Options;
using System.Data;

namespace Leistd.UnitOfWork.Attributes;

/// <summary>声明由 AOP 拦截器管理的工作单元边界。</summary>
/// <remarks>仅可标注在实现类或实现方法上；拦截器不读取接口特性。</remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class UnitOfWorkAttribute : Attribute
{
    /// <summary>创建继承宿主默认事务设置的工作单元特性。</summary>
    public UnitOfWorkAttribute()
    {
    }

    /// <summary>创建显式指定事务设置的工作单元特性。</summary>
    /// <param name="isTransactional">是否开启事务。</param>
    public UnitOfWorkAttribute(bool isTransactional)
    {
        IsTransactional = isTransactional;
    }

    /// <summary>工作单元的超时时长；不设置时取默认选项的值。</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>事务隔离级别，仅在开启事务时生效；不设置时取默认选项的值。</summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>为 <see langword="true"/> 时不创建工作单元，其余属性均不生效。</summary>
    public bool IsDisabled { get; set; }

    /// <summary>是否开启事务；不设置时继承宿主的默认选项。</summary>
    public bool? IsTransactional { get; }

    /// <summary>以默认选项为底，套用本特性显式设置的项，得到本次工作单元的选项。</summary>
    /// <param name="defaultUnitOfWorkOptions">宿主注册的默认选项。</param>
    public UnitOfWorkOptions CreateOptionsFromDefault(UnitOfWorkOptions defaultUnitOfWorkOptions)
    {
        return new UnitOfWorkOptions
        {
            IsTransactional = IsTransactional ?? defaultUnitOfWorkOptions.IsTransactional,
            IsolationLevel = IsolationLevel ?? defaultUnitOfWorkOptions.IsolationLevel,
            Timeout = Timeout ?? defaultUnitOfWorkOptions.Timeout
        };
    }
}
