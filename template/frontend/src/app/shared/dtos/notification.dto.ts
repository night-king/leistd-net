/** 通知 DTO，对应后端 `Leistd.Notifications.Dtos.NotificationOutputDto`（类型为字符串）。 */
export interface NotificationOutputDto {
  id: string;
  title: string;
  content?: string;
  type: string;
  link?: string;
  icon?: string;
  isRead: boolean;
  creationTime: string;
  relatedEntityId?: string;
  relatedEntityType?: string;
}
