import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, lastValueFrom, tap } from 'rxjs';

//#if (IncludeNotifications || IncludeRealTime)
import { SignalRService } from './signalr-service';
//#endif
//#if (RemoteTokenAuth)
//#if (IncludeMultiTenancy)
import { TenantContextService } from './tenant-context-service';
//#endif
//#endif
//#if (LocalIdentity)
import { LoginInputDto, SessionLoginOutputDto, UserOutputDto } from '../../shared/dtos/auth.dto';
//#else
import { UserOutputDto } from '../../shared/dtos/auth.dto';
//#endif
import { User } from '../../shared/models/user.model';
import { SILENT_AUTH } from '../interceptors/http-context-tokens';
//#if (RemoteTokenAuth)
import { MOCKED_URL } from '../mock/mocked-url';
//#endif

@Injectable({ providedIn: 'root' })
export class AuthService {
  //#if (LocalIdentity)
  static readonly loginUrl = '/api/v1/auth/session-login';

  //#endif
  private readonly http = inject(HttpClient);
  //#if (RemoteTokenAuth)
//#if (IncludeMultiTenancy)
  private readonly tenantContext = inject(TenantContextService);
//#endif
  //#endif
  //#if (IncludeNotifications || IncludeRealTime)
  private readonly signalR = inject(SignalRService);
  //#endif
  //#if (RemoteTokenAuth)
  private readonly isMockedUrl = inject(MOCKED_URL);
  //#endif

  private readonly _currentUser = signal<User | null>(null);
  public readonly currentUser = this._currentUser.asReadonly();

  isAuthenticated(): boolean {
    return this._currentUser() !== null;
  }
  //#if (LocalIdentity)

  /** 账号密码登录；已启用两步验证时不下发会话，返回第二步凭据。 */
  login(credentials: LoginInputDto): Observable<SessionLoginOutputDto> {
    return this.http.post<SessionLoginOutputDto>(AuthService.loginUrl, credentials);
  }
  //#endif

  // 错误按原样抛出：401（未登录）与服务故障（503/断网）由 StartupService 分别处理，
  // 吞掉异常会把认证服务故障误判成「未登录」。
  async initializeAuth(): Promise<void> {
    await lastValueFrom(this.loadUser());
  }

  loadUser(): Observable<UserOutputDto> {
    // 标记为静默认证：/me 探测的 401 由 Guard（负责 returnUrl）与登录流各自本地处理，
    // 不触发 HTTP 拦截器的全局跳转，避免启动阶段覆盖 Guard 的 returnUrl。
    return this.http
      .get<UserOutputDto>('/api/v1/auth/me', {
        context: new HttpContext().set(SILENT_AUTH, true),
      })
      .pipe(tap((user) => this.setCurrentUser(user)));
  }

  setCurrentUser(user: UserOutputDto): void {
    this._currentUser.set(new User(user));
    //#if (RemoteTokenAuth)
//#if (IncludeMultiTenancy)
    this.tenantContext.setAuthenticatedTenant(user.tenantId ?? null);
//#endif
    //#endif
  }

  /**
   * 清空认证数据。权限与设置的清理在 `SessionContextService.clear()`，非静默 401 与启动流都走那里；
   * `logout()` 之后整页跳转，不必再走一遍。
   */
  clearAuthData(): void {
    this._currentUser.set(null);
    //#if (RemoteTokenAuth)
//#if (IncludeMultiTenancy)
    this.tenantContext.clear();
//#endif
    //#endif
    //#if (IncludeNotifications || IncludeRealTime)
    // 不等待：状态与连接引用在 reset() 内部同步清掉，真正的 stop() 是网络动作，
    // 让它在后台完成即可，不该把登出卡在网络上。
    void this.signalR.reset();
    //#endif
  }

  logout(): void {
    this.clearAuthData();
    //#if (RemoteTokenAuth)
    if (this.isMockedUrl('/api/v1/auth/logout')) {
      this.http
        .post('/api/v1/auth/logout', {})
        .subscribe(() => (window.location.href = '/auth/login'));
      return;
    }
    // 整页 POST 让官方 OIDC 退出重定向由浏览器完成，不通过 XHR 跨站跟随。
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/api/v1/auth/logout';
    document.body.appendChild(form);
    form.submit();
    //#else
    this.endSessionAndGo('/auth/login');
    //#endif
  }
  //#if (LocalIdentity)

  /**
   * 退出后带着 `returnUrl` 回到登录页：确认身份只能靠重新登录时用（账号没有可出示的凭证，或本人用不了
   * 身份验证器）。待提交的修改与凭证都不进地址栏，回来后由本人重新发起。
   */
  signInAgain(returnUrl: string): void {
    this.clearAuthData();
    this.endSessionAndGo(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }

  // 退出请求失败也要离开：本地主体已清空，留在原页面只会看到一个没有用户的界面
  private endSessionAndGo(target: string): void {
    this.http.post('/api/v1/auth/logout', {}).subscribe({
      next: () => (window.location.href = target),
      error: () => (window.location.href = target),
    });
  }
  //#endif
  //#if (RemoteTokenAuth)

  startLogin(returnUrl = '/workspace'): void {
    if (this.isMockedUrl('/api/v1/auth/login')) {
      this.http.post('/api/v1/auth/login', {}).subscribe(() => (window.location.href = returnUrl));
      return;
    }
    window.location.href = `/api/v1/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }
  //#endif
}
