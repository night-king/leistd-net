//#if (LocalIdentity)
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { OpenApplicationEditDialog } from './open-application-edit-dialog';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import {
  CreateOpenApplicationInputDto,
  OpenApplicationOutputDto,
  UpdateOpenApplicationInputDto,
} from '../../../../dtos/open-application.dto';

/**
 * 下拉触发器与选项文案一致：不传 `itemToString` 时触发器显示原始值，只有点开、选中再读触发器
 * 才暴露得出来。
 */
@Component({
  imports: [OpenApplicationEditDialog],
  template: `
    <app-open-application-edit-dialog
      [(visible)]="visible"
      [application]="application()"
      (saved)="saved.push($event)"
    />
  `,
})
class HostComponent {
  readonly visible = signal(true);
  readonly application = signal<OpenApplicationOutputDto | null>(null);
  readonly saved: (CreateOpenApplicationInputDto | UpdateOpenApplicationInputDto)[] = [];
}

function registered(sessionBound: boolean | null): OpenApplicationOutputDto {
  return {
    id: 'bff',
    clientId: 'bff',
    applicationType: 'web',
    clientType: 'confidential',
    redirectUris: ['https://bff.example.test/signin-oidc'],
    postLogoutRedirectUris: [],
    permissions: ['ept:authorization', 'ept:token', 'gt:authorization_code', 'rst:code'],
    requirements: [],
    settings: {},
    properties: {},
    hasClientSecret: true,
    sessionBound,
    creationTime: '2026-05-01T09:00:00Z',
  };
}

describe('OpenApplicationEditDialog', () => {
  let fixture: ComponentFixture<HostComponent>;

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
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  async function selectFirstOtherOption(triggerId: string): Promise<{
    optionText: string;
    triggerText: string;
  }> {
    const trigger = document.getElementById(triggerId);
    expect(trigger, `${triggerId} should be rendered`).not.toBeNull();

    trigger!.click();
    await fixture.whenStable();

    const options = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]'));
    expect(options.length, `${triggerId} should offer options`).toBeGreaterThan(1);

    const option =
      options.find((item) => item.getAttribute('aria-selected') !== 'true') ?? options[0];
    const optionText = option.textContent?.trim() ?? '';

    option.click();
    await fixture.whenStable();

    return {
      optionText,
      triggerText: document.getElementById(triggerId)?.textContent?.trim() ?? '',
    };
  }

  function dialog(): OpenApplicationEditDialog {
    return fixture.debugElement.children[0].componentInstance as OpenApplicationEditDialog;
  }

  async function edit(application: OpenApplicationOutputDto): Promise<void> {
    fixture.componentInstance.application.set(application);
    await fixture.whenStable();
  }

  async function typeClientId(value: string): Promise<void> {
    const input = document.getElementById('application-client-id') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function toggleSessionBound(): Promise<void> {
    document.getElementById('application-session-bound')!.click();
    await fixture.whenStable();
  }

  // 开关的标签经 for 关联到开关本身（点标签即切换、读屏读出名称），说明与它同属一个字段
  it('labels and describes the session binding switch', () => {
    const label = document.querySelector('label[for="application-session-bound"]');
    expect(label).not.toBeNull();
    const field = label!.closest('[hlmField]')!;
    expect(field.contains(document.getElementById('application-session-bound'))).toBe(true);

    const description = field.querySelector('[hlmFieldDescription]')?.textContent?.trim();
    //#if (IncludeLocalization)
    // 测试不装词条，缺失的键原样渲染：正好能看出读的是哪一条
    expect(description).toBe('openApp.sessionBound.hint');
    //#else
    expect(description).toMatch(/^Authorization codes and refresh tokens stop working/);
    //#endif
    // 读屏软件聚焦开关时连同说明一起读
    const describedBy = field.querySelector('[role="switch"]')?.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)?.textContent?.trim()).toBe(description);
  });

  it('binds new browser applications to the sign-in session by default', async () => {
    await typeClientId('spa');
    dialog().save();

    expect(fixture.componentInstance.saved).toEqual([
      expect.objectContaining({ clientId: 'spa', sessionBound: true }),
    ]);
  });

  it('keeps the registered choice when editing instead of resetting it', async () => {
    await edit(registered(false));
    dialog().save();

    expect(fixture.componentInstance.saved).toEqual([
      expect.objectContaining({ sessionBound: false }),
    ]);
  });

  it('requires an explicit choice for applications registered before session binding', async () => {
    await edit(registered(null));
    expect(document.querySelector('[data-testid="session-bound-unset"]')).not.toBeNull();

    dialog().save();
    expect(fixture.componentInstance.saved).toEqual([]);

    await toggleSessionBound();
    expect(document.querySelector('[data-testid="session-bound-unset"]')).toBeNull();
    dialog().save();
    expect(fixture.componentInstance.saved).toEqual([
      expect.objectContaining({ sessionBound: true }),
    ]);
  });

  it('does not bind desktop or machine clients from their templates', async () => {
    dialog().applyTemplate('desktop');
    await fixture.whenStable();
    dialog().save();
    dialog().applyTemplate('service');
    await typeClientId('machine');
    dialog().save();

    expect(fixture.componentInstance.saved).toEqual([
      expect.objectContaining({ applicationType: 'native', sessionBound: false }),
      expect.objectContaining({ applicationType: 'service', sessionBound: false }),
    ]);
  });

  for (const triggerId of ['application-type', 'application-client-type']) {
    it(`shows the picked option's own label in ${triggerId}`, async () => {
      const { optionText, triggerText } = await selectFirstOtherOption(triggerId);

      expect(optionText).not.toBe('');
      expect(triggerText).toBe(optionText);
    });
  }
});
//#endif
