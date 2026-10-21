import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { EMPTY, of, throwError } from 'rxjs';

import { TwoFactorRequired } from './two-factor-required';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
//#if (OpenIddictServer)
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
import { AccountService } from '../../services/account-service';
import { RecoveryCodes } from '../recovery-codes/recovery-codes';
import { TwoFactorSetup } from '../two-factor-setup/two-factor-setup';

import type { Mock } from 'vitest';

/** 组织强制两步验证时的受限会话页：设置完成 → 展示恢复码 → 刷新会话上下文后回到原目标或进入应用。 */
describe('TwoFactorRequired', () => {
  let fixture: ComponentFixture<TwoFactorRequired>;
  let auth: { loadUser: Mock; logout: Mock; currentUser: () => unknown };
  let establish: Mock<() => Promise<void>>;
  let canAccessPlatform: Mock<() => boolean>;
  let navigate: Mock;
  let navigateByUrl: Mock;
  /** 设置页地址上的查询参数；页面在离开时才读，用例可以在那之前设定。 */
  let queryParams: Record<string, string>;

  function setup(): TwoFactorSetup | undefined {
    return fixture.debugElement.query(By.directive(TwoFactorSetup))?.componentInstance as
      TwoFactorSetup | undefined;
  }

  function recoveryCodes(): RecoveryCodes | undefined {
    return fixture.debugElement.query(By.directive(RecoveryCodes))?.componentInstance as
      RecoveryCodes | undefined;
  }

  /** 走到恢复码那一步：由真实的设置组件发出启用结果。 */
  async function enable(): Promise<void> {
    setup()!.enabled.emit(['code-1', 'code-2']);
    await fixture.whenStable();
  }

  async function confirmCodesSaved(): Promise<void> {
    recoveryCodes()!.done.emit();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    auth = {
      loadUser: vi.fn(() => of(undefined)),
      logout: vi.fn(),
      // 设置组件按当前用户决定确认身份时能出示什么；本页用例不走到那一步
      currentUser: () => ({ hasPassword: true, isTwoFactorEnabled: false }),
    };
    establish = vi.fn(async () => undefined);
    canAccessPlatform = vi.fn(() => false);
    queryParams = {};
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [TwoFactorRequired],
      // prettier-ignore
      providers: [
        provideRouter([]),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: AuthService, useValue: auth },
        { provide: AuthorizationService, useValue: { canAccessPlatform } },
        { provide: SessionContextService, useValue: { establish } },
        // 设置组件只需能完成首次加载，它自己的流程由它的用例负责
        { provide: AccountService, useValue: { beginTwoFactorSetup: () => EMPTY } },
        {
          provide: ActivatedRoute,
          useValue: { get snapshot() { return { queryParamMap: convertToParamMap(queryParams) }; } },
        },
      ],
    });

    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true) as Mock;
    navigateByUrl = vi
      .spyOn(TestBed.inject(Router), 'navigateByUrl')
      .mockResolvedValue(true) as Mock;
    fixture = TestBed.createComponent(TwoFactorRequired);
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('starts at the setup step and cannot be cancelled', () => {
    expect(setup()).toBeDefined();
    // 受限会话只能到这里：可取消的话就退回一个哪儿也去不了的状态
    expect(setup()!.cancellable()).toBe(false);
    expect(recoveryCodes()).toBeUndefined();
  });

  it('shows the recovery codes after enabling, before leaving the page', async () => {
    await enable();

    expect(setup()).toBeUndefined();
    expect(recoveryCodes()!.codes()).toEqual(['code-1', 'code-2']);
    expect(auth.loadUser).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('rebuilds the session and enters the workspace once the codes are saved', async () => {
    await enable();

    await confirmCodesSaved();

    expect(auth.loadUser).toHaveBeenCalledOnce();
    expect(establish).toHaveBeenCalledOnce();
    expect(auth.loadUser.mock.invocationCallOrder[0]).toBeLessThan(
      establish.mock.invocationCallOrder[0],
    );
    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/workspace']);
  });

  it('enters the platform when the user may access it', async () => {
    canAccessPlatform.mockReturnValue(true);
    await enable();

    await confirmCodesSaved();

    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/platform']);
  });

  // 被拦下时要去的地方：设置完成、会话上下文就位之后回到那里，而不是固定的首页
  it('returns to the page the session was heading to once the codes are saved', async () => {
    queryParams = { returnUrl: '/platform/users?offset=20' };
    await enable();

    await confirmCodesSaved();

    expect(establish).toHaveBeenCalledOnce();
    expect(establish.mock.invocationCallOrder[0]).toBeLessThan(
      navigateByUrl.mock.invocationCallOrder[0],
    );
    expect(navigateByUrl).toHaveBeenCalledExactlyOnceWith('/platform/users?offset=20');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('ignores a target that leaves the site and enters the workspace instead', async () => {
    queryParams = { returnUrl: 'https://evil.example/' };
    await enable();

    await confirmCodesSaved();

    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/workspace']);
  });
  //#if (OpenIddictServer)

  // 授权请求被受限会话拦下：设置完成后整页回到同一个缓存的授权请求
  it('continues an authorization request with a full page load', async () => {
    const target = '/connect/authorize?client_id=resource&request_uri=urn%3Aabc';
    queryParams = { returnUrl: target };
    const loadPage = vi
      .spyOn(TestBed.inject(ReturnUrlService), 'loadPage')
      .mockImplementation(() => undefined);
    await enable();

    await confirmCodesSaved();

    expect(loadPage).toHaveBeenCalledExactlyOnceWith(target);
    expect(navigateByUrl).not.toHaveBeenCalled();
  });
  //#endif

  it('stays on the page with an error when the session cannot be rebuilt', async () => {
    auth.loadUser.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server Error' })),
    );
    await enable();

    await confirmCodesSaved();

    expect(toast.error).toHaveBeenCalledOnce();
    expect(establish).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    // 恢复码仍在，人可以再点一次
    expect(recoveryCodes()).toBeDefined();
  });

  it('signs out from the sign-out button', async () => {
    const signOut = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'button[variant="link"]',
    )!;

    signOut.click();

    expect(auth.logout).toHaveBeenCalledOnce();
  });
});
