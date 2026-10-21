namespace CompanyName.ProjectName.Application.OperationRecords.Provider;

/// <summary>操作动作的类别，驱动界面的分类筛选。</summary>
/// <remarks>
/// <para>类别是业务词汇，由本项目定义：框架只接受字符串，不预置清单。业务增长时直接在这里加
/// （如"订单""结算"），不需要改框架。</para>
/// <para>取值同时是界面词条的键（<c>operationRecords.categories.*</c>），<b>一旦发布就不要改</b>。</para>
/// </remarks>
public static class OperationRecordCategories
{
#if (LocalIdentity)
    /// <summary>登录、登出、改密、MFA、外部登录绑定、令牌签发。</summary>
    public const string Authentication = "authentication";

#endif
    /// <summary>用户与角色本身的增删改。</summary>
    public const string Account = "account";

    /// <summary>权限授予与角色分配——改变"谁能做什么"。</summary>
    public const string Authorization = "authorization";

    /// <summary>租户的创建、启停与连接配置。</summary>
    public const string Tenant = "tenant";

    /// <summary>设置变更。</summary>
    public const string Configuration = "configuration";

    /// <summary>业务数据的变更。</summary>
    public const string Data = "data";
}
