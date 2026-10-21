namespace Leistd.UnitOfWork.Database;

/// <summary>管理挂在同一个工作单元上的数据库 API。</summary>
public interface IDatabaseApiContainer
{
    /// <summary>按稳定键查找数据库 API；不存在返回 <see langword="null"/>。</summary>
    IDatabaseApi? FindDatabaseApi(string key);

    /// <summary>登记一个数据库 API。已存在的键不可替换。</summary>
    void AddDatabaseApi(string key, IDatabaseApi api);
}
