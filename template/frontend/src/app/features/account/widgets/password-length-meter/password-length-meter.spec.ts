import { TestBed } from '@angular/core/testing';

import { PasswordLengthMeter } from './password-length-meter';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif

/** 口令长度进度：只反映口令策略的最小长度，不另立复杂度规则，够长即达标。 */
describe('PasswordLengthMeter', () => {
  function render(password: string): HTMLElement {
    // prettier-ignore
    TestBed.configureTestingModule({
      providers: [
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
    const fixture = TestBed.createComponent(PasswordLengthMeter);
    fixture.componentRef.setInput('password', password);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function filled(host: HTMLElement): number {
    return host.querySelectorAll('span.bg-warning, span.bg-success').length;
  }

  it('fills toward the minimum and says how many characters remain', () => {
    const host = render('abcdef');

    expect(filled(host)).toBe(2);
    expect(host.querySelector('span.bg-success')).toBeNull();
    //#if (IncludeLocalization)
    expect(host.textContent).toContain('account.passwordLength.progress');
    //#else
    expect(host.textContent).toContain('6 more to go');
    //#endif
  });

  it('turns complete once the minimum length is reached', () => {
    const host = render('abcdefghijkl');

    expect(host.querySelectorAll('span.bg-success')).toHaveLength(4);
    //#if (IncludeLocalization)
    expect(host.textContent).toContain('account.passwordLength.met');
    //#else
    expect(host.textContent).toContain('Length requirement met');
    //#endif
  });
});
