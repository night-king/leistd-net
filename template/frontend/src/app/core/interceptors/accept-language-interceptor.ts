import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { LanguageService } from '../services/language-service';

/**
 * 为每个请求注入 Accept-Language，使后端消息按当前活动语言本地化；置于拦截器链首位。词条请求
 * （`/i18n/*.json`）直接放行、不注入 LanguageService：它们发生在启动早期，注入会与其构造期加载重入。
 */
export const acceptLanguageInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.url.includes('/i18n/')) {
    return next(req);
  }

  const languageService = inject(LanguageService);
  return next(req.clone({ setHeaders: { 'Accept-Language': languageService.activeLang() } }));
};
