import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { SortingState } from '@tanstack/angular-table';
import { BehaviorSubject } from 'rxjs';
import { page, userEvent } from 'vitest/browser';

import { OpenApplicationTable } from './open-application-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { expectAriaSortOnSortedHeaderOnly } from '../../../../../../shared/utils/table-sorting.testing';
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

  // 换列由用户表与角色表的同名用例覆盖；这里只有 Client ID 一列在各档都显示
  it('puts aria-sort only on the header of the sorted column', async () => {
    fixture.componentRef.setInput('applications', [application('portal')]);
    fixture.detectChanges();
    // 表格包在 @defer 里，测试环境不会自己渲染它
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    // 表头格属于哪一列，以点它的排序按钮发出的排序意图为准
    const sortedColumns: string[] = [];
    fixture.componentInstance.sortingChange.subscribe((value) => sortedColumns.push(value[0].id));
    const columnOf = (header: HTMLTableCellElement | null) => {
      header!.querySelector<HTMLButtonElement>(':scope > button')!.click();
      return sortedColumns.at(-1);
    };
    expectAriaSortOnSortedHeaderOnly(host, null);

    fixture.componentRef.setInput('sorting', [{ id: 'clientId', desc: false }] as SortingState);
    fixture.detectChanges();
    expect(columnOf(expectAriaSortOnSortedHeaderOnly(host, 'ascending'))).toBe('clientId');

    fixture.componentRef.setInput('sorting', [{ id: 'clientId', desc: true }] as SortingState);
    fixture.detectChanges();
    expectAriaSortOnSortedHeaderOnly(host, 'descending');

    fixture.componentRef.setInput('sorting', [] as SortingState);
    fixture.detectChanges();
    expectAriaSortOnSortedHeaderOnly(host, null);
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
    // 主列里名称与 Client ID 上下两行，长了各自截断，不压到右邻的类型列上
    const titleCell = host.querySelector<HTMLElement>('tbody td')!;
    for (const text of Array.from(titleCell.querySelectorAll<HTMLElement>('span.truncate'))) {
      expect(text.getBoundingClientRect().right).toBeLessThanOrEqual(
        titleCell.getBoundingClientRect().right,
      );
    }

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
  describe('read-only lists', () => {
    const LONG_URI =
      'https://partner-portal.regional-subsidiary.example.test/authentication/oidc/callback/primary';
    const NOT_CONFIGURED = /Not configured|openApp\.permission\.notConfigured/;

    async function render(apps: OpenApplicationOutputDto[]): Promise<HTMLElement> {
      await page.viewport(1800, 900);
      onTestFinished(() => page.viewport(1280, 800));
      viewport.next(desktop(true));
      const host = fixture.nativeElement as HTMLElement;
      host.style.display = 'block';
      host.style.width = '1760px';
      //#if (IncludeLocalization)
      TestBed.inject(TranslocoService).setTranslation(
        {
          popover: {
            permissionsTrigger: 'View all authorization capabilities of {{name}} ({{count}})',
          },
        },
        'openApp/en',
        { merge: true },
      );
      //#endif
      fixture.componentRef.setInput('applications', apps);
      const [table] = await fixture.getDeferBlocks();
      await table.render(DeferBlockState.Complete);
      fixture.autoDetectChanges();
      await fixture.whenStable();
      await expect
        .poll(() => host.querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
        .toBe('desktop');
      onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
      return host;
    }

    function cellOf(host: HTMLElement, column: string): HTMLElement {
      const index = Array.from(host.querySelectorAll('th')).findIndex((th) =>
        new RegExp(column).test(th.textContent ?? ''),
      );
      return host.querySelectorAll<HTMLElement>('tbody tr:first-child > td')[index];
    }

    async function openPanel(trigger: HTMLElement): Promise<HTMLElement> {
      await userEvent.click(trigger);
      await expect
        .poll(() => document.querySelector('[data-testid="readonly-list-panel"]'))
        .not.toBeNull();
      return document.querySelector<HTMLElement>('[data-testid="readonly-list-panel"]')!;
    }

    function panelLabels(panel: HTMLElement): string[] {
      return Array.from(panel.querySelectorAll('[data-testid="readonly-list"] li')).map((li) =>
        li.textContent!.trim(),
      );
    }

    // 摘要只从授权方式（gt:）推导；没有授权方式时写"未配置"，但端点与范围权限照样在面板里列全
    it('lists every permission even when none of them is a grant type', async () => {
      const permissions = ['ept:token', 'ept:introspection', 'scp:openid'];
      const host = await render([{ ...application('portal', 'Portal'), permissions }]);
      const cell = cellOf(host, 'Capabilities|colPermissions');

      const summary = cell.querySelector<HTMLElement>(
        '[data-testid="open-app-permissions-summary"]',
      )!;
      expect(summary.textContent).toMatch(NOT_CONFIGURED);
      expect(summary.textContent).toContain('(3)');
      const panel = await openPanel(
        cell.querySelector<HTMLElement>('[data-readonly-list-trigger]')!,
      );
      expect(panelLabels(panel)).toEqual(permissions);
    });

    it('keeps the grant summary and the total on the trigger', async () => {
      const permissions = ['gt:authorization_code', 'gt:refresh_token', 'ept:token'];
      const host = await render([{ ...application('portal', 'Portal'), permissions }]);
      const cell = cellOf(host, 'Capabilities|colPermissions');

      const summary = cell.querySelector<HTMLElement>(
        '[data-testid="open-app-permissions-summary"]',
      )!;
      expect(summary.textContent).toContain('authorization_code / refresh_token');
      expect(summary.textContent).toContain('(3)');
      expect(
        cell.querySelector('[data-readonly-list-trigger]')!.getAttribute('aria-label'),
      ).toMatch(/Portal/);
    });

    it('shows "not configured" without a panel when there are no permissions', async () => {
      const host = await render([application('portal', 'Portal')]);
      const cell = cellOf(host, 'Capabilities|colPermissions');

      expect(cell.textContent).toMatch(NOT_CONFIGURED);
      expect(cell.querySelector('[data-readonly-list-trigger]')).toBeNull();
    });

    // 只有一条地址时没有 "+N"，截断的地址也要能打开读全文
    it('opens the full address from a cell with a single long redirect URI', async () => {
      const host = await render([{ ...application('portal', 'Portal'), redirectUris: [LONG_URI] }]);
      const cell = cellOf(host, 'Redirect URIs|colRedirectUris');
      expect(cell.querySelector('[data-testid="open-app-redirect-uris-more"]')).toBeNull();
      // 长地址在单元格里截断，不压到右邻的安全列上
      const trigger = cell.querySelector<HTMLElement>('[data-readonly-list-trigger]')!;
      expect(trigger.getBoundingClientRect().right).toBeLessThanOrEqual(
        cell.getBoundingClientRect().right,
      );

      const panel = await openPanel(
        cell.querySelector<HTMLElement>('[data-readonly-list-trigger]')!,
      );
      expect(panelLabels(panel)).toEqual([LONG_URI]);
    });

    // 旧面板只列被隐藏的那几条；现在 "+N" 只是入口，面板列出全部地址
    it('counts the hidden redirect URIs on the trigger and lists all of them in the panel', async () => {
      const uris = [LONG_URI, 'https://a.example.test/cb', 'https://b.example.test/cb'];
      const host = await render([{ ...application('portal', 'Portal'), redirectUris: uris }]);
      const cell = cellOf(host, 'Redirect URIs|colRedirectUris');

      expect(
        cell.querySelector('[data-testid="open-app-redirect-uris-more"]')!.textContent!.trim(),
      ).toBe('+2');
      const panel = await openPanel(
        cell.querySelector<HTMLElement>('[data-readonly-list-trigger]')!,
      );
      expect(panelLabels(panel)).toEqual(uris);
    });
  });

  // 最窄一档收起全部二、三级列，按列定义顺序进详情；详情给完整时刻，安全标记两项都给出
  it('lists the collapsed data columns in definition order with full content', async () => {
    await page.viewport(390, 844);
    onTestFinished(() => page.viewport(1280, 800));
    const host = fixture.nativeElement as HTMLElement;
    host.style.display = 'block';
    host.style.width = '356px';
    fixture.componentRef.setInput('applications', [
      { ...application('portal', 'Portal'), creationTime: '2026-01-01T08:09:07Z' },
    ]);
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);
    fixture.autoDetectChanges();
    await expect
      .poll(() => host.querySelector('[data-table-fit]')?.getAttribute('data-table-fit'))
      .toBe('mobile');
    host.querySelector('tbody ng-icon[name="lucideChevronRight"]')!.closest('button')!.click();
    await fixture.whenStable();

    const detail = host.querySelector<HTMLElement>('[data-testid="open-app-detail"]')!;
    const columns = Array.from(detail.querySelectorAll('[data-detail-column]')).map((dd) =>
      dd.getAttribute('data-detail-column'),
    );
    expect(columns).toEqual(['permissions', 'redirectUris', 'security', 'creationTime']);
    const value = (id: string) =>
      detail.querySelector<HTMLElement>(`[data-detail-column="${id}"]`)!;
    expect(value('creationTime').textContent).toMatch(/:09:07/);
    const security = Array.from(value('security').querySelectorAll<HTMLElement>('[hlmBadge]'));
    expect(security).toHaveLength(2);
    expect(security.every((badge) => getComputedStyle(badge).display !== 'none')).toBe(true);
  });
});
