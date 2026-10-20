import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { CaptchaImage } from './captcha-image';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import {
  expectTouchTarget,
  setCoarsePointer,
} from '../../../../shared/utils/coarse-pointer.testing';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

/** 图形验证码图片：创建即取一张，点击换一张；每换一张通知父表单清空已填的码。 */
describe('CaptchaImage', () => {
  let account: Pick<MockedObject<AccountService>, 'getCaptcha'>;

  beforeEach(() => {
    account = { getCaptcha: vi.fn().mockName('AccountService.getCaptcha') };
    let next = 0;
    account.getCaptcha.mockImplementation(() =>
      of({ captchaToken: `token-${++next}`, captchaImageBase64: 'data:image/png;base64,' }),
    );
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        { provide: AccountService, useValue: account },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(CaptchaImage);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, changed, host: fixture.nativeElement as HTMLElement };
  }

  it('loads an image on creation and exposes its token', async () => {
    const { fixture, host } = await render();

    expect(fixture.componentInstance.token()).toBe('token-1');
    expect(host.querySelector('img')).not.toBeNull();
  });

  // 令牌只能用一次：换一张后旧码作废，父表单要清空已填的码
  it('loads a new image on click and tells the form to clear the code', async () => {
    const { fixture, changed, host } = await render();
    changed.mockClear();

    host.querySelector<HTMLButtonElement>('[data-testid="captcha-image"]')!.click();
    await fixture.whenStable();

    expect(fixture.componentInstance.token()).toBe('token-2');
    expect(changed).toHaveBeenCalledOnce();
  });

  // 首次取图失败：没有令牌就提交不了，要能就地重试，而不是只能刷新整页
  it('offers a retry when the first image cannot be loaded and recovers from it', async () => {
    account.getCaptcha.mockReturnValueOnce(throwError(() => new Error('unavailable')));
    const { fixture, changed, host } = await render();

    expect(fixture.componentInstance.token()).toBeNull();
    expect(changed).not.toHaveBeenCalled();

    host.querySelector<HTMLButtonElement>('[data-testid="captcha-retry"]')!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.token()).toBe('token-1');
    expect(host.querySelector('[data-testid="captcha-retry"]')).toBeNull();
    expect(changed).toHaveBeenCalledOnce();
  });

  // 旧令牌可能已被上一次请求用掉：换图失败时不能再把它交出去
  it('drops the previous token when loading a new image fails', async () => {
    const { fixture } = await render();
    account.getCaptcha.mockReturnValueOnce(throwError(() => new Error('unavailable')));

    await fixture.componentInstance.reload();

    expect(fixture.componentInstance.token()).toBeNull();
  });

  describe('on a touch screen', () => {
    beforeEach(() => setCoarsePointer(true));
    afterEach(() => setCoarsePointer(false));

    it('keeps the image a full-size touch target', async () => {
      const { host } = await render();

      expectTouchTarget(host.querySelector('[data-testid="captcha-image"]')!);
    });
  });
});
