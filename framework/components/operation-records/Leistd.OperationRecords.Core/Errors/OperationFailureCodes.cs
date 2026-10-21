namespace Leistd.OperationRecords.Errors;

/// <summary>本组件自身会写进 <see cref="Models.OperationFailure.Code"/> 的失败码。</summary>
/// <remarks>
/// <para>除此之外，记录里的码都来自宿主自己的业务拒绝。这些是写进记录的失败原因码，错误响应不带它们。</para>
/// <para>中英默认译文随包分发，由 <c>AddOperationRecords()</c> 登记；宿主在自己的资源里写同名键即可改文案。</para>
/// </remarks>
public static class OperationFailureCodes
{
    /// <summary>授权未通过。由 <c>RecordDeniedOperationAsync</c> 在调用方未给出更具体的原因时补上。</summary>
    /// <remarks>无占位参数。</remarks>
    public const string Forbidden = "Error:Forbidden";
}
