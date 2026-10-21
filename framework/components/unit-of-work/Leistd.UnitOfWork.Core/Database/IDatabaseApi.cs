namespace Leistd.UnitOfWork.Database;

/// <summary>标记由工作单元协调的数据库资源。</summary>
/// <remarks>
/// 工作单元不释放数据库 API（与 <see cref="ITransactionApi"/> 相反）；需要随工作单元释放的资源注册到 <c>IUnitOfWork.ServiceProvider</c>。
/// </remarks>
public interface IDatabaseApi
{
}
