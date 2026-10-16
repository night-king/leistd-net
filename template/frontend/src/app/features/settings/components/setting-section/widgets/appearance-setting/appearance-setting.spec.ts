import { TestBed } from '@angular/core/testing';

import { AppearanceSetting } from './appearance-setting';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { ThemeService } from '../../../../../../core/services/theme-service';

/** 偏好面板的外观与页头切换共用同一份主题状态：一边改了，另一边读到的就是新值。 */
describe('AppearanceSetting', () => {
  beforeEach(() => {
    localStorage.removeItem(ThemeService.STORAGE_KEY);
    TestBed.configureTestingModule({
      imports: [AppearanceSetting],
      //#if (IncludeLocalization)
      providers: [...provideTranslocoTesting(['en'])],
      //#endif
    });
  });

  afterEach(() => localStorage.removeItem(ThemeService.STORAGE_KEY));

  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(AppearanceSetting);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the mode chosen elsewhere and resets to following the system', async () => {
    const theme = TestBed.inject(ThemeService);
    theme.setMode('dark');
    const host = await render();

    const reset = host.querySelector<HTMLButtonElement>('[data-testid="appearance-reset"]')!;
    expect(reset.disabled).toBe(false);

    reset.click();
    expect(theme.mode()).toBe('system');
  });

  // 渲染之后在页头改了模式：偏好面板跟着变
  it('follows a mode changed elsewhere after it is shown', async () => {
    const theme = TestBed.inject(ThemeService);
    theme.setMode('system');
    const fixture = TestBed.createComponent(AppearanceSetting);
    await fixture.whenStable();
    fixture.detectChanges();
    const reset = () =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
        '[data-testid="appearance-reset"]',
      )!;
    expect(reset().disabled).toBe(true);

    theme.setMode('light');
    fixture.detectChanges();

    expect(reset().disabled).toBe(false);
  });

  it('cannot reset when it already follows the system', async () => {
    TestBed.inject(ThemeService).setMode('system');
    const host = await render();

    expect(
      host.querySelector<HTMLButtonElement>('[data-testid="appearance-reset"]')!.disabled,
    ).toBe(true);
  });
});
