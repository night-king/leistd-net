namespace Leistd.UnitOfWork.EntityFrameworkCore.Database;

// 同时固定连接归属与物理目标，分别防止跨归属写入和跨库事务。
// 归属键由 IConnectionAffinityProvider 提供，本层不解释其内容。
internal sealed class UnitOfWorkConnectionBinding
{
    private bool _isBound;
    private string? _affinityKey;
    private string? _targetKey;

    public string? TargetKey => _targetKey;

    /// <summary>在尚未解析连接前先校验归属未变，让快路径（复用已创建的 DbContext）也受约束。</summary>
    public void EnsureAffinity(string? affinityKey)
    {
        if (_isBound && !string.Equals(_affinityKey, affinityKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The connection affinity cannot change inside an active unit of work.");
        }
    }

    public void Bind(string? affinityKey, string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        if (!_isBound)
        {
            _affinityKey = affinityKey;
            _targetKey = targetKey;
            _isBound = true;
            return;
        }

        EnsureAffinity(affinityKey);

        if (!string.Equals(_targetKey, targetKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The physical database target cannot change inside an active unit of work.");
        }
    }
}
