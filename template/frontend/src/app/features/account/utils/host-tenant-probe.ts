import { computed, inject, signal } from '@angular/core';
import { lastValueFrom } from 'rxjs';

import { TenantContextService } from '../../../core/services/tenant-context-service';
import { HostTenantDecision } from '../dtos/tenant-by-host.dto';
import { AccountService } from '../services/account-service';

/**
 * 按主机名定案租户（登录、找回密码共用）：创建即探测一次，结果写进租户上下文。匿名认证请求要等它定案再发——
 * 本地记住的租户只是提示，域名指向的租户或宿主才是服务端实际采用的上下文。须在注入上下文里创建。
 *
 * 进度与结果（`decision`）：
 * - `pending` 未回来：定案会覆盖当前上下文，认证入口不放行；
 * - `tenant` / `host` 域名已定案，不能再改；
 * - `undecided` 域名不表态（未配置子域名格式），交回用户手填；
 * - `failed` 探测没能完成（租户不可用时中间件返回 404，或网络故障），不能当作 `undecided`。
 */
export function hostTenantProbe() {
  const accountService = inject(AccountService);
  const tenantContext = inject(TenantContextService);

  const decision = signal<HostTenantDecision | 'pending' | 'failed'>('pending');
  /** 租户区的错误说明，存词条键、由模板按当前语言取：存成文字的话切换语言时它不会跟着变。 */
  const error = signal<string | null>(null);

  async function resolve(): Promise<void> {
    try {
      const result = await lastValueFrom(accountService.getTenantByHost());
      switch (result.decision) {
        case 'tenant':
          // 租户不存在或已停用时 tenant 为空：清掉记住的那个，并保持锁定，
          // 让界面提示这个域名不可用，而不是让人换一个反正会被覆盖的租户。
          if (result.tenant) {
            tenantContext.set(result.tenant.name);
          } else {
            tenantContext.clear();
            error.set('account.login.tenantUnavailable');
          }
          break;

        case 'host':
          // 受管域但不指向租户：服务端会按宿主处理，记住的租户必须清掉。
          tenantContext.clear();
          break;

        case 'undecided':
          // 域名不表态：保持现状（上次记住的租户，或空白待填）。
          break;
      }

      decision.set(result.decision);
    } catch {
      // 未配置子域名格式的部署正常返回 undecided，走不到这里；失败只能是租户解析不了（404）
      // 或后端不可达，因此不开放手选也不放行认证，只给原因和重试。
      decision.set('failed');
      error.set('account.login.tenantProbeFailed');
    }
  }

  void resolve();

  return {
    decision: decision.asReadonly(),
    error,
    /**
     * 租户由域名定案（指向租户或宿主）时锁定：服务端按主机名解析且不允许请求头改写，
     * 放开选择会让界面显示的租户与请求实际落到的上下文不一致。
     */
    locked: computed(() => decision() === 'host' || decision() === 'tenant'),
    /** 上下文未定案（探测未回来或失败）：此时提交会落到与界面不一致的上下文，认证入口一律等待。 */
    blocked: computed(() => decision() === 'pending' || decision() === 'failed'),
    /** 重试探测：瞬时故障不该只剩刷新整页（表单可能已填好）。 */
    async retry(): Promise<void> {
      if (decision() !== 'failed') {
        return;
      }

      decision.set('pending');
      error.set(null);
      await resolve();
    },
  };
}
