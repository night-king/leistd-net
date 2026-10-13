import { HttpHeaders } from '@angular/common/http';

import { NOTIFICATION_API } from './notification';
import { MockRequest } from '../core/models';

import type { NotificationOutputDto } from '../../src/app/shared/dtos/notification.dto';

/** 通知 Mock：最新的在前；含本地化时按请求语言给文案。 */
describe('notification mock', () => {
  function list(language?: string): NotificationOutputDto[] {
    const headers = language ? new HttpHeaders({ 'Accept-Language': language }) : new HttpHeaders();
    return NOTIFICATION_API['GET /api/v1/notifications']({ headers } as MockRequest);
  }

  it('lists the newest notification first', () => {
    const times = list().map((notification) => Date.parse(notification.creationTime));

    expect(times).toEqual([...times].sort((a, b) => b - a));
  });
  //#if (IncludeLocalization)

  it('answers in Chinese for a Chinese interface and in English otherwise', () => {
    const chinese = list('zh-CN')[0];
    const english = list('en')[0];

    expect(chinese.id).toBe(english.id);
    expect(chinese.title).toMatch(/[一-鿿]/);
    expect(english.title).not.toMatch(/[一-鿿]/);
    expect(list('fr')[0].title).toBe(english.title);
  });
  //#endif
});
