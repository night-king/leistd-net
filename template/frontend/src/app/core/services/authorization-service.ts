import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, lastValueFrom, tap } from 'rxjs';

import { PLATFORM_ENTRY_PERMISSIONS } from '../../shared/constants/permission.constants';
import { CurrentPermissionsOutputDto } from '../../shared/dtos/permission.dto';
import { SILENT_AUTH } from '../interceptors/http-context-tokens';

/**
 * 当前用户有效权限的唯一来源。
 *
 * 路由 Guard、菜单、按钮和表格操作列全部从这里读取，避免出现"按角色名放行"的第二套语义。
 * 权限集合在启动时加载一次；授权发生变更后调用 {@link reload} 刷新。
 */
@Injectable({ providedIn: 'root' })
export class AuthorizationService {
  private readonly http = inject(HttpClient);

  private readonly _permissions = signal<ReadonlySet<string>>(new Set<string>());
  private readonly _isSuperAdmin = signal(false);
  private readonly _versionToken = signal('');

  /** 授权版本。变化即表示权限已被改动，页面应重新拉取受影响数据。 */
  readonly versionToken = this._versionToken.asReadonly();
  readonly isSuperAdmin = this._isSuperAdmin.asReadonly();
  readonly permissions = computed(() => [...this._permissions()]);

  /** 是否已加载过权限。未加载完成前一律按"无权限"处理，避免闪现受保护入口。 */
  readonly loaded = computed(() => this._versionToken() !== '');

  /** 是否可以进入平台管理区：判据是拥有任一平台入口权限，而不是角色名或超级管理员标志。 */
  readonly canAccessPlatform = computed(() => this.hasAny(...PLATFORM_ENTRY_PERMISSIONS));

  async initialize(): Promise<void> {
    await lastValueFrom(this.load());
  }

  load(): Observable<CurrentPermissionsOutputDto> {
    // 与 /me 一样标记静默认证：未登录时的 401 由 Guard 与启动流各自处理，
    // 不触发拦截器的全局跳转，避免覆盖 returnUrl。
    return this.http
      .get<CurrentPermissionsOutputDto>('/api/v1/permissions/current', {
        context: new HttpContext().set(SILENT_AUTH, true),
      })
      .pipe(tap((result) => this.setPermissions(result)));
  }

  /** 授权变更后刷新。失败时保持原有权限，由调用方提示重试。 */
  reload(): Observable<CurrentPermissionsOutputDto> {
    return this.load();
  }

  setPermissions(result: CurrentPermissionsOutputDto): void {
    this._permissions.set(new Set(result.permissions));
    this._isSuperAdmin.set(result.isSuperAdmin);
    this._versionToken.set(result.versionToken);
  }

  clear(): void {
    this._permissions.set(new Set<string>());
    this._isSuperAdmin.set(false);
    this._versionToken.set('');
  }

  has(permission: string): boolean {
    return this._permissions().has(permission);
  }

  hasAny(...permissions: string[]): boolean {
    return permissions.some((permission) => this.has(permission));
  }

  hasAll(...permissions: string[]): boolean {
    return permissions.every((permission) => this.has(permission));
  }
}
