import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, map } from 'rxjs';

import { ThemeService } from './theme-service';

/**
 * 跟随系统的暗色判定。系统偏好由测试替身给出：测试浏览器自身是亮色，
 * 服务若绕开注入的 BreakpointObserver 自己去读媒体查询，这里的"系统切到暗色"就不会生效。
 */
describe('ThemeService', () => {
  const query = '(prefers-color-scheme: dark)';
  const reducedMotionQuery = '(prefers-reduced-motion: reduce)';
  let systemDark: BehaviorSubject<boolean>;
  let reducedMotion: BehaviorSubject<boolean>;

  // 只有查询的确是对应的系统偏好时才如实回报，查错了查询就恒为不匹配
  const state = (value: string, matches: boolean): BreakpointState => ({
    matches,
    breakpoints: { [value]: matches },
  });
  const observe = (value: string) => {
    if (value === query) return systemDark.pipe(map((matches) => state(value, matches)));
    if (value === reducedMotionQuery) {
      return reducedMotion.pipe(map((matches) => state(value, matches)));
    }
    return systemDark.pipe(map(() => state(value, false)));
  };

  function create(): ThemeService {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: BreakpointObserver,
          useValue: { observe },
        },
      ],
    });
    return TestBed.inject(ThemeService);
  }

  beforeEach(() => {
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    systemDark = new BehaviorSubject(false);
    reducedMotion = new BehaviorSubject(false);
  });

  afterEach(() => {
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    document.documentElement.classList.remove('dark');
  });

  it('follows the system preference in system mode, including later changes', () => {
    systemDark.next(true);
    const service = create();

    expect(service.mode()).toBe('system');
    expect(service.isDarkTheme()).toBe(true);

    systemDark.next(false);
    expect(service.isDarkTheme()).toBe(false);
  });

  it('ignores the system preference once a mode is chosen explicitly', () => {
    systemDark.next(true);
    const service = create();

    service.setMode('light');
    expect(service.isDarkTheme()).toBe(false);

    systemDark.next(false);
    service.setMode('dark');
    expect(service.isDarkTheme()).toBe(true);
  });

  it('applies the resolved theme to the document root', () => {
    systemDark.next(true);
    create();
    TestBed.tick();

    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('switches the theme without a view transition when the system asks for reduced motion', () => {
    reducedMotion.next(true);
    // 测试浏览器（Chromium）自带这个 API；替身直接执行回调，不真的播放过渡
    const startViewTransition = vi
      .spyOn(document, 'startViewTransition')
      .mockImplementation((update) => {
        void (update as () => void)();
        return {} as ViewTransition;
      });
    const service = create();

    service.toggleTheme();

    // 三态按 light → system → dark 循环，默认 system 的下一档是 dark
    expect(startViewTransition).not.toHaveBeenCalled();
    expect(service.mode()).toBe('dark');
  });

  it('keeps the view transition when reduced motion is not requested', () => {
    // 测试浏览器（Chromium）自带这个 API；替身直接执行回调，不真的播放过渡
    const startViewTransition = vi
      .spyOn(document, 'startViewTransition')
      .mockImplementation((update) => {
        void (update as () => void)();
        return {} as ViewTransition;
      });
    const service = create();

    service.toggleTheme();

    expect(startViewTransition).toHaveBeenCalledTimes(1);
    expect(service.mode()).toBe('dark');
  });
});
