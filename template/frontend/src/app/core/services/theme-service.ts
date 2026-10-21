import { BreakpointObserver } from '@angular/cdk/layout';
import { isPlatformBrowser } from '@angular/common';
import { PLATFORM_ID, computed, effect, inject, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

export const THEME_MODES = ['system', 'light', 'dark'] as const;
export type ThemeMode = (typeof THEME_MODES)[number];

export interface ThemePreferences {
  mode: ThemeMode;
}

const SYSTEM_DARK_QUERY = '(prefers-color-scheme: dark)';
const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

const DEFAULT_THEME_PREFERENCES: ThemePreferences = {
  mode: 'system',
};

/** 主题服务：管理亮/暗/跟随系统三态，切换 `<html>` 的 `.dark` class 并持久化；配色走 styles.css 的 CSS 变量。 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  static readonly STORAGE_KEY = 'theme_config';

  private readonly platformId = inject(PLATFORM_ID);
  /**
   * 系统暗色偏好。首个值同步给出（CDK 以当前匹配结果起头），之后的变化由 CDK 合并到下一轮任务里下发；
   * 非浏览器环境下 CDK 的媒体查询恒不匹配。订阅随根注入器销毁。
   */
  private readonly systemDark = toSignal(
    inject(BreakpointObserver)
      .observe(SYSTEM_DARK_QUERY)
      .pipe(map((state) => state.matches)),
    { requireSync: true },
  );
  /** 系统"减少动效"偏好：开启时切换主题不播放过渡，直接换色。 */
  private readonly reducedMotion = toSignal(
    inject(BreakpointObserver)
      .observe(REDUCED_MOTION_QUERY)
      .pipe(map((state) => state.matches)),
    { requireSync: true },
  );

  private readonly preferencesState = signal<ThemePreferences>(this.loadPreferences());

  readonly preferences = this.preferencesState.asReadonly();
  readonly mode = computed(() => this.preferences().mode);
  readonly modeIcon = computed(() => {
    switch (this.mode()) {
      case 'light':
        return 'lucideSun';
      case 'dark':
        return 'lucideMoon';
      default:
        return 'lucideMonitor';
    }
  });
  readonly isDarkTheme = computed(() => {
    const mode = this.mode();
    return mode === 'dark' || (mode === 'system' && this.systemDark());
  });

  constructor() {
    effect(() => {
      const preferences = this.preferences();
      const isDark = this.isDarkTheme();

      if (!isPlatformBrowser(this.platformId)) {
        return;
      }

      document.documentElement.classList.toggle('dark', isDark);
      localStorage.setItem(ThemeService.STORAGE_KEY, JSON.stringify(preferences));
    });
  }

  toggleTheme(): void {
    const modes: ThemeMode[] = ['light', 'system', 'dark'];
    const nextMode = modes[(modes.indexOf(this.mode()) + 1) % modes.length];

    if (
      isPlatformBrowser(this.platformId) &&
      document.startViewTransition &&
      !this.reducedMotion()
    ) {
      document.startViewTransition(() => this.setMode(nextMode));
      return;
    }

    this.setMode(nextMode);
  }

  setMode(mode: ThemeMode): void {
    this.preferencesState.update((preferences) => ({ ...preferences, mode }));
  }

  private loadPreferences(): ThemePreferences {
    if (!isPlatformBrowser(this.platformId)) {
      return DEFAULT_THEME_PREFERENCES;
    }

    const storedPreferences = localStorage.getItem(ThemeService.STORAGE_KEY);
    if (!storedPreferences) {
      return DEFAULT_THEME_PREFERENCES;
    }

    try {
      const parsed = JSON.parse(storedPreferences) as Partial<ThemePreferences>;
      return {
        mode: THEME_MODES.includes(parsed.mode as ThemeMode)
          ? (parsed.mode as ThemeMode)
          : DEFAULT_THEME_PREFERENCES.mode,
      };
    } catch {
      localStorage.removeItem(ThemeService.STORAGE_KEY);
      return DEFAULT_THEME_PREFERENCES;
    }
  }
}
