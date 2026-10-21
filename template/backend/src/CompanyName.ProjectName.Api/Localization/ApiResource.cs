namespace CompanyName.ProjectName.Api.Localization;

/// <summary>
/// DataAnnotations 校验消息本地化的标记类型：作为 <c>DataAnnotationLocalizerProvider</c> 的 resource 类型传入。
/// 须在 <c>AddJsonLocalization</c> 时登记到 <c>JsonLocalizationOptions.JsonResourceTypes</c>，
/// 组合工厂据此把本类型精确路由到 JSON 词条表（未登记则委派官方 RESX，取不到 JSON 校验文案）。
/// JSON 工厂对已登记类型返回共享视图（读取全部已登记资源程序集），真正的校验文案键存放在本项目
/// <c>Resources/{en,zh-CN}.json</c>。
/// </summary>
public sealed class ApiResource;
