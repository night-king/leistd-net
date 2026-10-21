namespace Leistd.RealTime.AspNetCore.SignalR;

// 资源组名的唯一生成处，订阅侧与发布侧必须逐字一致，否则推送静默无人接收。
// 组名不依赖环境租户，后台发布与订阅寻址一致；租户隔离由 IRealTimeSubscriptionAuthorizer 判定。
internal static class RealTimeGroups
{
    internal static string Resource(string resourceKey) => $"resource:{resourceKey}";
}
