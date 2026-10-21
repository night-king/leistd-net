namespace Leistd.UnitOfWork.Database;

/// <summary>管理挂在同一个工作单元上的事务 API。</summary>
public interface ITransactionApiContainer
{
    /// <summary>按稳定键查找事务 API；不存在返回 <see langword="null"/>。</summary>
    ITransactionApi? FindTransactionApi(string key);

    /// <summary>登记一个事务 API。已存在的键不可替换。</summary>
    void AddTransactionApi(string key, ITransactionApi api);
}
