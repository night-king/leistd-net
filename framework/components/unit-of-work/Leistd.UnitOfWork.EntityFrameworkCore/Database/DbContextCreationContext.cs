using System.Data.Common;

namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

/// <summary>把异步解析完成的连接信息传入 EF Core 同步 Options 回调。</summary>
/// <remarks>
/// 宿主的 <c>AddDbContext</c> 回调只读取 <see cref="Current"/>，不得在回调中执行远程调用。
/// </remarks>
public sealed class DbContextCreationContext
{
    private static readonly AsyncLocal<DbContextCreationContext?> CurrentContext = new();

    private DbContextCreationContext(string connectionString, DbConnection? existingConnection)
    {
        ConnectionString = connectionString;
        ExistingConnection = existingConnection;
    }

    /// <summary>当前异步控制流已经解析好的创建上下文。</summary>
    public static DbContextCreationContext? Current => CurrentContext.Value;

    /// <summary>最终连接字符串。不得记录到日志或异常。</summary>
    public string ConnectionString { get; }

    /// <summary>同一物理目标上可复用的现有连接；为空时按 <see cref="ConnectionString"/> 创建。</summary>
    public DbConnection? ExistingConnection { get; }

    internal static IDisposable Change(string connectionString, DbConnection? existingConnection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var parent = CurrentContext.Value;
        CurrentContext.Value = new DbContextCreationContext(connectionString, existingConnection);
        return new RestoreContext(parent);
    }

    private sealed class RestoreContext(DbContextCreationContext? parent) : IDisposable
    {
        private DbContextCreationContext? _parent = parent;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentContext.Value = _parent;
            _parent = null;
            _disposed = true;
        }
    }
}
