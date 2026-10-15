import { TestBed } from '@angular/core/testing';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif

import { RecoveryCodes } from './recovery-codes';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif

describe('RecoveryCodes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RecoveryCodes],
      //#if (IncludeLocalization)
      providers: [...provideTranslocoTesting(['en'])],
      //#endif
    });
  });
  // 产品要求：提示图标对整段文字垂直居中（不是官方默认的首行对齐）。先确认窄档确实折成多行、宽档是一行，
  // 再量图标与文字的中心，避免"只在单行时量过"就宣称多行也居中
  it('centers the warning icon on the text whether it wraps or not', async () => {
    //#if (IncludeLocalization)
    // 测试不装词条时渲染成键名，比真实文案短得多、窄档也折不了行；量换行要用真实长度的文案
    TestBed.inject(TranslocoService).setTranslation(
      {
        twoFactor: {
          recoveryCodesWarning:
            'Save these recovery codes somewhere safe. Each one signs you in once if you lose your phone. They will not be shown again.',
        },
      },
      'account/en',
    );
    //#endif
    const fixture = TestBed.createComponent(RecoveryCodes);
    fixture.componentRef.setInput('codes', ['fixture-one', 'fixture-two']);
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    expect(host.querySelector('[data-slot="alert"]')?.getAttribute('role')).toBe('status');
    const row = host.querySelector('[data-slot="alert-description"] > div')!;
    const [icon, text] = [...row.children] as HTMLElement[];
    const lineHeight = Number.parseFloat(getComputedStyle(text).lineHeight);

    for (const [width, multiline] of [
      [320, true],
      [1100, false],
    ] as const) {
      host.style.width = `${width}px`;
      const textRect = text.getBoundingClientRect();
      expect(textRect.height > lineHeight * 1.5, `text wraps at ${width}px`).toBe(multiline);
      const iconRect = icon.getBoundingClientRect();
      const iconCenter = iconRect.y + iconRect.height / 2;
      const textCenter = textRect.y + textRect.height / 2;
      expect(Math.abs(iconCenter - textCenter), `centered at ${width}px`).toBeLessThan(1);
    }
  });

  it('reports when the codes were saved', () => {
    const fixture = TestBed.createComponent(RecoveryCodes);
    fixture.componentRef.setInput('codes', ['fixture-one']);
    fixture.detectChanges();
    const done = vi.fn();
    fixture.componentInstance.done.subscribe(done);

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="recovery-codes-done"]')!
      .click();

    expect(done).toHaveBeenCalledTimes(1);
  });
});
