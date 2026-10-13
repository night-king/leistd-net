import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { NotificationService } from './notification-service';
import { SignalRService } from './signalr-service';
import { NotificationOutputDto } from '../../shared/dtos/notification.dto';

import type { Mock } from 'vitest';

/** 通知的连接闭环：init() 是 SignalR 连接的实际调用方，铃铛组件每次初始化都会走到这里。 */
describe('NotificationService', () => {
  let service: NotificationService;
  let signalR: SignalRService;
  let httpMock: HttpTestingController;

  function notification(id: string, creationTime: string): NotificationOutputDto {
    return { id, title: id, type: 'info', isRead: false, creationTime };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
                provideHttpClient(),
                provideHttpClientTesting(),
            ],
    });

    service = TestBed.inject(NotificationService);
    signalR = TestBed.inject(SignalRService);
    httpMock = TestBed.inject(HttpTestingController);

    vi.spyOn(console, 'error').mockReturnValue(undefined);
    vi.spyOn(signalR, 'connect').mockResolvedValue();
  });

  afterEach(() => httpMock.verify());

  it('does not open a second connection when initialized twice', async () => {
    const first = service.init();
    httpMock.expectOne((req) => req.url === '/api/v1/notifications').flush([]);
    await first;

    const second = service.init();
    httpMock.expectOne((req) => req.url === '/api/v1/notifications').flush([]);
    await second;

    // 幂等由 SignalRService.connect() 自身保证；这里锁住的是"调用方没有绕过它"，
    // 例如自己缓存一个 initialized 标志、或改成直接建连接。
    expect(signalR.connect).toHaveBeenCalledTimes(2);
    expect(signalR.connect).toHaveBeenCalledWith();
  });

  it('merges history with pushed notifications, newest first and without duplicates', async () => {
    // 先有一条实时推送进来，随后才拉到历史列表。
    signalR.notifications.set([notification('pushed', '2026-01-02T00:00:00Z')]);

    const init = service.init();
    httpMock
      .expectOne((req) => req.url === '/api/v1/notifications')
      .flush([
        notification('old', '2026-01-01T00:00:00Z'),
        notification('pushed', '2026-01-02T00:00:00Z'),
      ]);
    await init;

    // 推送那条已经在历史里，不能再插一遍——否则铃铛会显示两条一模一样的。
    expect(service.notifications().map((item) => item.id)).toEqual(['pushed', 'old']);
  });

  it('keeps a stale response out of the new auth subject list on mid-request switch', async () => {
    vi.spyOn(signalR, 'disconnect').mockResolvedValue();

    const init = service.init();
    const request = httpMock.expectOne((req) => req.url === '/api/v1/notifications');

    // 响应还没回来就登出/换人登录：reset 递增认证代际并清空列表。
    await signalR.reset();

    request.flush([notification('a-1', '2026-01-01T00:00:00Z')]);
    await init;

    // 旧响应无条件写回共享 signal，就是把上一个用户的历史通知落到下一个人界面上。
    expect(service.notifications()).toEqual([]);
  });

  it('skips the connection from a stale init after a mid-request subject switch', async () => {
    vi.spyOn(signalR, 'disconnect').mockResolvedValue();
    (signalR.connect as Mock).mockClear();

    const init = service.init();
    const request = httpMock.expectOne((req) => req.url === '/api/v1/notifications');

    await signalR.reset();
    request.flush([]);
    await init;

    // 服务端 Cookie 此刻可能仍有效：这一轮 init 建成的连接会把 principal 定在
    // 上一个人身上，下一个用户的 connect() 见到活连接就直接复用了它。
    expect(signalR.connect).not.toHaveBeenCalled();
  });

  it('keeps a stale write response off the new subject list on a mid-request switch', async () => {
    vi.spyOn(signalR, 'disconnect').mockResolvedValue();
    signalR.notifications.set([notification('b-1', '2026-02-01T00:00:00Z')]);

    const cleared = service.clearAll();
    const request = httpMock.expectOne((req) => req.url === '/api/v1/notifications');

    await signalR.reset();
    signalR.notifications.set([notification('b-1', '2026-02-01T00:00:00Z')]);

    request.flush(null);
    await cleared;

    // A 的 clearAll 在途、B 登录并加载完自己的列表，这一句会把 B 的列表清空。
    expect(service.notifications().map((item) => item.id)).toEqual(['b-1']);
  });

  it('keeps pushed notifications and resets loading when the load fails', async () => {
    signalR.notifications.set([notification('pushed', '2026-01-02T00:00:00Z')]);

    const init = service.init();
    httpMock
      .expectOne((req) => req.url === '/api/v1/notifications')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    await init;

    expect(service.loading()).toBe(false);
    expect(service.notifications().map((item) => item.id)).toEqual(['pushed']);
    // 失败要能被界面看到，否则空列表与"没有通知"无从区分。
    expect(service.loadFailed()).toBe(true);
  });

  it('clears the failure once a retry succeeds', async () => {
    const failed = service.loadNotifications();
    httpMock
      .expectOne((req) => req.url === '/api/v1/notifications')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    await failed;
    expect(service.loadFailed()).toBe(true);

    const retry = service.loadNotifications();
    // 重试进行中不再显示失败，界面应回到加载态。
    expect(service.loadFailed()).toBe(false);
    httpMock
      .expectOne((req) => req.url === '/api/v1/notifications')
      .flush([notification('n-1', '2026-01-01T00:00:00Z')]);
    await retry;

    expect(service.loadFailed()).toBe(false);
    expect(service.notifications().map((item) => item.id)).toEqual(['n-1']);
  });

  it('keeps a stale failure off the new subject on a mid-request switch', async () => {
    vi.spyOn(signalR, 'disconnect').mockResolvedValue();

    const load = service.loadNotifications();
    const request = httpMock.expectOne((req) => req.url === '/api/v1/notifications');

    await signalR.reset();
    request.flush('boom', { status: 500, statusText: 'Server Error' });
    await load;

    // 上一个用户的请求失败了，不能在下一个人的铃铛里显示"加载失败"。
    expect(service.loadFailed()).toBe(false);
  });

  it("keeps the new subject's loading state when the old subject's request settles late", async () => {
    vi.spyOn(signalR, 'disconnect').mockResolvedValue();

    const loadA = service.loadNotifications();
    await signalR.reset();
    const loadB = service.loadNotifications();
    const [requestA, requestB] = httpMock.match((req) => req.url === '/api/v1/notifications');

    // A 的请求在 B 加载途中结束：它的 finally 不能替 B 关掉加载状态。
    requestA.flush([]);
    await loadA;
    expect(service.loading()).toBe(true);

    requestB.flush([]);
    await loadB;
    expect(service.loading()).toBe(false);
  });
});
