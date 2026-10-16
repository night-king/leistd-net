import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, Observable, of, switchMap } from 'rxjs';

//#if (IncludeMultiTenancy)
import { TenantContextService } from './tenant-context-service';
//#endif
import { SecurityConfigOutputDto } from '../../shared/dtos/account-security.dto';

/**
 * 当前租户的注册摘要：是否开放自助注册、注册时怎么验证。登录页、注册页、首页与个人资料读同一份。
 * 进入这些页面时调用 {@link refresh} 取当前值（管理员可能刚改过策略）；
 * 登录入口换了租户也会重新取（注册策略按租户），未完成的旧请求随之取消。
 */
@Injectable({ providedIn: 'root' })
export class SecurityConfigService {
  private readonly http = inject(HttpClient);
  //#if (IncludeMultiTenancy)
  private readonly tenantContext = inject(TenantContextService);
  //#endif
  private readonly revision = signal(0);

  /** 取回的摘要；取回前为 `undefined`，取不到为 `null`。 */
  //#if (IncludeMultiTenancy)
  readonly config = toSignal(
    toObservable(computed(() => [this.tenantContext.current(), this.revision()])).pipe(
      switchMap(() => this.load()),
    ),
  );
  //#else
  readonly config = toSignal(toObservable(this.revision).pipe(switchMap(() => this.load())));
  //#endif

  /**
   * 是否给出注册入口：只有明确关闭时才隐藏。取回前与取不到时照常显示，注册接口本身会拒绝已关闭的租户，
   * 不因一次读取失败把开放注册的租户的入口藏起来。
   */
  readonly registrationOpen = computed(() => this.config()?.registrationEnabled !== false);

  /** 重新读取当前租户的摘要；消费页面在进入时调用。 */
  refresh(): void {
    this.revision.update((value) => value + 1);
  }

  private load(): Observable<SecurityConfigOutputDto | null> {
    return this.http
      .get<SecurityConfigOutputDto>('/api/v1/auth/security-config')
      .pipe(catchError(() => of(null)));
  }
}
