import { APP_BASE_HREF, PlatformLocation } from '@angular/common';
import { HttpClient, HttpContext } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Translation, TranslocoLoader } from '@jsverse/transloco';

import { SKIP_GATEWAY } from '../interceptors/http-context-tokens';

/** 运行时词条加载器：从 `{baseHref}i18n/{lang}.json` 加载，用 baseHref 前缀以支持子路径部署。 */
@Injectable({ providedIn: 'root' })
export class TranslocoHttpLoader implements TranslocoLoader {
  private readonly http = inject(HttpClient);

  // APP_BASE_HREF 未显式提供时回落到 PlatformLocation 解析出的 base，最终归一化为以 / 结尾。
  private readonly baseHref = this.resolveBaseHref();

  getTranslation(lang: string) {
    // 词条随前端一起发布，必须从站点自身取：默认会被加上网关前缀，
    // 前后端分域时就变成向 API 服务器要静态文件。
    return this.http.get<Translation>(`${this.baseHref}i18n/${lang}.json`, {
      context: new HttpContext().set(SKIP_GATEWAY, true),
    });
  }

  private resolveBaseHref(): string {
    const appBaseHref = inject(APP_BASE_HREF, { optional: true });
    const raw = appBaseHref ?? inject(PlatformLocation).getBaseHrefFromDOM() ?? '/';
    return raw.endsWith('/') ? raw : `${raw}/`;
  }
}
