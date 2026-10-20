//#if (IncludeNotifications)
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';

import { Notifications } from './notifications';
import { ApplicationHttpError } from '../../../core/errors/application-http-error';
import { ConfirmService } from '../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
import { LanguageService } from '../../../core/services/language-service';
//#endif
import { NotificationService } from '../../../core/services/notification-service';
import { NotificationOutputDto } from '../../../shared/dtos/notification.dto';

import type { Mock, MockedObject } from 'vitest';
//#if (IncludeLocalization)

const TRANSLATIONS = {
  en: {
    layout: {
      notifications: { title: 'Notifications', unreadAria: 'Notifications ({{count}} unread)' },
    },
  },
  'zh-CN': { layout: { notifications: { title: '通知', unreadAria: '通知（{{count}} 条未读）' } } },
};
//#endif

describe('Notifications', () => {
  let service: Pick<
    MockedObject<NotificationService>,
    'init' | 'markAsRead' | 'markAllAsRead' | 'clearAll' | 'clearOne' | 'getIcon'
  > &
    Pick<NotificationService, 'notifications' | 'unreadCount' | 'loading' | 'loadFailed'>;
  let confirmService: Pick<MockedObject<ConfirmService>, 'open'>;
  let errorToast: Mock;

  beforeEach(() => {
    service = {
      init: vi.fn().mockName('NotificationService.init'),
      markAsRead: vi.fn().mockName('NotificationService.markAsRead'),
      markAllAsRead: vi.fn().mockName('NotificationService.markAllAsRead'),
      clearAll: vi.fn().mockName('NotificationService.clearAll'),
      clearOne: vi.fn().mockName('NotificationService.clearOne'),
      getIcon: vi.fn().mockName('NotificationService.getIcon'),
      notifications: signal<NotificationOutputDto[]>([]),
      unreadCount: signal(0),
      loading: signal(false),
      loadFailed: signal(false),
    };
    service.init.mockResolvedValue();
    confirmService = {
      open: vi.fn().mockName('ConfirmService.open'),
    };
    confirmService.open.mockResolvedValue(true);
    errorToast = vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [Notifications],
      // prettier-ignore
      providers: [
                provideRouter([]),
                { provide: NotificationService, useValue: service },
                { provide: ConfirmService, useValue: confirmService },
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(['en', 'zh-CN']),
                // 面板里的时间要一个书写 locale，它取自活动语言（见 SettingContextService）。
                // 本组用例要的是"能在测试里换翻译"，不是验语言那条链路，
                // 所以给语言服务一个定值替身，免得它按设备语言去改活动语言。
                { provide: LanguageService, useValue: { activeLang: signal('en') } },
                //#endif
            ],
    });
  });

  function createComponent(): Notifications {
    const fixture = TestBed.createComponent(Notifications);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  function httpError(status: number): ApplicationHttpError {
    return ApplicationHttpError.from(
      new HttpErrorResponse({
        status,
        statusText: 'Server Error',
        error: { detail: 'Delete failed.' },
      }),
    );
  }

  it('keeps the panel open and surfaces an error when clearing all fails', async () => {
    service.clearAll.mockRejectedValue(httpError(500));
    const component = createComponent();
    component.notificationOpen.set('open');

    await component.clearAllNotifications();

    expect(errorToast).toHaveBeenCalled();
    expect(component.notificationOpen(), 'a failed delete must not look successful').toBe('open');
  });

  it('closes the panel only after clearing all succeeds', async () => {
    service.clearAll.mockResolvedValue();
    const component = createComponent();
    component.notificationOpen.set('open');

    await component.clearAllNotifications();

    expect(errorToast).not.toHaveBeenCalled();
    expect(component.notificationOpen()).toBe('closed');
  });
  //#if (IncludeLocalization)

  it('re-renders ARIA labels when the language changes at runtime', async () => {
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation(TRANSLATIONS.en, 'en');
    transloco.setTranslation(TRANSLATIONS['zh-CN'], 'zh-CN');
    (
      service.unreadCount as unknown as {
        set(v: number): void;
      }
    ).set(2);
    const fixture = TestBed.createComponent(Notifications);
    fixture.detectChanges();
    await fixture.whenStable();
    const trigger = (fixture.nativeElement as HTMLElement).querySelector('[hlmPopoverTrigger]')!;

    expect(trigger.getAttribute('aria-label')).toBe('Notifications (2 unread)');

    transloco.setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(trigger.getAttribute('aria-label'), 'trigger name must follow the language').toBe(
      '通知（2 条未读）',
    );
  });
  //#endif

  it('still navigates when marking as read fails', async () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    service.markAsRead.mockRejectedValue(httpError(500));
    const component = createComponent();
    component.notificationOpen.set('open');

    await component.onNotificationClick({
      id: 'n-1',
      title: 't',
      content: 'c',
      type: 'info',
      isRead: false,
      link: '/platform/users',
      creationTime: new Date().toISOString(),
    } as NotificationOutputDto);

    expect(errorToast, 'failure must still be surfaced').toHaveBeenCalled();
    expect(
      navigate,
      'navigation is the primary action and must not be blocked',
    ).toHaveBeenCalledWith('/platform/users');
    expect(component.notificationOpen()).toBe('closed');
  });

  // 行的排版与截断由 NotificationItem 负责（见其 spec）；这里只验面板接线：展开只作用于那一条，不触发跳转
  it('expands one notification from its summary without opening it', async () => {
    const long = 'Three entries could not be matched against the bank statement. '.repeat(6);
    service.notifications.set([
      {
        id: 'n-long',
        title: 'Reconciliation finished',
        content: long,
        link: '/platform/users',
        type: 'System',
        isRead: true,
        creationTime: new Date().toISOString(),
      } as NotificationOutputDto,
    ]);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(Notifications);
    fixture.detectChanges();
    fixture.componentInstance.notificationOpen.set('open');
    fixture.detectChanges();
    await fixture.whenStable();
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());

    const expand = () =>
      document.querySelector<HTMLButtonElement>(
        '.cdk-overlay-container [data-testid="notification-expand"]',
      );
    await expect.poll(expand).not.toBeNull();
    expand()!.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.expanded().has('n-long')).toBe(true);
    expect(navigate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.notificationOpen()).toBe('open');
  });

  // 图标由使用方自己整组注册（测试不在父级预先提供）：漏注册不会报错，面板里只剩一块空白
  it('registers the type icons it shows', async () => {
    service.getIcon.mockImplementation((type: string) =>
      NotificationService.prototype.getIcon.call(service, type),
    );
    service.notifications.set([
      {
        id: 'n-1',
        title: 'Sign-in from a new device',
        //#if (LocalIdentity)
        type: 'Security',
        //#else
        type: 'System',
        //#endif
        isRead: false,
        creationTime: new Date().toISOString(),
      } as NotificationOutputDto,
    ]);
    const fixture = TestBed.createComponent(Notifications);
    fixture.detectChanges();
    fixture.componentInstance.notificationOpen.set('open');
    fixture.detectChanges();
    await fixture.whenStable();
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());

    const item = () => document.querySelector('.cdk-overlay-container app-notification-item');
    await expect.poll(item).not.toBeNull();
    const svg = item()!.querySelector('ng-icon svg');
    expect(svg, 'the icon must be registered by the panel itself').not.toBeNull();
    expect(svg!.getBoundingClientRect().width).toBeGreaterThan(0);
  });

  it('surfaces an error when dismissing a single notification fails', async () => {
    service.clearOne.mockRejectedValue(httpError(503));
    const component = createComponent();

    await component.clearOneNotification('n-1');

    expect(errorToast).toHaveBeenCalled();
  });
});
//#endif
