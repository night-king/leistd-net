import { MockRequest } from '../core/models';
//#if (IncludeLocalization)
import { NOTIFICATION_TEXTS_ZH_CN, NOTIFICATIONS } from '../data/notifications';
//#else
import { NOTIFICATIONS } from '../data/notifications';
//#endif

/** 通知中心 Mock API（对应框架的 NotificationEndpoints）。 */
export const NOTIFICATION_API = {
  //#if (IncludeLocalization)
  // 中文界面给中文文案，其余语言给英文；请求头不是受支持语言时用英文（与设置接口同一规则）
  'GET /api/v1/notifications': (req: MockRequest) => {
    const chinese = req.headers.get('Accept-Language') === 'zh-CN';
    return [...NOTIFICATIONS]
      .sort((a, b) => new Date(b.creationTime).getTime() - new Date(a.creationTime).getTime())
      .map((notification) =>
        chinese ? { ...notification, ...NOTIFICATION_TEXTS_ZH_CN[notification.id] } : notification,
      );
  },
  //#else
  // 不含本地化时只有英文文案，请求参数用不上
  'GET /api/v1/notifications': (_request: MockRequest) => {
    return [...NOTIFICATIONS].sort(
      (a, b) => new Date(b.creationTime).getTime() - new Date(a.creationTime).getTime(),
    );
  },
  //#endif

  'GET /api/v1/notifications/unread-count': () => {
    return NOTIFICATIONS.filter((n) => !n.isRead).length;
  },

  'PUT /api/v1/notifications/:id/read': (req: MockRequest) => {
    const item = NOTIFICATIONS.find((n) => n.id === req.params.id);
    if (item) item.isRead = true;
    return null;
  },

  'PUT /api/v1/notifications/read-all': () => {
    NOTIFICATIONS.forEach((n) => (n.isRead = true));
    return null;
  },

  'DELETE /api/v1/notifications': () => {
    // 持久清空：原地清空数组，后续 GET 返回空列表。
    NOTIFICATIONS.length = 0;
    return null;
  },

  'DELETE /api/v1/notifications/:id': (req: MockRequest) => {
    const index = NOTIFICATIONS.findIndex((n) => n.id === req.params.id);
    if (index !== -1) NOTIFICATIONS.splice(index, 1);
    return null;
  },
};
