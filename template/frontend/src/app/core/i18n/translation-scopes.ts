import { Location } from '@angular/common';
import { inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationCancel, NavigationEnd, ResolveFn, Router } from '@angular/router';
import { TRANSLOCO_SCOPE } from '@jsverse/transloco';
import { EMPTY, from, mergeMap, of } from 'rxjs';

import { LanguageService } from '../services/language-service';

/** 首次导航没有原页面可保留，根组件复用启动失败卡片；重试保留完整目标 URL。 */
@Injectable({ providedIn: 'root' })
export class TranslationScopeRecovery {
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly failedUrlState = signal<string | null>(null);
  readonly failedUrl = this.failedUrlState.asReadonly();

  constructor() {
    this.router.events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event instanceof NavigationEnd) {
        this.failedUrlState.set(null);
      } else if (event instanceof NavigationCancel && this.failedUrl()) {
        // Router 取消首次导航时会还原到 /，地址栏仍保留用户打开的深链。
        this.location.replaceState(this.failedUrl()!);
      }
    });
  }

  fail(url: string): void {
    if (!this.router.navigated) {
      this.failedUrlState.set(url);
    }
  }

  async retry(): Promise<void> {
    const url = this.failedUrl();
    if (url) {
      await this.router.navigateByUrl(url, { onSameUrlNavigation: 'reload' });
    }
  }
}

/** 使用路由登记的完整官方 scope；已有页面时失败取消导航，首次失败由根组件提供恢复入口。 */
export const resolveTranslationScopes: ResolveFn<boolean> = (_, state) => {
  const provided = inject(TRANSLOCO_SCOPE);
  const scopes = (Array.isArray(provided) ? provided : [provided]).filter(
    (scope) => scope !== undefined,
  );
  const recovery = inject(TranslationScopeRecovery);
  return from(inject(LanguageService).loadScopes(scopes)).pipe(
    mergeMap((loaded) => {
      if (loaded) {
        return of(true);
      }
      recovery.fail(state.url);
      return EMPTY;
    }),
  );
};
