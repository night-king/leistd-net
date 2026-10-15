import {
  HttpErrorResponse,
  HttpInterceptorFn,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { catchError, throwError } from 'rxjs';

import { PermissionGrantDialog } from './permission-grant-dialog';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import {
  expectTouchTarget,
  setCoarsePointer,
  tapAt,
} from '../../../../shared/utils/coarse-pointer.testing';

/**
 * 权限弹窗的加载竞态：弹窗供角色与用户共用，晚到的响应若落到新主体，两边版本同为 0 时
 * 乐观并发也拦不住，须由前端自己守住。
 */
@Component({
  imports: [PermissionGrantDialog],
  template: ` <app-permission-grant-dialog [(open)]="open" [roleId]="roleId()" /> `,
})
class HostComponent {
  readonly open = signal(true);
  readonly roleId = signal<string | null>('role-a');
}

const DEFINITIONS_URL = '/api/v1/permissions/definitions';

/** 与应用的错误拦截器一样把失败归一化为 `ApplicationHttpError`，弹窗按其中的错误码分支。 */
const normalizeErrors: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((error: unknown) =>
      throwError(() =>
        error instanceof HttpErrorResponse ? ApplicationHttpError.from(error) : error,
      ),
    ),
  );

function definitions() {
  return [
    {
      name: 'App',
      displayName: 'App',
      permissions: [
        { name: 'App.Users', displayName: 'Users', parentName: undefined, children: [] },
      ],
    },
  ];
}

/** 手风琴组标题的展开按钮。 */
function groupTrigger(): HTMLElement {
  const trigger = document.querySelector<HTMLElement>('hlm-accordion-trigger button');
  expect(trigger, 'group trigger should be rendered').not.toBeNull();
  return trigger!;
}

function grants(providerKey: string, granted: boolean) {
  return {
    providerName: 'Role',
    providerKey,
    version: 7,
    grants: [{ name: 'App.Users', granted }],
  };
}

describe('PermissionGrantDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
                provideZonelessChangeDetection(),
                provideHttpClient(withInterceptors([normalizeErrors])),
                provideHttpClientTesting(),
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(),
                //#endif
            ],
    });
    fixture = TestBed.createComponent(HostComponent);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  function dialog(): PermissionGrantDialog {
    return fixture.debugElement.children[0].componentInstance as PermissionGrantDialog;
  }

  it('cancels the in-flight load when the subject changes', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    const first = httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a');

    fixture.componentInstance.roleId.set('role-b');
    await fixture.whenStable();

    // 切换主体后上一次的授予请求被取消，新主体重新走完整加载。
    expect(first.cancelled).toBe(true);
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-b').flush(grants('role-b', true));
    await fixture.whenStable();

    expect(dialog().isGranted('App.Users')).toBe(true);
    expect(dialog().version()).toBe(7);
  });

  it('ignores a response whose subject does not match the request', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());

    // 服务端/Mock 返回了另一个主体的数据：宁可不渲染，也不能当成本主体的授予。
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-b', false));
    await fixture.whenStable();

    expect(dialog().isGranted('App.Users')).toBe(false);
    expect(dialog().version()).toBe(0);
  });

  it('drops the previous subject state when loading the next one fails', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();
    expect(dialog().isGranted('App.Users')).toBe(true);

    fixture.componentInstance.roleId.set('role-b');
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting
      .expectOne('/api/v1/permissions/grants/roles/role-b')
      .flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();

    // 加载失败后界面不得留着 A 的状态与版本，否则保存会把 A 的授予写给 B；
    // 两边版本恰好相同时乐观并发也拦不住。
    expect(dialog().isGranted('App.Users')).toBe(false);
    expect(dialog().version()).toBe(0);
    expect(dialog().canSave()).toBe(false);
  });

  it('shows the load failure with retry instead of an empty list, and retry reloads', async () => {
    const errorToast = vi.spyOn(toast, 'error').mockImplementation(() => '');
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting
      .expectOne('/api/v1/permissions/grants/roles/role-a')
      .flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    await fixture.whenStable();

    // 失败要说出来并给重试：不能停在一张看似"没有任何权限"的空列表上
    expect(dialog().loadError()).not.toBeNull();
    expect(errorToast).not.toHaveBeenCalled();
    expect(document.querySelector('hlm-accordion-trigger')).toBeNull();
    const retry = document.querySelector<HTMLButtonElement>('[data-testid="permissions-retry"]');
    expect(retry, 'retry button should be rendered').not.toBeNull();

    retry!.click();
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();

    expect(dialog().loadError()).toBeNull();
    expect(dialog().isGranted('App.Users')).toBe(true);
    expect(dialog().canSave()).toBe(true);
    expect(document.querySelector('[data-testid="permissions-retry"]')).toBeNull();
  });

  it('blocks saving until the current subject has loaded successfully', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());

    // 响应归属不符会被丢弃，此时保存必须不可用，也不得发出任何写请求。
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-b', true));
    await fixture.whenStable();

    expect(dialog().canSave()).toBe(false);
    dialog().onSave();
    httpTesting.expectNone('/api/v1/permissions/grants/roles/role-a');
  });

  it('reloads the subject after a concurrency conflict', async () => {
    const errorToast = vi.spyOn(toast, 'error').mockImplementation(() => '');
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();

    dialog().onSave();
    httpTesting
      .expectOne({ method: 'PUT', url: '/api/v1/permissions/grants/roles/role-a' })
      .flush(
        { code: 'Permission:ConcurrencyConflict', detail: 'conflict' },
        { status: 409, statusText: 'Conflict' },
      );
    await fixture.whenStable();

    expect(errorToast).toHaveBeenCalledOnce();
    expect(errorToast).not.toHaveBeenCalledWith('conflict');
    expect(dialog().saving()).toBe(false);

    // 409 后必须真的重新拉取，否则用户只能拿着旧版本反复重试、反复 409。
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting
      .expectOne({ method: 'GET', url: '/api/v1/permissions/grants/roles/role-a' })
      .flush(grants('role-a', false));
    await fixture.whenStable();

    expect(dialog().isGranted('App.Users')).toBe(false);
  });

  it('only reports a 409 whose code is not a concurrency conflict, without reloading', async () => {
    const errorToast = vi.spyOn(toast, 'error').mockImplementation(() => '');
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();

    dialog().onSave();
    httpTesting
      .expectOne({ method: 'PUT', url: '/api/v1/permissions/grants/roles/role-a' })
      .flush(
        { code: 'Permission:SomethingElse', detail: 'Other conflict' },
        { status: 409, statusText: 'Conflict' },
      );
    await fixture.whenStable();

    // 只按服务端文案提示一次；不重新加载，界面上的勾选与版本原样保留，可以改后再存。
    expect(errorToast).toHaveBeenCalledExactlyOnceWith('Other conflict');
    httpTesting.expectNone(DEFINITIONS_URL);
    expect(dialog().isGranted('App.Users')).toBe(true);
    expect(dialog().version()).toBe(7);
    expect(dialog().saving()).toBe(false);
  });

  it('keeps the spinner up until the next subject has loaded', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a');

    fixture.componentInstance.roleId.set('role-b');
    await fixture.whenStable();

    // switchMap 退订上一轮时它的 finalize 照样会跑。若 loading 在 switchMap 之前置位，
    // 这一下就把它打回 false —— B 还在飞，界面已经显示成加载完成。
    expect(dialog().loading()).toBe(true);

    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-b').flush(grants('role-b', true));
    await fixture.whenStable();

    expect(dialog().loading()).toBe(false);
  });

  it('writes the model when a rendered checkbox is clicked', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();

    // 走真实 DOM 点击而不是调组件方法：复选框的输出名写错时组件方法照样能过，
    // 界面却只改了控件自身的内部状态，重新加载就"复原"——只有点击才暴露得出来。
    const checkbox = document.getElementById('App.Users');
    expect(checkbox, 'permission checkbox should be rendered').not.toBeNull();

    checkbox?.click();
    await fixture.whenStable();

    expect(dialog().isGranted('App.Users')).toBe(false);
  });

  // 单行权限只有约 37px 高：触屏下行要撑高，相邻两行复选框的命中区才不互相覆盖
  it('keeps the touch targets of adjacent single-line permissions apart on coarse pointers', async () => {
    await setCoarsePointer(true);
    onTestFinished(() => setCoarsePointer(false));
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush([
      {
        name: 'App',
        displayName: 'App',
        permissions: [
          { name: 'App.Users', displayName: 'Users', parentName: undefined, children: [] },
          { name: 'App.Roles', displayName: 'Roles', parentName: undefined, children: [] },
        ],
      },
    ]);
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();
    // 弹窗打开时有缩放动画，量尺寸前等它结束
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    const users = document.getElementById('App.Users')!;
    const roles = document.getElementById('App.Roles')!;

    expectTouchTarget(users);
    expectTouchTarget(roles);

    // 视觉方框之外、44px 命中区之内：上一行的下沿与下一行的上沿
    await tapAt(users, 0, 20);
    await fixture.whenStable();
    expect(dialog().isGranted('App.Users')).toBe(false);
    expect(dialog().isGranted('App.Roles')).toBe(false);

    await tapAt(roles, -20, -20);
    await fixture.whenStable();
    expect(dialog().isGranted('App.Roles')).toBe(true);
    expect(dialog().isGranted('App.Users')).toBe(false);
  });

  it('keeps the group expanded after its last grant is cleared', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();
    expect(dialog().isExpanded('App')).toBe(true);

    // 展开状态一旦由"本组已有授予"算出来，取消最后一个勾就会顺手把整组折叠掉，
    // 用户只是想改一个勾，界面却塌了。
    document.getElementById('App.Users')?.click();
    await fixture.whenStable();

    expect(dialog().isGranted('App.Users')).toBe(false);
    expect(dialog().isExpanded('App')).toBe(true);
  });

  it('reopens a manually collapsed group when the search matches it', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a').flush(grants('role-a', true));
    await fixture.whenStable();
    expect(groupTrigger().getAttribute('aria-expanded')).toBe('true');

    groupTrigger().click();
    await fixture.whenStable();
    expect(groupTrigger().getAttribute('aria-expanded')).toBe('false');

    // 不把手动折叠写回状态的话，isExpanded() 本来就是 true、输入值没有变化，
    // 手风琴不会重新打开它已经关掉的组，搜索命中项就一直藏着。
    dialog().onKeywordChange('Users');
    await fixture.whenStable();

    expect(groupTrigger().getAttribute('aria-expanded')).toBe('true');
  });

  it('cancels the in-flight load when the dialog is destroyed', async () => {
    await fixture.whenStable();
    httpTesting.expectOne(DEFINITIONS_URL).flush(definitions());
    const pending = httpTesting.expectOne('/api/v1/permissions/grants/roles/role-a');

    fixture.destroy();

    // 销毁即退订：请求被取消，晚到的响应没有任何落地路径。
    expect(pending.cancelled).toBe(true);
    expect(() => pending.flush(grants('role-a', true))).toThrowError(
      /Cannot flush a cancelled request/,
    );
  });
});
