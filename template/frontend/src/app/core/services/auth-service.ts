import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, Injector, inject, signal } from '@angular/core';
import { Observable, defer, lastValueFrom, tap } from 'rxjs';

import { AccountPreferencesService, PreferenceRead } from './account-preferences-service';
//#if (IncludeNotifications || IncludeRealTime)
import { SignalRService } from './signalr-service';
//#endif
//#if (RemoteTokenAuth)
//#if (IncludeMultiTenancy)
import { TenantContextService } from './tenant-context-service';
//#endif
//#endif
//#if (LocalIdentity)
import { PasskeyOptionsOutputDto } from '../../shared/dtos/account-security.dto';
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
  // 按需取偏好服务：它依赖语言服务，构造期注入会经 HTTP 拦截器成环
  private readonly injector = inject(Injector);
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
  private _revision = 0;
  /** 最近一次 `loadUser()` 读到的偏好与发起时取的号，供随后建立会话时应用（见 `SessionContextService.establish`）。 */
  private _lastReadPreferences: PreferenceRead | null = null;

  /** 当前用户每写入或清空一次加一：后台读取据此判断发出之后是否已有更新的写入（见 `SessionContextService.revalidate`）。 */
  get revision(): number {
    return this._revision;
  }

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
    // 号在请求发出时取：之后到应用之间有写入成功或主体离开，这份 /me 就是旧的，不能覆盖写入结果
    return defer(() => {
      const ticket = this.injector.get(AccountPreferencesService).beginRead();
      return this.fetchUser().pipe(
        tap((user) => {
          this.setCurrentUser(user);
          this._lastReadPreferences = {
            ticket,
            read: {
              preferences: user.preferences ?? null,
              preferenceOverrides: user.preferenceOverrides ?? null,
            },
          };
        }),
      );
    });
  }

  /**
   * 最近一次 `loadUser()` 读到的偏好及其读取号。这里只留存不应用：应用偏好要动语言服务，而本服务在 HTTP 拦截器构造时
   * 就被注入，构造期依赖语言服务会成环。
   */
  lastReadPreferences(): PreferenceRead | null {
    return this._lastReadPreferences;
  }

  /**
   * 只取不应用：调用方确认主体没变之后再 {@link setCurrentUser}（见 `SessionContextService.revalidate`）。
   * @param silent 默认静默：启动与登录流里 /me 探测的 401 由 Guard（负责 returnUrl）与登录流各自本地处理，
   * 不触发 HTTP 拦截器的全局跳转。会话中途的重查传 `false`，401 由拦截器清会话并转去登录。
   */
  fetchUser(silent = true): Observable<UserOutputDto> {
    return this.http.get<UserOutputDto>('/api/v1/auth/me', {
      context: new HttpContext().set(SILENT_AUTH, silent),
    });
  }

  setCurrentUser(user: UserOutputDto): void {
    this._revision++;
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
    this._revision++;
    this._currentUser.set(null);
    this._lastReadPreferences = null;
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

  /**
   * 确认身份：取用通行密钥确认的断言选项。断言不单独提交，随要求确认的那个请求一起重放（`passkey` 字段）；
   * 本人没有通行密钥或部署没配时以 `Auth:ReauthenticationMethodUnavailable` / `Auth:PasskeyUnavailable` 拒绝。
   */
  beginPasskeyReauthentication(): Observable<PasskeyOptionsOutputDto> {
    return this.http.post<PasskeyOptionsOutputDto>(
      '/api/v1/auth/me/reauthentication/passkey-options',
      {},
    );
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
