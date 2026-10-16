import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ReauthenticateDialog } from './reauthenticate-dialog';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif

/** 确认是本人：只收口令交给调用方重试；空口令不交出去，并说明原因。 */
@Component({
  imports: [ReauthenticateDialog],
  template: `
    <app-reauthenticate-dialog [(visible)]="visible" (confirmed)="confirmed.push($event)" />
  `,
})
class HostComponent {
  readonly visible = signal(true);
  readonly confirmed: string[] = [];
}

describe('ReauthenticateDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  /** 对话框渲染在 document 上的浮层里。 */
  function passwordInput(): HTMLInputElement {
    return document.getElementById('reauthenticate-password') as HTMLInputElement;
  }

  function submitButton(): HTMLButtonElement {
    return document.querySelector<HTMLButtonElement>('hlm-dialog-footer button[type="submit"]')!;
  }

  function shownErrors(): number {
    return Array.from(
      passwordInput().closest('[hlmField]')!.querySelectorAll<HTMLElement>('hlm-field-error'),
    ).filter((element) => !element.hidden).length;
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
        provideZonelessChangeDetection(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('does not hand over an empty password and reveals the error on submit', async () => {
    expect(shownErrors()).toBe(0);

    submitButton().click();
    await fixture.whenStable();

    expect(host.confirmed).toEqual([]);
    expect(shownErrors()).toBeGreaterThan(0);
  });

  it('hands the entered password to the caller', async () => {
    passwordInput().value = 'admin-password';
    passwordInput().dispatchEvent(new Event('input'));
    await fixture.whenStable();

    submitButton().click();
    await fixture.whenStable();

    expect(host.confirmed).toEqual(['admin-password']);
  });
});
