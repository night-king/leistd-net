import { Component, computed, effect, inject, signal } from '@angular/core';
//#if (IncludeLocalization)
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
//#endif
import { Title } from '@angular/platform-browser';
import { RouterOutlet } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideRefreshCw } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmToaster } from '@spartan-ng/helm/sonner';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
//#if (IncludeLocalization)
import { catchError, defaultIfEmpty, map, Observable, of, switchMap } from 'rxjs';
//#endif

import {
  ApplicationHttpError,
  applicationErrorMessage,
} from './core/errors/application-http-error';
//#if (IncludeLocalization)
import { TranslationScopeRecovery } from './core/i18n/translation-scopes';
//#endif
import { LayoutService } from './core/services/layout-service';
import { StartupService } from './core/services/startup-service';
import { ThemeService } from './core/services/theme-service';

/** 应用名的英文原文，与 `index.html` 启动前的标题一致。 */
const APP_NAME = 'Template Project';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, HlmToaster, HlmSpinner, HlmButton, NgIcon, ...HlmCardImports],
  providers: [provideIcons({ lucideRefreshCw })],
  template: `
    <!-- 全局 toast 宿主。标题与说明保留换行：多个字段的校验错误、批量操作的失败项各占一行。
         按类型只给图标上色（底色保持中性）：颜色用语义令牌，暗色自动跟随；
         不用 sonner 的 richColors——那会连底色一起换成它自带的调色板，且暗色一档只在
         data-theme="dark" 下生效，与本项目的令牌体系两张皮。 -->
    <hlm-toaster
      [toastOptions]="{
        classes: {
          title: 'whitespace-pre-line',
          description: 'whitespace-pre-line',
          success: '[&_[data-icon]]:text-success',
          warning: '[&_[data-icon]]:text-warning',
          error: '[&_[data-icon]]:text-destructive',
          info: '[&_[data-icon]]:text-info',
        },
      }"
    />

    @switch (status()) {
      @case ('success') {
        <router-outlet></router-outlet>
      }
      @case ('loading') {
        <div class="h-screen w-full flex items-center justify-center bg-background">
          <hlm-spinner class="text-4xl" [attr.aria-label]="loadingLabel()" />
        </div>
      }
      @case ('failed') {
        <div class="h-screen w-full flex items-center justify-center bg-background">
          @if (hasFailure()) {
            <section hlmCard class="w-[360px] text-center">
              <div hlmCardHeader>
                <h1 hlmCardTitle>{{ failedHeader() }}</h1>
              </div>
              <div hlmCardContent>
                <p>{{ errorMessage() }}</p>
              </div>
              <div hlmCardFooter class="justify-center">
                <button hlmBtn (click)="onRetryClick()" [disabled]="isRetrying()">
                  @if (isRetrying()) {
                    <hlm-spinner class="text-base" data-icon="inline-start" />
                  } @else {
                    <ng-icon name="lucideRefreshCw" data-icon="inline-start" />
                  }
                  {{ retryLabel() }}
                </button>
              </div>
            </section>
          }
        </div>
      }
    }
  `,
})
export class App {
  protected readonly startupService = inject(StartupService);
  // 注入即让主题在启动时生效（ThemeService 构造时注册 effect）。
  protected readonly themeService = inject(ThemeService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  private readonly scopeRecovery = inject(TranslationScopeRecovery);
  // 根组件不用 *transloco 结构指令，也不用 translateSignal：前者要等词条到位才渲染内容，
  // 后者在词条加载失败时读取即抛错，而启动失败页恰恰要在词条缺失时照样显示出来。
  protected readonly loadingLabel = this.startupText({
    key: 'app.startup.loading',
    english: 'Loading...',
  });
  protected readonly failedHeader = this.startupText({
    key: 'app.startup.failed',
    english: 'Application failed to load',
  });
  protected readonly retryLabel = this.startupText({ key: 'common.retry', english: 'Retry' });
  private readonly appName = this.startupText({ key: 'app.name', english: APP_NAME });
  private readonly errorText = computed(() =>
    this.scopeRecovery.failedUrl()
      ? {
          key: 'app.startup.unknownError',
          english: 'An unknown error occurred. Please try again later.',
        }
      : startupErrorText(this.startupService.error()),
  );
  protected readonly errorMessage = toSignal(
    toObservable(this.errorText).pipe(switchMap((text) => this.selectStartupText(text))),
    { initialValue: '' },
  );
  //#else
  protected readonly loadingLabel = () => 'Loading...';
  protected readonly failedHeader = () => 'Application failed to load';
  protected readonly retryLabel = () => 'Retry';
  private readonly appName = () => APP_NAME;
  protected readonly errorMessage = computed(
    () => startupErrorText(this.startupService.error()).english,
  );
  //#endif

  protected readonly status = computed(() => {
    //#if (IncludeLocalization)
    if (this.startupService.status() === 'success' && this.scopeRecovery.failedUrl()) {
      return 'failed';
    }
    //#endif
    return this.startupService.status();
  });
  protected readonly hasFailure = computed(() => {
    //#if (IncludeLocalization)
    if (this.scopeRecovery.failedUrl()) {
      return true;
    }
    //#endif
    return !!this.startupService.error();
  });

  private _isRetrying = signal(false);
  public readonly isRetrying = this._isRetrying.asReadonly();

  constructor() {
    // 浏览器标签页标题只在这里设：页面标题取自各页已经在设的布局标题（随语言切换已是新语言），
    // 没有页面标题的页（登录、注册等认证页，落地页）只显示应用名
    const layoutService = inject(LayoutService);
    const title = inject(Title);
    effect(() => {
      const pageTitle = layoutService.title();
      title.setTitle(pageTitle ? `${pageTitle} · ${this.appName()}` : this.appName());
    });
  }

  async onRetryClick(): Promise<void> {
    this._isRetrying.set(true);
    try {
      //#if (IncludeLocalization)
      if (this.scopeRecovery.failedUrl()) {
        await this.scopeRecovery.retry();
      } else {
        await this.startupService.retry();
      }
      //#else
      await this.startupService.retry();
      //#endif
    } finally {
      this._isRetrying.set(false);
    }
  }
  //#if (IncludeLocalization)

  private startupText(text: StartupText) {
    return toSignal(this.selectStartupText(text), { initialValue: text.english });
  }

  /** 随语言切换与词条到达更新；词条取不到或缺这一条时退回英文。 */
  private selectStartupText({ key, params, english }: StartupText): Observable<string> {
    return this.transloco.langChanges$.pipe(
      switchMap((lang) =>
        this.transloco.selectTranslate<string>(key, params, lang).pipe(
          map((value) => (value === key ? english : value)),
          // 加载失败时 Transloco 可能报错，也可能直接结束而不发射
          defaultIfEmpty(english),
          catchError(() => of(english)),
        ),
      ),
    );
  }
  //#endif
}

/** 启动页的一句文案：本地化形态按 `key` 取词条，词条缺失与不含本地化时显示 `english`。 */
interface StartupText {
  key: string;
  params?: Record<string, unknown>;
  english: string;
}

/** 启动失败的说明；拦截器已把 HTTP 错误归一化为类型化 ApplicationHttpError，这里按其契约展示。 */
function startupErrorText(error: unknown): StartupText {
  if (error instanceof ApplicationHttpError) {
    const message = applicationErrorMessage(error);
    if (error.code) {
      return {
        key: 'app.startup.requestFailed',
        params: { message, code: error.code },
        english: `Request failed: ${message} (code: ${error.code})`,
      };
    }
    return {
      key: 'app.startup.serverError',
      params: { status: error.status, statusText: message },
      english: `Unknown server error: ${error.status} - ${message}`,
    };
  }

  if (error instanceof Error) {
    return {
      key: 'app.startup.unknownErrorDetail',
      params: { message: error.message },
      english: `An unknown error occurred: ${error.message}`,
    };
  }

  return {
    key: 'app.startup.unknownError',
    english: 'An unknown error occurred. Please try again later.',
  };
}
