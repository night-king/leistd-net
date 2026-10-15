import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

//#if (ExternalLogin && IncludeMultiTenancy)
import { TenantContextService } from '../../../core/services/tenant-context-service';
//#endif
import {
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../../shared/dtos/account-security.dto';
//#if (ExternalLogin)
import { SessionLoginOutputDto, UserOutputDto } from '../../../shared/dtos/auth.dto';
//#else
import { UserOutputDto } from '../../../shared/dtos/auth.dto';
//#endif
import {
  ChangePasswordInputDto,
  //#if (ExternalLogin)
  ExternalLoginProvidersOutputDto,
  ExternalLoginsOutputDto,
  //#endif
  RegisterInputDto,
  SetAvatarInputDto,
  UpdateCurrentUserInputDto,
//#if (Email)
  EmailVerificationInputDto,
//#endif
//#if (Email)
  SecurityConfigOutputDto,
//#endif
  CaptchaOutputDto,
//#if (Email)
  SendEmailCodeInputDto,
//#endif
//#if (Email)
  EmailVerificationChallengeOutputDto,
//#endif
  BeginTwoFactorSetupInputDto,
  DisableTwoFactorInputDto,
  TwoFactorLoginInputDto,
  TwoFactorRecoveryCodesOutputDto,
  TwoFactorSetupOutputDto,
  //#if (OpenIddictServer)
  LogoutConfirmationOutputDto,
  //#endif
} from '../dtos/account.dto';
//#if (IncludeMultiTenancy)
import { TenantByHostOutputDto } from '../dtos/tenant-by-host.dto';
//#endif

@Injectable({ providedIn: 'root' })
export class AccountService {
  private http = inject(HttpClient);
  //#if (ExternalLogin && IncludeMultiTenancy)
  private readonly tenantContext = inject(TenantContextService);
  //#endif
  //#if (Email)

  getSecurityConfig(): Observable<SecurityConfigOutputDto> {
    return this.http.get<SecurityConfigOutputDto>('/api/v1/auth/security-config');
  }
  //#endif
  //#if (OpenIddictServer)

  /** 依赖方发起的退出需要确认时，核对确认凭据并取得确认表单的防伪令牌。 */
  getLogoutConfirmation(
    requestUri: string,
    confirmation: string,
  ): Observable<LogoutConfirmationOutputDto> {
    return this.http.get<LogoutConfirmationOutputDto>('/api/v1/auth/logout-confirmation', {
      params: { request_uri: requestUri, confirmation },
    });
  }
  //#endif
  //#if (IncludeMultiTenancy)

  /** 按当前主机名探测租户（匿名），返回三档定案结果（见 {@link TenantByHostOutputDto}）。 */
  getTenantByHost(): Observable<TenantByHostOutputDto> {
    return this.http.get<TenantByHostOutputDto>('/api/v1/tenants/by-host');
  }
  //#endif

  getCaptcha(): Observable<CaptchaOutputDto> {
    return this.http.get<CaptchaOutputDto>('/api/v1/auth/captcha');
  }
  //#if (Email)

  sendEmailCode(data: SendEmailCodeInputDto): Observable<EmailVerificationChallengeOutputDto> {
    return this.http.post<EmailVerificationChallengeOutputDto>(
      '/api/v1/auth/send-email-code',
      data,
    );
  }
  //#endif

  register(data: RegisterInputDto): Observable<void> {
    return this.http.post('/api/v1/auth/register', data).pipe(map(() => undefined));
  }

  /** 更新自己的资料，返回服务端保存后的资料；写回当前用户由调用方负责。 */
  updateCurrentUser(data: UpdateCurrentUserInputDto): Observable<UserOutputDto> {
    return this.http.put<UserOutputDto>('/api/v1/auth/me', data);
  }

  /** 设置或清除自己的头像，返回的资料里头像地址带新的版本号；写回当前用户由调用方负责。 */
  setAvatar(data: SetAvatarInputDto): Observable<UserOutputDto> {
    return this.http.put<UserOutputDto>('/api/v1/auth/me/avatar', data);
  }
//#if (Email)

  sendCurrentEmailCode(): Observable<EmailVerificationChallengeOutputDto> {
    return this.http.post<EmailVerificationChallengeOutputDto>(
      '/api/v1/auth/me/email-verification',
      {},
    );
  }
//#endif
//#if (Email)

  /** 用验证码确认自己当前的邮箱，返回确认后的资料；写回当前用户由调用方负责。 */
  confirmCurrentEmail(data: EmailVerificationInputDto): Observable<UserOutputDto> {
    return this.http.post<UserOutputDto>('/api/v1/auth/me/email-verification/confirm', data);
  }
//#endif

  changePassword(data: ChangePasswordInputDto): Observable<void> {
    return this.http.post('/api/v1/auth/change-password', data).pipe(map(() => undefined));
  }

  /** 登录第二步：提交验证码或恢复码，通过后下发会话。 */
  completeTwoFactorLogin(data: TwoFactorLoginInputDto): Observable<void> {
    return this.http.post('/api/v1/auth/two-factor', data).pipe(map(() => undefined));
  }

  getTwoFactorStatus(): Observable<TwoFactorStatusOutputDto> {
    return this.http.get<TwoFactorStatusOutputDto>('/api/v1/auth/me/two-factor');
  }

  /** 开始设置：生成待确认的密钥（几分钟内有效）；已启用时是换绑。 */
  beginTwoFactorSetup(
    input: BeginTwoFactorSetupInputDto = {},
  ): Observable<TwoFactorSetupOutputDto> {
    return this.http.post<TwoFactorSetupOutputDto>('/api/v1/auth/me/two-factor/setup', input);
  }

  /** 用新验证器上的验证码确认这一次设置（首次启用或换绑），返回一次性展示的恢复码。 */
  enableTwoFactor(setupId: string, code: string): Observable<TwoFactorRecoveryCodesOutputDto> {
    return this.http.post<TwoFactorRecoveryCodesOutputDto>('/api/v1/auth/me/two-factor/enable', {
      setupId,
      code,
    });
  }

  disableTwoFactor(data: DisableTwoFactorInputDto): Observable<void> {
    return this.http.post('/api/v1/auth/me/two-factor/disable', data).pipe(map(() => undefined));
  }

  regenerateRecoveryCodes(code: string): Observable<TwoFactorRecoveryCodesOutputDto> {
    return this.http.post<TwoFactorRecoveryCodesOutputDto>(
      '/api/v1/auth/me/two-factor/recovery-codes',
      { code },
    );
  }

  /** 自己仍然有效的登录会话，当前设备在前。 */
  getSessions(): Observable<UserSessionOutputDto[]> {
    return this.http.get<UserSessionOutputDto[]>('/api/v1/auth/me/sessions');
  }

  revokeSession(id: string): Observable<void> {
    return this.http.delete(`/api/v1/auth/me/sessions/${id}`).pipe(map(() => undefined));
  }

  /** 让除当前设备以外的全部设备退出登录，返回退出的个数。 */
  revokeOtherSessions(): Observable<number> {
    return this.http.post<number>('/api/v1/auth/me/sessions/revoke-others', {});
  }
  //#if (ExternalLogin)

  /** 部署已配置的外部登录提供商；登录页据此决定显示哪些入口。 */
  getExternalLoginProviders(): Observable<ExternalLoginProvidersOutputDto> {
    return this.http.get<ExternalLoginProvidersOutputDto>('/api/v1/external-auth/providers');
  }

  getExternalLogins(): Observable<ExternalLoginsOutputDto> {
    return this.http.get<ExternalLoginsOutputDto>('/api/v1/external-auth/links');
  }

  /** "绑定外部账号"的授权地址（与登录用的互不通用）。 */
  getExternalLinkUrl(provider: string): string {
    return `/api/v1/external-auth/${encodeURIComponent(provider)}/link/challenge`;
  }

  /** 外部授权回来后完成绑定。 */
  linkExternalLogin(provider: string): Observable<void> {
    return this.http
      .post(`/api/v1/external-auth/${provider}/link/complete`, {})
      .pipe(map(() => undefined));
  }

  unlinkExternalLogin(id: string): Observable<void> {
    return this.http.delete(`/api/v1/external-auth/links/${id}`).pipe(map(() => undefined));
  }

  getExternalLoginUrl(provider: 'github' | 'google', returnUrl?: string | null): string {
//#if (IncludeMultiTenancy)
    const tenant = this.tenantContext.current()?.key;
//#endif
    const query = new URLSearchParams();
//#if (IncludeMultiTenancy)
    if (tenant) query.set('tenant', tenant);
//#endif
    if (returnUrl) query.set('returnUrl', returnUrl);
    return `/api/v1/external-auth/${provider}/challenge${query.size ? `?${query}` : ''}`;
  }

  /** 外部登录回调；已启用两步验证时不下发会话，返回第二步凭据。 */
  externalLoginCallback(provider: string): Observable<SessionLoginOutputDto> {
    return this.http.post<SessionLoginOutputDto>(`/api/v1/external-auth/${provider}/complete`, {});
  }
  //#endif
}
