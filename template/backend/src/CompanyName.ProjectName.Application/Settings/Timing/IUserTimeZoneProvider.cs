namespace CompanyName.ProjectName.Application.Settings.Timing;

/// <summary>当前用户的展示时区。</summary>
/// <remarks>
/// 时间一律以 UTC 存储，只在展示时换算。需要在服务端产出给人看的时间文本时注入本服务——
/// 导出文件、报表、通知正文里的绝对时刻都属此类；纯粹返回给前端渲染的 DTO 不需要，
/// 那些保持 UTC，由前端按同一份设置换算。
/// </remarks>
public interface IUserTimeZoneProvider
{
    /// <summary>解析当前用户的展示时区。</summary>
    /// <remarks>
    /// 三层（用户 → 租户 → 代码默认值，即部署基线）都没有值，或取到的值已失效（例如时区数据库更名），
    /// 才回落到 UTC——这是本消费端对「最终缺值」的降级选择，不是设置层的语义；不用宿主所在的时区，
    /// 同一份数据不随部署机器变。展示时间不该因为一个偏好设置坏掉而整页报错。
    /// 浏览器在设置为空时用的是它自己的时区，服务端无从得知，需要与前端一致的场景由请求显式带上时区。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TimeZoneInfo> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>解析指定用户的展示时区，回落规则与 <see cref="GetAsync"/> 相同。</summary>
    /// <remarks>
    /// 给"收件人不是当前用户"的场合用，例如登录前触发的锁定提醒。读的是<b>当前租户环境</b>里这个用户的设置，
    /// 不会凭 userId 跨租户查找：调用方要先处于收件人所属的租户上下文。
    /// </remarks>
    /// <param name="userId">收件人的用户 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TimeZoneInfo> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>把 UTC 时刻换算到当前用户的展示时区。</summary>
    /// <param name="utc">UTC 时刻。<c>Kind</c> 不是 <see cref="DateTimeKind.Utc"/> 时按 UTC 解释。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>换算后的时刻，<c>Kind</c> 为 <see cref="DateTimeKind.Unspecified"/>——它已不是 UTC，也未必是本机本地时间。</returns>
    Task<DateTime> ToUserTimeAsync(DateTime utc, CancellationToken cancellationToken = default);

    /// <summary>按 IANA 时区名解析时区；不可用时返回 <see langword="false"/>。</summary>
    /// <remarks>
    /// 写入设置前用它挡掉无效值，请求里显式带的时区也用它解析。与 <see cref="GetAsync"/> 共用同一套判定，
    /// 不会出现「写得进去却解析不出来」。
    /// </remarks>
    /// <param name="timeZoneId">IANA 时区名，如 <c>Asia/Shanghai</c>。</param>
    /// <param name="timeZone">解析出的时区；不可用时为 UTC。</param>
    bool TryResolve(string? timeZoneId, out TimeZoneInfo timeZone);
}
