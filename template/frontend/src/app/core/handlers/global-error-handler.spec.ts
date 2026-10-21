import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';

import { GlobalErrorHandler } from './global-error-handler';
import { ApplicationHttpError } from '../errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif

describe('GlobalErrorHandler', () => {
  let handler: GlobalErrorHandler;
  let errorToast: ReturnType<typeof vi.spyOn>;
  let consoleError: ReturnType<typeof vi.spyOn>;
  let consoleWarn: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        GlobalErrorHandler,
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
    handler = TestBed.inject(GlobalErrorHandler);
    errorToast = vi.spyOn(toast, 'error').mockImplementation(() => '');
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    consoleWarn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });

  // 拦截器归一化后的 HTTP 错误由发起操作的 feature 负责反馈；没被捕获而到达这里是预期情况
  it('ignores a normalized http error silently', () => {
    handler.handleError(
      ApplicationHttpError.from(new HttpErrorResponse({ status: 409, url: '/api/v1/users' })),
    );

    expect(errorToast).not.toHaveBeenCalled();
    expect(consoleError).not.toHaveBeenCalled();
    expect(consoleWarn).not.toHaveBeenCalled();
  });

  it('reports a raw http error as a bypassed interceptor chain without a toast', () => {
    handler.handleError(new HttpErrorResponse({ status: 500, url: 'https://other.example.com' }));

    expect(errorToast).not.toHaveBeenCalled();
    expect(consoleWarn).toHaveBeenCalledTimes(1);
  });

  it('shows a runtime error with its message', () => {
    handler.handleError(new Error('boom'));

    expect(consoleError).toHaveBeenCalled();
    expect(errorToast).toHaveBeenCalledWith(expect.any(String), { description: 'boom' });
  });

  it('shows a generic message for a thrown value that is not an error', () => {
    handler.handleError('boom');

    expect(errorToast).toHaveBeenCalledTimes(1);
    expect(errorToast.mock.calls[0][1]).not.toEqual({ description: 'boom' });
  });
});
