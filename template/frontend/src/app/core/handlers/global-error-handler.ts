import { HttpErrorResponse } from '@angular/common/http';
// prettier-ignore
import {
  ErrorHandler,
  Injectable,
  //#if (IncludeLocalization)
  inject,
  //#endif
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';

import { ApplicationHttpError } from '../errors/application-http-error';

/**
 * 全局错误处理器：兜底未处理的非 HTTP 错误。已归一化的 `ApplicationHttpError` 静默忽略（反馈由
 * feature 负责）；未归一化的 `HttpErrorResponse` 说明请求绕过了拦截器链，只记录配置问题。
 */
@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);

  //#endif
  handleError(error: unknown): void {
    if (error instanceof ApplicationHttpError) {
      return;
    }

    console.error('Global error caught:', error);

    if (error instanceof HttpErrorResponse) {
      console.warn(
        'An HTTP error bypassed the application interceptor chain. Check how the request was sent.',
      );
      return;
    }

    if (error instanceof Error) {
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('common.appError'), { description: error.message });
      //#else
      toast.error('Application error', { description: error.message });
      //#endif
      return;
    }

    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.unknownError'), {
      description: this.transloco.translate('common.unexpectedError'),
    });
    //#else
    toast.error('Unknown error', {
      description: 'An unexpected error occurred. Please refresh the page and try again.',
    });
    //#endif
  }
}
