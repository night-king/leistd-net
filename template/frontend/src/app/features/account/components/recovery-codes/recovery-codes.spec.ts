import { TestBed } from '@angular/core/testing';

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
  it('centers the official alert content at narrow, medium and wide widths', () => {
    const fixture = TestBed.createComponent(RecoveryCodes);
    fixture.componentRef.setInput('codes', ['fixture-one', 'fixture-two']);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-slot="alert"]')?.getAttribute('role')).toBe('status');
    for (const width of [320, 640, 1100]) {
      host.style.display = 'block';
      host.style.width = `${width}px`;
      const row = host.querySelector('[data-slot="alert-description"] > div')!;
      const centers = [...row.children].map((child) => {
        const rect = child.getBoundingClientRect();
        return rect.y + rect.height / 2;
      });
      expect(Math.max(...centers) - Math.min(...centers)).toBeLessThan(2);
    }
    const done = vi.fn();
    fixture.componentInstance.done.subscribe(done);
    (host.querySelector('[data-testid="recovery-codes-done"]') as HTMLButtonElement).click();
    expect(done).toHaveBeenCalledTimes(1);
  });
});
