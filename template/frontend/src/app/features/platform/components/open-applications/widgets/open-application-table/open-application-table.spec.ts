import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { page } from 'vitest/browser';

import { OpenApplicationTable } from './open-application-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { OpenApplicationOutputDto } from '../../../../dtos/open-application.dto';

/** 开放应用表格在最窄一档的身份信息：主列截断名称与 Client ID，展开详情补全文。 */
describe('OpenApplicationTable', () => {
  let fixture: ComponentFixture<OpenApplicationTable>;
  let viewport: BehaviorSubject<BreakpointState>;

  const desktop = (matches: boolean): BreakpointState => ({
    matches,
    breakpoints: { '(min-width: 768px)': matches, '(min-width: 1024px)': matches },
  });

  function application(clientId: string, displayName?: string): OpenApplicationOutputDto {
    return {
      id: clientId,
      clientId,
      displayName,
      applicationType: 'web',
      clientType: 'confidential',
      redirectUris: [],
      postLogoutRedirectUris: [],
      permissions: [],
      requirements: [],
      settings: {},
      properties: {},
      hasClientSecret: true,
      sessionBound: null,
      creationTime: '2026-01-01T00:00:00Z',
    } as OpenApplicationOutputDto;
  }

  beforeEach(async () => {
    viewport = new BehaviorSubject<BreakpointState>(desktop(false));
    await TestBed.configureTestingModule({
      imports: [OpenApplicationTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: BreakpointObserver, useValue: { observe: () => viewport.asObservable() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(OpenApplicationTable);
  });

  it('shows the full name and client id in the expanded row only at the narrowest level', async () => {
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '356px';
    const longName = 'Customer Self-Service Portal for Regional Partners';
    const clientId = 'customer-self-service-portal-regional-partners';
    fixture.componentRef.setInput('applications', [application(clientId, longName)]);
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.autoDetectChanges();
    await expect
      .poll(() => host.querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .toBe('mobile');

    host.querySelector('tbody ng-icon[name="lucideChevronRight"]')!.closest('button')!.click();
    await fixture.whenStable();

    const identity = host.querySelector<HTMLElement>('[data-testid="open-app-detail-identity"]')!;
    expect(getComputedStyle(identity).display).not.toBe('none');
    expect(identity.textContent).toContain(longName);
    expect(identity.textContent).toContain(clientId);

    // 视口档位是表格档位的上限，加宽容器之外还要把视口放到桌面
    await page.viewport(1800, 900);
    viewport.next(desktop(true));
    host.style.width = '1600px';
    await expect
      .poll(() => host.querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .not.toBe('mobile');
    // 没有收起的列时展开行整个不渲染；渲染时这一项也不显示
    const wide = host.querySelector<HTMLElement>('[data-testid="open-app-detail-identity"]');
    expect(wide === null || getComputedStyle(wide).display === 'none').toBe(true);
  });
});
