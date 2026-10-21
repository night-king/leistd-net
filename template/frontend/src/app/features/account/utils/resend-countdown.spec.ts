import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { resendCountdown } from './resend-countdown';

@Component({ template: '' })
class Host {
  readonly countdown = resendCountdown();
}

/** 重发倒计时：按服务端给的秒数倒数，到 0 停表；组件销毁后不再走。 */
describe('resendCountdown', () => {
  afterEach(() => vi.useRealTimers());

  /** 宿主建好之后只伪造倒计时用到的计时器，rAF 等其余计时照常。 */
  function create() {
    const fixture = TestBed.createComponent(Host);
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    return fixture;
  }

  it('counts down once per second and stops at zero', () => {
    const { countdown } = create().componentInstance;

    countdown.start(2.4);
    expect(countdown.remaining()).toBe(3);
    vi.advanceTimersByTime(1000);
    expect(countdown.remaining()).toBe(2);
    vi.advanceTimersByTime(5000);
    expect(countdown.remaining()).toBe(0);
  });

  // 重新开始时旧表必须停掉：两只表同时走会每秒减 2
  it('restarts from the new value with a single timer and stops when asked', () => {
    const { countdown } = create().componentInstance;

    countdown.start(10);
    vi.advanceTimersByTime(3000);
    countdown.start(5);
    expect(countdown.remaining()).toBe(5);
    vi.advanceTimersByTime(1000);
    expect(countdown.remaining()).toBe(4);

    countdown.stop();
    vi.advanceTimersByTime(1000);
    expect(countdown.remaining()).toBe(0);
  });

  it('stops ticking when the owner is destroyed', () => {
    const fixture = create();
    const { countdown } = fixture.componentInstance;
    countdown.start(30);

    fixture.destroy();
    vi.advanceTimersByTime(5000);

    expect(countdown.remaining()).toBe(0);
  });
});
