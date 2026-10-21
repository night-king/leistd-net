//#if (OpenIddictServer)
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { LogoutConfirm } from './logout-confirm';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { LogoutConfirmationOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

/**
 * 依赖方发起退出的确认页。
 *
 * 确认必须是一次带防伪令牌的整页表单 POST 回协议端点：改成 XHR 的话，退出完成后回到依赖方的跳转
 * 不会由浏览器跟随；漏掉任何一个字段，服务端都会把它当成未确认、再次要求确认，用户卡在这一页。
 */
describe('LogoutConfirm', () => {
  let fixture: ComponentFixture<LogoutConfirm>;
  let getLogoutConfirmation: ReturnType<typeof vi.fn>;

  let submitted: HTMLFormElement[];

  async function render(
    query: Record<string, string>,
    response: LogoutConfirmationOutputDto | Error,
  ): Promise<void> {
    getLogoutConfirmation = vi
      .fn()
      .mockReturnValue(response instanceof Error ? throwError(() => response) : of(response));
    TestBed.configureTestingModule({
      imports: [LogoutConfirm],
      providers: [
        provideRouter([]),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: AccountService, useValue: { getLogoutConfirmation } },
        { provide: AuthService, useValue: { logout: vi.fn() } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(query) } },
        },
      ],
    });
    fixture = TestBed.createComponent(LogoutConfirm);
    await fixture.whenStable();
  }

  const element = (testId: string) =>
    (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${testId}"]`);

  beforeEach(() => {
    submitted = [];
    vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(function (
      this: HTMLFormElement,
    ) {
      submitted.push(this);
    });
  });

  afterEach(() => {
    fixture?.destroy();
    document.querySelectorAll('form[action="/connect/logout"]').forEach((form) => form.remove());
    vi.restoreAllMocks();
  });

  it('posts the confirmation with its antiforgery token back to the end session endpoint', async () => {
    await render(
      { request_uri: 'urn:request', confirmation: 'protected' },
      {
        isValid: true,
        applicationName: 'CRM',
        antiforgeryFieldName: '__RequestVerificationToken',
        antiforgeryToken: 'csrf-token',
      },
    );
    expect(getLogoutConfirmation).toHaveBeenCalledWith('urn:request', 'protected');
    expect(element('logout-confirm-description')).not.toBeNull();

    (element('logout-confirm-submit') as HTMLButtonElement).click();

    expect(submitted).toHaveLength(1);
    const form = submitted[0];
    expect(form.method).toBe('post');
    expect(form.getAttribute('action')).toBe('/connect/logout');
    expect(Object.fromEntries(new FormData(form))).toEqual({
      request_uri: 'urn:request',
      confirmation: 'protected',
      __RequestVerificationToken: 'csrf-token',
    });
  });

  it('offers no confirmation when the request is no longer valid', async () => {
    await render({ request_uri: 'urn:request', confirmation: 'stale' }, { isValid: false });

    expect(element('logout-confirm-invalid')).not.toBeNull();
    expect(element('logout-confirm-submit')).toBeNull();
  });

  it('reports a failed check as a request failure that can be retried, not as an expired request', async () => {
    await render({ request_uri: 'urn:request', confirmation: 'protected' }, new Error('offline'));

    expect(element('logout-confirm-failed')).not.toBeNull();
    expect(element('logout-confirm-invalid')).toBeNull();
    expect(element('logout-confirm-submit')).toBeNull();

    getLogoutConfirmation.mockReturnValue(
      of({ isValid: true, antiforgeryFieldName: 'f', antiforgeryToken: 't' }),
    );
    (element('logout-confirm-retry') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(element('logout-confirm-failed')).toBeNull();
    expect(element('logout-confirm-submit')).not.toBeNull();
  });

  it('does not ask the server without both references', async () => {
    await render({ confirmation: 'protected' }, { isValid: true });

    expect(getLogoutConfirmation).not.toHaveBeenCalled();
    expect(element('logout-confirm-invalid')).not.toBeNull();
  });

  it('stays signed in when cancelled', async () => {
    await render(
      { request_uri: 'urn:request', confirmation: 'protected' },
      { isValid: true, antiforgeryFieldName: 'f', antiforgeryToken: 't' },
    );
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    (element('logout-confirm-cancel') as HTMLButtonElement).click();

    expect(navigate).toHaveBeenCalledWith('/');
    expect(submitted).toHaveLength(0);
  });
});
//#endif
