namespace Leistd.ServiceClient.Exceptions;

/// <summary>服务调用失败的来源；HTTP 宿主可据此决定安全的对外状态。</summary>
public enum ServiceClientFailureKind
{
    /// <summary>未分类的调用故障。</summary>
    Unknown,
    /// <summary>本地客户端配置无效。</summary>
    Configuration,
    /// <summary>传输层无法连接远端服务；不由远端 HTTP 状态推断。</summary>
    Unavailable,
    /// <summary>等待远端调用结果超时；不由远端 HTTP 状态推断。</summary>
    Timeout,
    /// <summary>远端响应不符合约定。</summary>
    InvalidResponse,
    /// <summary>远端明确返回失败状态。</summary>
    RemoteFailure
}
