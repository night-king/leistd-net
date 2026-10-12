import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
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

/**
 * 失败原因的取法：后端按请求语言渲染的 `failureMessage` 优先，取不到时回落。
 * 回落写错的症状是界面显示裸码或空白，不会报错。
 */
describe('OperationRecordTable failure reasons', () => {
  let fixture: ComponentFixture<OperationRecordTable>;

  function failed(failure: Partial<OperationRecordOutputDto>): OperationRecordOutputDto {
    return {
      id: 'r-1',
      action: 'user.created',
      targetId: 't-1',
      authorizationBasis: 'App.Users.Create',
      outcome: 'failed',
      creationTime: '2026-09-20T10:00:00Z',
      ...failure,
    };
  }

  function reasonOf(record: OperationRecordOutputDto): string | null | undefined {
    fixture.componentRef.setInput('records', [record]);
    fixture.detectChanges();
    return fixture.componentInstance['recordTexts']().get(record.id)?.failure;
  }

  beforeEach(async () => {
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
    fixture = TestBed.createComponent(OperationRecordTable);
  });

  it('shows the reason the backend rendered in the request language', () => {
    expect(
      reasonOf(
        failed({ failureCode: 'User:EmailTaken', failureMessage: 'Email is already in use.' }),
      ),
    ).toBe('Email is already in use.');
  });

  it('falls back to the raw code when the backend has no text for it', () => {
    expect(reasonOf(failed({ failureCode: 'Order:Unknown' }))).toBe('Order:Unknown');
  });

  it('has no reason for a record without a failure code', () => {
    expect(reasonOf(failed({ failureMessage: 'ignored' }))).toBeNull();
  });
  //#if (IncludeLocalization)

  // 审计专用的措辞由后端的 {码}:Record 词条给出，前端不再按码备词条、也不覆盖后端文案
  it('does not override the backend message with a front-end entry', () => {
    TestBed.inject(TranslocoService).setTranslation(
      {
        operationRecords: {
          failures: { Auth_InvalidCredentials: 'Wrong password ({{attempts}} attempts)' },
        },
      },
      'en',
    );

    expect(
      reasonOf(
        failed({
          failureCode: 'Auth:InvalidCredentials',
          failureData: '{"attempts":5,"windowMinutes":15}',
          failureMessage:
            'Incorrect username or password (failed attempts in the last 15 minutes: 5)',
        }),
      ),
    ).toBe('Incorrect username or password (failed attempts in the last 15 minutes: 5)');
  });
  //#else

  // 不含本地化时后端不渲染，常见的码由内置英文句子兜底
  it('uses the built-in English sentence when the backend renders none', () => {
    expect(reasonOf(failed({ failureCode: 'Error:Forbidden' }))).toBe(
      'Not allowed to perform this action.',
    );
  });

  // 内置句子与后端 {码}:Record 同一写法，用记录的参数填占位符；缺的参数原样保留
  it('fills the built-in sentence with the recorded parameters', () => {
    expect(
      reasonOf(
        failed({
          failureCode: 'Auth:InvalidCredentials',
          failureData: '{"attempts":5,"windowMinutes":15}',
        }),
      ),
    ).toBe('Incorrect username or password (failed attempts in the last 15 minutes: 5)');
    expect(
      reasonOf(
        failed({ failureCode: 'Auth:UserTemporarilyLockedOut', failureData: '{"minutes":30}' }),
      ),
    ).toBe('Locked out for 30 minutes after {maxFailedAttempts} failed sign-in attempts');
  });
  //#endif
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
