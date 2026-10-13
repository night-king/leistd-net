//#if (IncludeRealTime)
import { DestroyRef, EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
//#endif
import { TestBed } from '@angular/core/testing';
import * as signalR from '@microsoft/signalr';

import { SignalRService } from './signalr-service';
import { environment } from '../../../environments/environment';

import type { Mock } from 'vitest';

/**
 * 连接生命周期：通知与业务事件共用一条连接，主体切换、失败与重连时不泄漏、不串人。
 *
 * 这里只替换 HubConnectionBuilder，不去 mock 网络：要锁住的是本服务对"部分失败"
 * 与"重复调用"的处理，而不是 SignalR 客户端自身的行为。
 */
describe('SignalRService connection lifecycle', () => {
  let service: SignalRService;
  let built: FakeConnection[];
  let failing: Set<string>;
  let deferStart = false;
  let withUrl: Mock;

  class FakeConnection {
    startCount = 0;
    stopCount = 0;
    state: signalR.HubConnectionState = signalR.HubConnectionState.Disconnected;
    readonly handlers = new Map<string, (...args: unknown[]) => void>();

    // 保存自动重连回调，测试据此模拟"掉线—恢复"时序。
    private reconnecting: (() => void) | null = null;
    private reconnected: (() => void | Promise<void>) | null = null;

    constructor(readonly url: string) {}

    on(name: string, handler: (...args: unknown[]) => void): void {
      this.handlers.set(name, handler);
    }

    onreconnecting(handler: () => void): void {
      this.reconnecting = handler;
    }

    onreconnected(handler: () => void | Promise<void>): void {
      this.reconnected = handler;
    }

    // 本用例集不模拟"连接彻底关闭"，注册即可。
    readonly onclose = (): void => undefined;

    /** 只触发 onreconnected，并把它的 Promise 交回给测试。 */
    triggerReconnected(): Promise<void> {
      this.state = signalR.HubConnectionState.Connected;
      return Promise.resolve(this.reconnected?.()).then(() => undefined);
    }

    /** 模拟自动重连：掉线 → 恢复。 */
    dropAndRecover(): void {
      this.state = signalR.HubConnectionState.Reconnecting;
      this.reconnecting?.();
      this.state = signalR.HubConnectionState.Connected;
      this.reconnected?.();
    }

    /** 模拟掉线但尚未恢复。 */
    drop(): void {
      this.state = signalR.HubConnectionState.Reconnecting;
      this.reconnecting?.();
    }

    /** 置为 true 时 start() 挂起，直到 completeStart() 被调用。 */
    private startGate: (() => void) | null = null;

    async start(): Promise<void> {
      this.startCount++;

      // 立即完成的 start 构造不出"连接已写入字段、但尚未通过最终代际校验"的窗口，
      // 而那正是旧请求的推送可以穿透的地方。
      if (deferStart) {
        await new Promise<void>((resolve) => {
          this.startGate = resolve;
        });
      }

      if ([...failing].some((fragment) => this.url.includes(fragment))) {
        throw new Error(`start failed: ${this.url}`);
      }
      this.state = signalR.HubConnectionState.Connected;
    }

    completeStart(): void {
      this.startGate?.();
      this.startGate = null;
    }

    async stop(): Promise<void> {
      this.stopCount++;
      this.state = signalR.HubConnectionState.Disconnected;
    }

    // 订阅走 invoke：缺了它，对账步骤会直接抛错进 catch，
    // 相关用例在改坏实现时也照样绿——那种"通过"什么都证明不了。
    readonly invocations: {
      method: string;
      args: unknown[];
    }[] = [];

    /** 置为 true 时 invoke() 挂起，直到 releaseInvoke() 被调用。 */
    gateInvoke = false;
    private invokeGates: { resolve: () => void; reject: (error: Error) => void }[] = [];

    /** 服务端拒绝订阅的资源键（例如无权限）。 */
    readonly rejectedSubscriptions = new Set<string>();

    invoke(method: string, ...args: unknown[]): Promise<void> {
      this.invocations.push({ method, args });

      if (method === 'Subscribe' && this.rejectedSubscriptions.has(args[0] as string)) {
        return Promise.reject(new Error(`subscription rejected: ${String(args[0])}`));
      }

      // 立即完成的 invoke 让重订阅循环在一个微任务里跑完，
      // 制造不出"循环卡在某一轮时主体切换"的竞态。
      if (!this.gateInvoke) {
        return Promise.resolve();
      }

      return new Promise<void>((resolve, reject) => this.invokeGates.push({ resolve, reject }));
    }

    releaseInvoke(): void {
      const gates = this.invokeGates;
      this.invokeGates = [];
      gates.forEach((gate) => gate.resolve());
    }

    /** 让挂起中的 invoke() 全部失败（模拟断线时 SDK 拒绝在途调用）。 */
    failInvoke(): void {
      const gates = this.invokeGates;
      this.invokeGates = [];
      gates.forEach((gate) => gate.reject(new Error('invocation canceled')));
    }
  }

  afterEach(() => {
    // 兜底：正常路径由各用例的 finally 释放。afterEach 只在测试体提前抛出时生效——
    // 断言失败后若还有 await 卡在未释放的 gate 上，失败会退化成用例超时。
    built.forEach((connection) => {
      connection.completeStart();
      connection.releaseInvoke();
    });
  });

  beforeEach(() => {
    built = [];
    environment.useMock = false;
    failing = new Set<string>();
    deferStart = false;

    vi.spyOn(console, 'error').mockReturnValue(undefined);

    // 只替换 build，链式调用仍由真实 builder 完成；spy 类导出会接管构造，变成在验证 SignalR 客户端。
    withUrl = vi.spyOn(signalR.HubConnectionBuilder.prototype, 'withUrl');
    vi.spyOn(signalR.HubConnectionBuilder.prototype, 'build').mockImplementation(() => {
      const url = vi.mocked(withUrl).mock.lastCall?.[0];
      if (!url) {
        throw new Error('withUrl must be called before build');
      }
      const connection = new FakeConnection(url);
      built.push(connection);

      return connection as unknown as signalR.HubConnection;
    });

    service = TestBed.inject(SignalRService);
  });
  //#if (IncludeRealTime)

  /** 一个可控的持有者：销毁它，等于持有订阅的页面被销毁。 */
  function holder(): { ref: DestroyRef; destroy: () => void } {
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    return { ref: injector.get(DestroyRef), destroy: () => injector.destroy() };
  }

  /** 让对账链上已就绪的步骤全部跑完（链由微任务串起，一个宏任务足以排空）。 */
  function settle(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
  }

  /** 某个连接上针对某个键的 Hub 调用序列。 */
  function calls(connection: FakeConnection, resourceKey: string): string[] {
    return connection.invocations
      .filter((call) => call.args[0] === resourceKey)
      .map((call) => call.method);
  }
  //#endif

  it('stays idle until a connection is requested', () => {
    expect(service.connectionStatus()).toBe('idle');
  });

  it('enters the connected state after connecting to the configured hub', async () => {
    await service.connect();

    expect(built.length).toBe(1);
    expect(built[0].url).toContain(SignalRService.hubPath);
    expect(service.isConnected()).toBe(true);
  });

  it('keeps the real connection when mock configuration is enabled in a deployment build', async () => {
    environment.useMock = true;
    await service.connect();
    expect(built).toHaveLength(1);
    expect(service.isConnected()).toBe(true);
  });

  it('leaves no live connection when connecting fails', async () => {
    failing.add(SignalRService.hubPath);

    await service.connect();

    expect(service.isConnected()).toBe(false);
    expect(service.connectionStatus()).toBe('disconnected');
    expect(built[0].stopCount).toBe(1);
  });

  it('concurrent calls share one connection attempt instead of each creating a connection', async () => {
    await Promise.all([service.connect(), service.connect(), service.connect()]);

    expect(built.length).toBe(1);
  });

  it('returns immediately when already connected, without rebuilding or leaking', async () => {
    await service.connect();
    await service.connect();

    // 只去重"进行中"的调用是不够的：串行第二次会新建一条并覆盖字段引用，
    // 旧连接连同 handler 继续往同一个 signal 里推。通知组件重挂载就会走到这里。
    expect(built.length).toBe(1);
    expect(built[0].stopCount).toBe(0);
    expect(service.isConnected()).toBe(true);
  });

  it('rebuilds when the held connection is fully disconnected', async () => {
    await service.connect();
    built[0].state = signalR.HubConnectionState.Disconnected;

    await service.connect();

    // 自动重连耗尽后一味早退，会把应用永久留在断线状态。
    expect(built.length).toBe(2);
    expect(service.isConnected()).toBe(true);
  });

  it('can retry after a failure without stacking on the previous connection', async () => {
    failing.add(SignalRService.hubPath);
    await service.connect();
    expect(service.isConnected()).toBe(false);

    failing.clear();
    await service.connect();

    expect(service.isConnected()).toBe(true);
    expect(built.filter((connection) => connection.stopCount === 0).length).toBe(1);
    expect(built.every((connection) => connection.startCount === 1)).toBe(true);
  });

  it('does not report connected while dropped and reports it again after recovery', async () => {
    await service.connect();

    built[0].drop();
    expect(service.isConnected()).toBe(false);
    expect(service.connectionStatus()).toBe('reconnecting');

    built[0].dropAndRecover();
    expect(service.isConnected()).toBe(true);
  });

  it('returns to idle after a deliberate reset', async () => {
    await service.connect();

    await service.reset();

    // 登出是主动断开，不应显示成掉线
    expect(service.connectionStatus()).toBe('idle');
  });
  //#if (IncludeNotifications && IncludeRealTime)

  it('notifications and resource events on one connection each trigger only their own handler', async () => {
    service.registerResourceEvent('OrderChanged');
    await service.connect();
    const connection = built[0];

    connection.handlers.get(SignalRService.notificationReceived)!({
      id: 'n1',
      title: '通知',
      type: 'info',
      isRead: false,
      creationTime: '2026-01-01',
    });
    expect(service.notifications().length).toBe(1);
    expect(service.lastResourceEvent()).toBeNull();

    connection.handlers.get('OrderChanged')!({ id: 'order-1' });
    expect(service.notifications().length).toBe(1);
    expect(service.lastResourceEvent()).toEqual({
      eventName: 'OrderChanged',
      payload: { id: 'order-1' },
    });
  });
  //#endif
  //#if (IncludeRealTime)

  it('resubscribes held resources on the same connection after reconnecting', async () => {
    service.watchResource('order-1', holder().ref);
    await service.connect();
    await settle();
    const connection = built[0];
    connection.invocations.length = 0;

    await connection.triggerReconnected();
    await settle();

    expect(connection.invocations).toEqual([{ method: 'Subscribe', args: ['order-1'] }]);
  });
  //#endif

  it('can connect again after disconnecting', async () => {
    await service.connect();
    await service.disconnect();

    expect(service.isConnected()).toBe(false);
    expect(built.every((connection) => connection.stopCount === 1)).toBe(true);

    await service.connect();

    expect(service.isConnected()).toBe(true);
    expect(built.length).toBe(2);
  });

  it('does not reuse the connection or state of the previous principal after a switch', async () => {
    await service.connect();
    //#if (IncludeNotifications)
    service.notifications.set([
      { id: 'n1', title: 'A 的通知', type: 'info', isRead: false, creationTime: '2026-01-01' },
    ]);
    //#endif
    //#if (IncludeRealTime)
    service.watchResource('order-1', holder().ref);
    await settle();
    //#endif

    await service.reset();

    // SignalR 的 principal 在握手时定死：不断开就换人登录，下一个用户会复用
    // 上一个人的活连接，以对方的身份继续收消息。
    expect(built.every((connection) => connection.stopCount === 1)).toBe(true);
    //#if (IncludeNotifications)
    expect(service.notifications()).toEqual([]);
    //#endif
    //#if (IncludeRealTime)
    expect(service.lastResourceEvent()).toBeNull();
    //#endif
    expect(service.isConnected()).toBe(false);

    await service.connect();

    // 新主体拿到的是新建的连接，不是上一个人的。
    expect(built.length).toBe(2);
    expect(built[1].stopCount).toBe(0);
  });

  it('does not hand an in-progress connection to the next principal after a switch', async () => {
    const connecting = service.connect();
    await service.reset();
    await connecting;

    // await 回来的连接握的是上一个身份；写进字段就成了没人再管、却仍在收推送的孤儿。
    expect(service.isConnected()).toBe(false);
    expect(built.every((connection) => connection.stopCount >= 1)).toBe(true);
  });
  //#if (IncludeNotifications)

  it('does not write stale hub pushes arriving during reset into the list of the new principal', async () => {
    await service.connect();
    const push = built[0].handlers.get(SignalRService.notificationReceived)!;

    await service.reset();

    // stop() 是异步的，在它完成之前仍可能收到上一个主体的推送。
    push({ id: 'n1', title: 'A 的推送', type: 'info', isRead: false, creationTime: '2026-01-01' });
    expect(service.notifications()).toEqual([]);
  });
  //#endif
  //#if (IncludeRealTime)

  it('does not resubscribe a subscribe call completing after reset on the connection of the next principal', async () => {
    await service.connect();
    const stale = built[0];
    stale.gateInvoke = true;
    service.watchResource('order-1', holder().ref);
    await settle();

    await service.reset();
    stale.gateInvoke = false;
    stale.releaseInvoke();

    await service.connect();
    const business = built[1];
    business.dropAndRecover();
    await settle();

    // 断言真实后果而不是内部集合：回填的 key 一旦留下，重连时会以下一个用户的身份
    // 重新订阅；同作用域、同权限的 key 照样通过后端订阅授权。
    expect(business.invocations).toEqual([]);
  });
  //#endif

  it('connects for a new principal even while the attempt of the old one is unfinished', async () => {
    const stale = service.connect();
    await service.reset();

    const fresh = service.connect();
    await Promise.all([stale, fresh]);

    // 直接复用上一个主体的 Promise，会让本主体拿到"正常返回但什么都没连上"，
    // 在组件重挂载前一直没有实时通知。
    expect(service.isConnected()).toBe(true);
  });

  it('does not connect when a stale connect request resumes after reset', async () => {
    deferStart = true;

    // 旧请求在 await 处让出执行权，恢复时已经不是当前主体。
    const stale = service.connect();
    try {
      await service.reset();
      await Promise.resolve();
      await Promise.resolve();

      // 一条都不该建：连接一旦写进字段，身份比对就一律为真，
      // start() 期间收到的推送会直接落进下一个主体的界面。
      expect(built.length).toBe(0);
    } finally {
      // 回归时这里会有连接卡在未完成的 start 上，不释放就等成超时。
      built.forEach((connection) => connection.completeStart());
    }

    await stale;
    expect(service.isConnected()).toBe(false);
  });

  it('ignores pushes and events from the old connection when the principal switches before start completes', async () => {
    deferStart = true;

    // 这一轮 connect 发起时仍是当前主体，因此连接会被建出来并写进字段；
    // 切换发生在 start() 完成之前——身份判据正是为这个窗口存在的。
    const pending = service.connect();
    try {
      await Promise.resolve();
      await Promise.resolve();

      const connection = built[0];
      expect(
        connection,
        'The connection should have been built and stored in the field',
      ).toBeDefined();

      //#if (IncludeRealTime)
      service.registerResourceEvent('OrderChanged');
      //#endif
      await service.reset();

      //#if (IncludeNotifications)
      connection.handlers.get(SignalRService.notificationReceived)?.({
        id: 'a-1',
        title: 'A 的推送',
        type: 'info',
        isRead: false,
        creationTime: '2026-01-01',
      });
      //#endif
      //#if (IncludeRealTime)
      connection.handlers.get('OrderChanged')?.({ id: 'order-1' });
      //#endif

      //#if (IncludeNotifications)
      expect(service.notifications()).toEqual([]);
      //#endif
      //#if (IncludeRealTime)
      expect(service.lastResourceEvent()).toBeNull();
      //#endif
    } finally {
      built.forEach((connection) => connection.completeStart());
    }

    await pending;
    expect(service.isConnected()).toBe(false);
  });
  //#if (IncludeRealTime)

  it('does not subscribe resources of the next principal when switching while reconnect resubscription is stuck', async () => {
    service.watchResource('a-order', holder().ref);
    await service.connect();
    await settle();

    const staleBusiness = built[0];
    staleBusiness.invocations.length = 0;
    staleBusiness.gateInvoke = true;

    // 重连回调进入循环并卡在 A 的第一次 Subscribe 上。
    const reconnected = staleBusiness.triggerReconnected();
    try {
      await settle();
      expect(
        staleBusiness.invocations.length,
        'Reconnect recovery should already have sent the first Subscribe',
      ).toBe(1);

      // 就在这一轮未完成时切换主体，并让新主体订阅自己的资源。
      await service.reset();
      await service.connect();
      service.watchResource('b-order', holder().ref);
      await settle();
    } finally {
      // 先关掉拦截再释放：缺陷重新引入时会对 b-order 再发 invoke，只释放已排队的那次
      // 会让用例以超时失败、看不出原因。
      staleBusiness.gateInvoke = false;
      staleBusiness.releaseInvoke();
    }

    await reconnected;
    await settle();

    // 跨 await 迭代活集合时，旧回调的迭代器会读到新主体刚加入的 key，
    // 并在上一个人的连接上把它订阅一遍。
    expect(staleBusiness.invocations.map((call) => call.args[0])).not.toContain('b-order');
  });
  //#endif

  // stop 抛错也要清空引用，否则下一次连接会把泄漏的连接留在后面。
  it('clears the reference even when stop throws', async () => {
    await service.connect();
    built.forEach((connection) => {
      vi.spyOn(connection, 'stop').mockRejectedValue(new Error('stop failed'));
    });

    await service.disconnect();
    await service.connect();

    // 引用已清空，新一轮正常建立一条。
    expect(built.length).toBe(2);
    expect(service.isConnected()).toBe(true);
  });
  //#if (IncludeRealTime)

  /** 资源订阅的持有与对账：需求与现状分开记，每个键一条链按差值补齐；下列用例各对应一种交错时序。 */
  describe('resource subscription lifecycle', () => {
    it('does not subscribe for a page destroyed before the connection is up', async () => {
      const page = holder();
      service.watchResource('roles', page.ref);
      page.destroy();

      await service.connect();
      await settle();
      await built[0].triggerReconnected();
      await settle();

      expect(calls(built[0], 'roles')).toEqual([]);
    });

    it('unsubscribes once an in-flight subscribe completes after the page is destroyed', async () => {
      await service.connect();
      const connection = built[0];
      connection.gateInvoke = true;
      const page = holder();
      service.watchResource('roles', page.ref);
      await settle();

      page.destroy();
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe', 'Unsubscribe']);

      connection.invocations.length = 0;
      await connection.triggerReconnected();
      await settle();
      expect(
        calls(connection, 'roles'),
        'A key nobody holds must not be revived on reconnect',
      ).toEqual([]);
    });

    it('stays subscribed when a page is left and immediately re-entered', async () => {
      await service.connect();
      const connection = built[0];
      const first = holder();
      service.watchResource('roles', first.ref);
      await settle();

      first.destroy();
      service.watchResource('roles', holder().ref);
      await settle();
      expect(calls(connection, 'roles').at(-1)).toBe('Subscribe');

      connection.invocations.length = 0;
      await connection.triggerReconnected();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe']);
    });

    it('resubscribes when a page re-enters while its unsubscribe is in flight', async () => {
      await service.connect();
      const connection = built[0];
      const first = holder();
      service.watchResource('roles', first.ref);
      await settle();

      connection.gateInvoke = true;
      first.destroy();
      await settle();
      service.watchResource('roles', holder().ref);
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe', 'Unsubscribe', 'Subscribe']);

      connection.invocations.length = 0;
      await connection.triggerReconnected();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe']);
    });

    it('keeps a stale page of the previous principal from touching the same key after reset', async () => {
      await service.connect();
      const stale = built[0];
      stale.gateInvoke = true;
      const oldPage = holder();
      service.watchResource('roles', oldPage.ref);
      await settle();

      await service.reset();
      service.watchResource('roles', holder().ref);
      await service.connect();
      const current = built[1];
      stale.gateInvoke = false;
      stale.releaseInvoke();
      oldPage.destroy();
      await settle();

      // 旧页面晚到的销毁既不能撤掉新主体的持有，也不能把 Unsubscribe 打到新连接上
      expect(calls(current, 'roles')).toEqual(['Subscribe']);
      current.invocations.length = 0;
      await current.triggerReconnected();
      await settle();
      expect(calls(current, 'roles')).toEqual(['Subscribe']);
    });

    it('keeps a shared key subscribed until its last holder is destroyed', async () => {
      await service.connect();
      const connection = built[0];
      const first = holder();
      const second = holder();
      service.watchResource('roles', first.ref);
      service.watchResource('roles', second.ref);
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe']);

      first.destroy();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe']);

      second.destroy();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe', 'Unsubscribe']);
    });

    it('does not restore a key released while reconnect recovery is in flight', async () => {
      const keep = holder();
      const release = holder();
      service.watchResource('a', keep.ref);
      service.watchResource('b', release.ref);
      await service.connect();
      await settle();
      const connection = built[0];
      connection.invocations.length = 0;

      connection.gateInvoke = true;
      void connection.triggerReconnected();
      await settle();
      release.destroy();
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();

      expect(calls(connection, 'a')).toEqual(['Subscribe']);
      expect(
        calls(connection, 'b').at(-1),
        'A key released during recovery must eventually be unsubscribed',
      ).toBe('Unsubscribe');

      connection.invocations.length = 0;
      await connection.triggerReconnected();
      await settle();
      expect(calls(connection, 'b')).toEqual([]);
    });

    it('keeps a key held again while reconnect recovery is in flight', async () => {
      const first = holder();
      service.watchResource('b', first.ref);
      await service.connect();
      await settle();
      const connection = built[0];
      connection.invocations.length = 0;

      connection.gateInvoke = true;
      void connection.triggerReconnected();
      await settle();
      first.destroy();
      service.watchResource('b', holder().ref);
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();

      expect(calls(connection, 'b').at(-1)).toBe('Subscribe');
    });

    it('recovers a rejected subscribe on the next reconnect', async () => {
      await service.connect();
      const connection = built[0];
      connection.rejectedSubscriptions.add('roles');
      service.watchResource('roles', holder().ref);
      await settle();
      expect(console.error).toHaveBeenCalled();

      connection.rejectedSubscriptions.delete('roles');
      connection.invocations.length = 0;
      await connection.triggerReconnected();
      await settle();
      expect(calls(connection, 'roles')).toEqual(['Subscribe']);
    });

    it('keeps reconciling a key after its in-flight unsubscribe fails', async () => {
      await service.connect();
      const connection = built[0];
      const first = holder();
      service.watchResource('roles', first.ref);
      await settle();

      // 退订在途时，同一个键又排进了新的持有与释放；随后那次退订失败
      connection.gateInvoke = true;
      first.destroy();
      await settle();
      const second = holder();
      service.watchResource('roles', second.ref);
      second.destroy();
      connection.gateInvoke = false;
      connection.failInvoke();
      await settle();

      // 链没有被那次失败卡死：排在后面的步骤照常执行，按"仍订阅着"再退订一次
      expect(calls(connection, 'roles')).toEqual(['Subscribe', 'Unsubscribe', 'Unsubscribe']);
    });

    it('does not let a stale queue cleanup drop the queue of the next principal for the same key', async () => {
      await service.connect();
      const stale = built[0];
      stale.gateInvoke = true;
      service.watchResource('roles', holder().ref);
      await settle();

      await service.reset();
      await service.connect();
      const current = built[1];
      current.gateInvoke = true;
      const page = holder();
      service.watchResource('roles', page.ref);
      await settle();

      // 旧链在新链入队之后才收尾；它若按键删除队列，新链上随后的释放会与在途的 Subscribe 并行，
      // 读到"尚未订阅"而不发 Unsubscribe，订阅就此残留
      stale.gateInvoke = false;
      stale.releaseInvoke();
      await settle();
      page.destroy();
      current.gateInvoke = false;
      current.releaseInvoke();
      await settle();

      expect(calls(current, 'roles')).toEqual(['Subscribe', 'Unsubscribe']);
    });

    it('records demand without touching the hub while there is no connection', async () => {
      const page = holder();
      service.watchResource('roles', page.ref);
      page.destroy();
      await settle();

      expect(built).toEqual([]);
      expect(console.error).not.toHaveBeenCalled();
    });

    it('ignores a destroy ref that is already destroyed', async () => {
      const page = holder();
      page.destroy();
      service.watchResource('roles', page.ref);

      await service.connect();
      await settle();
      expect(calls(built[0], 'roles')).toEqual([]);
    });

    it('takes the destroy ref of the injection context when none is given', async () => {
      const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
      injector.runInContext(() => service.watchResource('roles'));
      await service.connect();
      await settle();
      expect(calls(built[0], 'roles')).toEqual(['Subscribe']);

      injector.destroy();
      await settle();
      expect(calls(built[0], 'roles')).toEqual(['Subscribe', 'Unsubscribe']);
      expect(
        () => service.watchResource('roles'),
        'Calling outside an injection context without a DestroyRef must throw',
      ).toThrow();
    });
  });

  describe('confirmed resource subscriptions', () => {
    let confirmed: string[];

    beforeEach(() => {
      confirmed = [];
      service.resourceSubscribed$.subscribe((key) => confirmed.push(key));
    });

    it('reports a subscription only after the server acknowledges it', async () => {
      await service.connect();
      const connection = built[0];
      connection.gateInvoke = true;
      service.watchResource('order-1', holder().ref);
      await settle();
      // 确认前就报告，页面会在加入订阅组之前查询，之间的变更就漏了
      expect(confirmed).toEqual([]);

      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();
      expect(confirmed).toEqual(['order-1']);
    });

    it('reports the subscription again after every automatic reconnect', async () => {
      service.watchResource('order-1', holder().ref);
      await service.connect();
      await settle();
      const connection = built[0];

      await connection.triggerReconnected();
      await settle();
      await connection.triggerReconnected();
      await settle();

      expect(confirmed).toEqual(['order-1', 'order-1', 'order-1']);
    });

    it('restores and reports subscriptions on a connection rebuilt after reconnecting gave up', async () => {
      service.watchResource('order-1', holder().ref);
      await service.connect();
      await settle();
      built[0].state = signalR.HubConnectionState.Disconnected;

      await service.connect();
      await settle();

      expect(built.length).toBe(2);
      expect(built[1].invocations).toEqual([{ method: 'Subscribe', args: ['order-1'] }]);
      expect(confirmed).toEqual(['order-1', 'order-1']);
    });

    it('does not report a resource whose resubscription is rejected', async () => {
      service.watchResource('order-1', holder().ref);
      service.watchResource('order-2', holder().ref);
      await service.connect();
      await settle();
      const connection = built[0];
      connection.rejectedSubscriptions.add('order-1');
      confirmed.length = 0;

      await connection.triggerReconnected();
      await settle();

      expect(confirmed).toEqual(['order-2']);
    });

    it('does not report a resource released while its resubscription was pending', async () => {
      const page = holder();
      service.watchResource('order-1', page.ref);
      await service.connect();
      await settle();
      const connection = built[0];
      confirmed.length = 0;
      connection.gateInvoke = true;

      void connection.triggerReconnected();
      await settle();
      // 页面在重订阅确认前离开
      page.destroy();
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();

      expect(confirmed).toEqual([]);
    });

    it('does not report a first subscription cancelled before its acknowledgement', async () => {
      await service.connect();
      const connection = built[0];
      connection.gateInvoke = true;
      const page = holder();
      service.watchResource('order-1', page.ref);
      await settle();

      // 页面在首次订阅确认前离开
      page.destroy();
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();
      expect(confirmed).toEqual([]);
    });

    it('reports only the key still held when another is released during recovery', async () => {
      const kept = holder();
      const released = holder();
      service.watchResource('order-1', kept.ref);
      service.watchResource('order-2', released.ref);
      await service.connect();
      await settle();
      const connection = built[0];
      confirmed.length = 0;
      connection.gateInvoke = true;

      void connection.triggerReconnected();
      await settle();
      // order-2 的页面在恢复确认前离开
      released.destroy();
      connection.gateInvoke = false;
      connection.releaseInvoke();
      await settle();

      expect(confirmed).toEqual(['order-1']);
      expect(calls(connection, 'order-2').at(-1)).toBe('Unsubscribe');
    });

    // SDK 收到确认后先完成 Promise，await 之后的续行在微任务里才跑：这段间隙里切换主体、
    // 新主体又登记了同一个键，旧连接的确认不能冒充新主体的
    it('does not report a completed acknowledgement of the previous connection for the same key', async () => {
      await service.connect();
      const stale = built[0];
      stale.gateInvoke = true;
      service.watchResource('host:roles', holder().ref);
      await settle();

      stale.gateInvoke = false;
      stale.releaseInvoke();
      void service.reset();
      service.watchResource('host:roles', holder().ref);
      await settle();
      expect(confirmed).toEqual([]);

      await service.connect();
      await settle();
      expect(calls(built[1], 'host:roles')).toEqual(['Subscribe']);
      expect(confirmed).toEqual(['host:roles']);
    });

    it('does not report resources of the previous principal resubscribed after a switch', async () => {
      service.watchResource('a-order', holder().ref);
      await service.connect();
      await settle();
      const stale = built[0];
      confirmed.length = 0;
      stale.gateInvoke = true;

      void stale.triggerReconnected();
      await settle();
      await service.reset();
      stale.gateInvoke = false;
      stale.releaseInvoke();
      await settle();

      expect(confirmed).toEqual([]);
    });
  });
  //#endif
});
