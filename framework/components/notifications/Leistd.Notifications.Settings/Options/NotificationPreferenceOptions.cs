namespace Leistd.Notifications.Settings.Options;

/// <summary>通知偏好：按 <c>{前缀}.{通知类型}.{渠道名}</c> 的用户级布尔设置决定是否投递。</summary>
public sealed class NotificationPreferenceOptions
{
    /// <summary>默认的设置名前缀。</summary>
    public const string DefaultSettingNamePrefix = "Notifications";

    /// <summary>设置名前缀，默认 <see cref="DefaultSettingNamePrefix"/>。</summary>
    public string SettingNamePrefix { get; set; } = DefaultSettingNamePrefix;

    /// <summary>必达组合：这些“类型 + 渠道”不受偏好影响，总是投递（如安全提醒的站内通知）。</summary>
    public IList<NotificationDelivery> MandatoryDeliveries { get; } = [];

    /// <summary>某类通知经某渠道的偏好设置名。</summary>
    /// <param name="type">通知类型。</param>
    /// <param name="channel">渠道名。</param>
    public string SettingNameOf(string type, string channel) => $"{SettingNamePrefix}.{type}.{channel}";
}

/// <summary>一种投递组合。</summary>
/// <param name="Type">通知类型。</param>
/// <param name="Channel">渠道名。</param>
public sealed record NotificationDelivery(string Type, string Channel);
