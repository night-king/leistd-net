namespace CompanyName.ProjectName.Infrastructure.Persistence;

/// <summary>本服务在数据库中占用的 schema 与迁移历史表名。</summary>
/// <remarks>
/// <para><b>这是本服务的身份，不是装饰。</b>多个服务共用同一个物理数据库时，靠 schema 分开各自的
/// 表；<see cref="Name"/> 与迁移历史表名<b>必须配对</b>修改——只改 schema 会让多个服务争用同一张
/// <c>__EFMigrationsHistory</c>，表现是"另一个服务的迁移被当成本服务已执行过"，
/// 而这个错误只在部署之后暴露。</para>
/// <para>因此这两组值集中在这里。改服务名/租户库布局时改这一处，
/// 所有 <c>HasDefaultSchema</c> 与 <c>MigrationsHistoryTable</c> 一起跟随；
/// 已生成的 migration 文件里是快照值，需要一并重建迁移。</para>
/// </remarks>
public static class DatabaseSchema
{
    /// <summary>本服务的 schema 名</summary>
    public const string Name = "companyname-projectname";

    /// <summary>业务库的迁移历史表</summary>
    public const string BusinessMigrationsHistoryTable = "__EFMigrationsHistory";

#if (LocalIdentity && IncludeMultiTenancy)
    /// <summary>控制面库的迁移历史表。与业务库分开，两者迁移各自演进</summary>
    public const string ControlMigrationsHistoryTable = "__EFMigrationsHistory_Control";
#endif
}
