namespace Leistd.Response.AspNetCore.Attributes;

/// <summary>禁用当前端点的响应包装。</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class NoWrapAttribute : Attribute
{
}
