import { operationFailureReason, operationRecordActor } from './operation-record-texts';
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';

/**
 * 失败原因的取法：后端按请求语言渲染的 `failureMessage` 优先，取不到时回落；操作人缺失时只在服务端判定
 * 自证类动作时回落到目标名。回落写错的症状是界面显示裸码或空白，不会报错。
 */
describe('operation record texts', () => {
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

  it('shows the reason the backend rendered in the request language', () => {
    expect(
      operationFailureReason(
        failed({ failureCode: 'User:EmailTaken', failureMessage: 'Email is already in use.' }),
      ),
    ).toBe('Email is already in use.');
  });

  it('falls back to the raw code when the backend has no text for it', () => {
    expect(operationFailureReason(failed({ failureCode: 'Order:Unknown' }))).toBe('Order:Unknown');
  });

  it('has no reason for a record without a failure code', () => {
    expect(operationFailureReason(failed({ failureMessage: 'ignored' }))).toBeNull();
  });
  //#if (!IncludeLocalization)

  // 不含本地化时后端不渲染，常见的码由内置英文句子兜底
  it('uses the built-in English sentence when the backend renders none', () => {
    expect(operationFailureReason(failed({ failureCode: 'Error:Forbidden' }))).toBe(
      'Not allowed to perform this action.',
    );
  });

  // 内置句子与后端 {码}:Record 同一写法，用记录的参数填占位符；缺的参数原样保留
  it('fills the built-in sentence with the recorded parameters', () => {
    expect(
      operationFailureReason(
        failed({
          failureCode: 'Auth:InvalidCredentials',
          failureData: '{"attempts":5,"windowMinutes":15}',
        }),
      ),
    ).toBe('Incorrect username or password (failed attempts in the last 15 minutes: 5)');
    expect(
      operationFailureReason(
        failed({ failureCode: 'Auth:UserTemporarilyLockedOut', failureData: '{"minutes":30}' }),
      ),
    ).toBe('Locked out for 30 minutes after {maxFailedAttempts} failed sign-in attempts');
  });
  //#endif

  it('falls back to the target name only when the server marks the actor as the target', () => {
    const anonymous = failed({ targetName: 'alice' });

    expect(operationRecordActor(anonymous)).toBeUndefined();
    expect(operationRecordActor({ ...anonymous, actorIsTarget: true })).toBe('alice');
    expect(operationRecordActor({ ...anonymous, actorId: 'u-1', actorIsTarget: true })).toBe('u-1');
  });
});
