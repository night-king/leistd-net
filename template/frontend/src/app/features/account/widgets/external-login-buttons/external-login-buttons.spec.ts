import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';

import { ExternalLoginButtons } from './external-login-buttons';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { ExternalLoginProvidersOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

/**
 * 登录页与注册页共用的外部登录入口：部署没有可用的提供方时连同分隔线一起消失，
 * 只剩本地表单；目录取不到时要说出来并能重试，不能当成"没有"。
 */
describe('ExternalLoginButtons', () => {
  let fixture: ComponentFixture<ExternalLoginButtons>;
  let account: Pick<MockedObject<AccountService>, 'getExternalLoginProviders'>;

  async function render(
    providers: ExternalLoginProvidersOutputDto | Observable<ExternalLoginProvidersOutputDto>,
  ): Promise<HTMLElement> {
    account.getExternalLoginProviders.mockReturnValue(
      providers instanceof Observable ? providers : of(providers),
    );
    fixture = TestBed.createComponent(ExternalLoginButtons);
    fixture.componentRef.setInput('dividerLabel', 'or');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    account = {
      getExternalLoginProviders: vi.fn().mockName('AccountService.getExternalLoginProviders'),
    };
    TestBed.configureTestingModule({
      imports: [ExternalLoginButtons],
      // prettier-ignore
      providers: [
        { provide: AccountService, useValue: account },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  afterEach(() => fixture.destroy());

  it('renders a button per known provider with the divider', async () => {
    const host = await render({ providers: ['github', 'google'] });

    expect(host.querySelector('[data-testid="external-login-github"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="external-login-google"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="external-divider"]')).not.toBeNull();
  });

  it('renders nothing, not even the divider, when no provider is available', async () => {
    const host = await render({ providers: [] });

    expect(host.querySelector('button')).toBeNull();
    expect(host.querySelector('[data-testid="external-divider"]')).toBeNull();
  });

  // 页面只内置 GitHub、Google 的入口；目录里只有别的提供方时，留一条分隔线等于暗示下面还有东西
  it('renders nothing when the directory only lists providers without a built-in button', async () => {
    const host = await render({ providers: ['microsoft'] });

    expect(host.querySelector('button')).toBeNull();
    expect(host.querySelector('[data-testid="external-divider"]')).toBeNull();
  });

  it('reports a load failure with retry and shows the buttons once retry succeeds', async () => {
    const host = await render(throwError(() => new Error('offline')));

    //#if (IncludeLocalization)
    expect(host.textContent).toContain('account.login.externalProvidersLoadFailed');
    //#else
    expect(host.textContent).toContain('Third-party sign-in options could not be loaded.');
    //#endif
    expect(host.querySelector('[data-testid="external-divider"]')).toBeNull();
    const retry = host.querySelector<HTMLButtonElement>('[data-testid="external-login-retry"]');
    expect(retry).not.toBeNull();

    account.getExternalLoginProviders.mockReturnValue(of({ providers: ['github'] }));
    retry!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.getExternalLoginProviders).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-testid="external-login-retry"]')).toBeNull();
    expect(host.querySelector('[data-testid="external-login-github"]')).not.toBeNull();
  });
});
