import type { NotificationOutputDto } from '../../src/app/shared/dtos/notification.dto';

/** Mock 通知数据（内存）。 */
export const NOTIFICATIONS: NotificationOutputDto[] = [
  {
    id: '00000000-0000-0000-0000-000000000001',
    title: 'Welcome to the Leistd full-stack template notification center',
    content:
      'This is a sample system notification demonstrating how title, content, time, and the unread marker are laid out in the bell panel.',
    type: 'System',
    isRead: false,
    creationTime: new Date(Date.now() - 60_000).toISOString(),
  },
  {
    id: '00000000-0000-0000-0000-000000000002',
    title: 'Data updated: the customer order sync task has completed',
    content:
      'Data you follow has changed. This content is intentionally longer to verify that the tooltip width adapts to the viewport after truncation.',
    type: 'System',
    isRead: true,
    creationTime: new Date(Date.now() - 3_600_000).toISOString(),
  },
  {
    id: '00000000-0000-0000-0000-000000000003',
    title:
      'Reminder: a cross-department purchase request is awaiting your action before end of day',
    content:
      'The request contains multiple line items and budget notes. Please approve it before end of day today or reassign it to the responsible owner.',
    type: 'System',
    isRead: false,
    creationTime: new Date(Date.now() - 7_200_000).toISOString(),
  },
  //#if (LocalIdentity)
  {
    id: '00000000-0000-0000-0000-000000000004',
    title: 'Your password was changed',
    content: "If you didn't do this, contact your administrator right away.",
    type: 'Security',
    isRead: false,
    creationTime: new Date(Date.now() - 10_800_000).toISOString(),
  },
  //#endif
];
//#if (IncludeLocalization)

/**
 * 中文界面下的通知文案（按通知 Id）。真实后端里通知以发送时的文字入库；Mock 按请求语言给出对应文案，
 * 中文界面里不至于整列英文。
 */
export const NOTIFICATION_TEXTS_ZH_CN: Record<string, { title: string; content: string }> = {
  '00000000-0000-0000-0000-000000000001': {
    title: '欢迎使用 Leistd 全栈模板的通知中心',
    content: '这是一条系统通知示例，演示标题、正文、时间与未读标记在铃铛面板里的排布。',
  },
  '00000000-0000-0000-0000-000000000002': {
    title: '数据已更新：客户订单同步任务已完成',
    content: '你关注的数据发生了变化。这段正文有意写长，用来验证截断后的提示宽度能随视口自适应。',
  },
  '00000000-0000-0000-0000-000000000003': {
    title: '提醒：一条跨部门采购申请待你今天下班前处理',
    content: '申请包含多个明细与预算说明。请在今天下班前审批，或转给负责的同事处理。',
  },
  //#if (LocalIdentity)
  '00000000-0000-0000-0000-000000000004': {
    title: '你的密码已修改',
    content: '如果这不是你本人的操作，请立即联系管理员。',
  },
  //#endif
};
//#endif
