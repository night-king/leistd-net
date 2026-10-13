import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { Translation, TranslocoService } from '@jsverse/transloco';
//#endif
//#if (IncludeLocalization)
import { of, Subject } from 'rxjs';
//#else
import { of } from 'rxjs';
//#endif

import { OperationRecordTable } from './operation-record-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';

/** 行展开：追查字段由共用的记录详情给出，宽屏下已显示的操作人与时间不再重复。 */
describe('OperationRecordTable rows', () => {
  it('shows the trace fields of a record when its row is expanded', async () => {
    await TestBed.configureTestingModule({
      imports: [OperationRecordTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
        },
      ],
    }).compileComponents();
    // 先建好设置上下文：它依赖的语言服务在首次渲染途中才创建会让结构指令重入
    TestBed.inject(SettingContextService);
    const fixture: ComponentFixture<OperationRecordTable> =
      TestBed.createComponent(OperationRecordTable);
    fixture.componentRef.setInput('records', [
      {
        id: 'r-1',
        action: 'user.created',
        targetId: 'target-42',
        authorizationBasis: 'App.Users.Create',
        outcome: 'failed',
        failureCode: 'Order:Unknown',
        correlationId: 'trace-7',
        creationTime: '2026-09-20T10:00:00Z',
      } satisfies OperationRecordOutputDto,
    ]);
    fixture.detectChanges();
    const [table] = await fixture.getDeferBlocks();
    await table.render(DeferBlockState.Complete);

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('button[aria-expanded]')!
      .click();
    fixture.detectChanges();

    const detail = (fixture.nativeElement as HTMLElement).querySelector(
      'app-operation-record-detail',
    );
    expect(detail?.textContent).toContain('target-42');
    expect(detail?.textContent).toContain('Order:Unknown');
    expect(detail?.textContent).toContain('trace-7');
  });
});
//#if (IncludeLocalization)

/** 操作句子取哪条词条随词条到达更新：首次渲染时词条可能未到，同步判断会把"未登记"缓存下来。 */
describe('OperationRecordTable action sentences', () => {
  it('switches from the raw code to the registered sentence once translations arrive', () => {
    vi.spyOn(navigator, 'languages', 'get').mockReturnValue(['en']);
    const translations = new Subject<Translation>();
    TestBed.configureTestingModule({
      imports: [OperationRecordTable],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        ...provideTranslocoTesting(['en'], { getTranslation: () => translations }),
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
        },
      ],
    });
    TestBed.inject(SettingContextService);
    const fixture = TestBed.createComponent(OperationRecordTable);
    const record = (id: string, action: string): OperationRecordOutputDto => ({
      id,
      action,
      targetId: 't-1',
      targetName: 'alice',
      authorizationBasis: 'App.Users.Create',
      outcome: 'succeeded',
      creationTime: '2026-09-20T10:00:00Z',
    });
    fixture.componentRef.setInput('records', [
      record('r-1', 'user.created'),
      record('r-2', 'order.shipped'),
    ]);
    const sentenceOf = (id: string) => fixture.componentInstance['recordTexts']().get(id)!.sentence;
    expect(sentenceOf('r-1').key).toBeNull();

    translations.next({
      operationRecords: { actions: { 'user.created': 'Created user {{target}}' } },
    });
    translations.complete();

    const sentence = sentenceOf('r-1');
    expect(sentence.key).not.toBeNull();
    expect(TestBed.inject(TranslocoService).translate(sentence.key!, sentence.params)).toBe(
      'Created user alice',
    );
    // 未登记的动作码仍原样显示：造一个假句子比显示机器码更糟
    expect(sentenceOf('r-2')).toEqual({ key: null, params: {}, text: 'order.shipped' });
  });
});
//#endif
