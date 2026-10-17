import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { Injector, inject } from '@angular/core';
//#if (LocalIdentity)
import { Router } from '@angular/router';
//#endif
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
//#if (LocalIdentity)
import { catchError, lastValueFrom, throwError } from 'rxjs';
//#else
import { catchError, throwError } from 'rxjs';
//#endif

import { SILENT_AUTH } from './http-context-tokens';
//#if (LocalIdentity)
import { API_ERROR_CODES } from '../errors/api-error-codes';
import { apiErrorCode, ApplicationHttpError } from '../errors/application-http-error';
//#else
import { ApplicationHttpError } from '../errors/application-http-error';
//#endif
import { EntryRouteService } from '../routing/entry-route-service';
//#if (LocalIdentity)
import { ReturnUrlService } from '../routing/return-url-service';
//#endif
import { AuthService } from '../services/auth-service';
import { SessionContextService } from '../services/session-context-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../services/tenant-context-service';
//#endif
//#if (IncludeMultiTenancy)
import { TENANT_INVALID_HEADER } from '../tenancy/tenant-protocol';
//#endif

export const httpErrorInterceptor: HttpInterceptorFn = (req, next) => {
  const authService = inject(AuthService);
//#if (IncludeMultiTenancy)
  const tenantContext = inject(TenantContextService);
//#endif
  const entryRoute = inject(EntryRouteService);
  // TranslocoService 与 SessionContextService 不能在这里直接注入：前者加载词条走 HttpClient，
  // 后者构造时创建 LanguageService、它立即加载初始语言的词条，这些请求都要经过本拦截器，
  // 构造期注入就是循环依赖（NG0200，整个应用停在启动页）。出错时再按需取——那时它们早已构造完成。
  const injector = inject(Injector);
  //#if (LocalIdentity)
  const router = inject(Router);
  const returnUrls = inject(ReturnUrlService);
  //#endif
  return next(req).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse)) {
        return throwError(() => error);
      }

      if (error.status === 401) {
        // 这个头表示会话所属租户已不存在，与请求是否静默、当前路由、是否重新认证都无关，一律清掉本地
        // 失效的租户选择：登录页的启动探测最容易撞上它（旧 Cookie 带着已停用租户），不清的话后续登录
        // 一直携带失效的租户提示头（服务端见 UseTenantSessionRecovery）。普通 401 不带这个头，
        // 租户去向由 AuthService.clearAuthData() 按身份形态决定。
//#if (IncludeMultiTenancy)
        if (error.headers.get(TENANT_INVALID_HEADER)) {
          tenantContext.clear();
        }

//#endif
        // 会话清理与重新认证需同时满足：
        // 1) 不在认证路由上：那条流程正在建立主体，插手会清掉它并多绕一轮授权（见 StartupService）。
        // 2) 请求不是静默的：静默请求的处置权在调用方。
        // 3) 仍持有主体：也是并发 401 的收敛点，第一条清掉主体后，同批后到的不再重复导航。
        // 4) 401 表示会话没了而非这次操作被拒：再认证连续失败的临时锁定不踢已有会话
        //    （见 User.AllowsExistingSessions），且锁定期内登录页进不去；管理员锁定不豁免。
        //#if (LocalIdentity)
        const lockedOutButStillSignedIn =
          apiErrorCode(error.error) === API_ERROR_CODES.userTemporarilyLockedOut;
        //#else
        // 资源服务形态没有再认证入口（认证在签发方），401 只有"会话没了"一种含义
        const lockedOutButStillSignedIn = false;
        //#endif

        if (
          !lockedOutButStillSignedIn &&
          !entryRoute.isOnAuthRoute() &&
          !req.context.get(SILENT_AUTH) &&
          authService.isAuthenticated()
        ) {
          // 主体离开要清干净：只清认证数据会把权限和设置留给下一个登录的人，
          // 表现是新用户看到上一个人的显示偏好。
          injector.get(SessionContextService).clear();
          //#if (LocalIdentity)
          void router.navigate(['/auth/login'], { queryParams: { returnUrl: entryRoute.url() } });
          //#else
          // 服务端已尝试刷新；401 表示会话无法续期或已撤销，重新发起整页认证。
          authService.startLogin(entryRoute.url());
          //#endif
        }
      }
      //#if (LocalIdentity)

      // 受限会话（组织要求两步验证而本人尚未启用）调了设置之外的接口：带去设置页，完成后回到当前页面。
      // 路由守卫只在导航时判定，这里兜住的是会话中途变成受限（组织刚打开要求）。先刷新当前用户：
      // 守卫读的是它，不刷新的话旧的"不受限"会让守卫把人放回工作台，设置页与工作台来回跳。
      if (
        error.status === 403 &&
        apiErrorCode(error.error) === API_ERROR_CODES.twoFactorSetupRequired &&
        !entryRoute.isOnAuthRoute()
      ) {
        const returnUrl = entryRoute.url();
        void lastValueFrom(authService.loadUser())
          .catch(() => undefined)
          .then(() => router.navigateByUrl(returnUrls.twoFactorSetup(returnUrl)));
      }
      //#endif

      //#if (IncludeLocalization)
      // 错误对象带着的是现成文字，事后不会随词条更新：词条尚未到达（首帧前与初始语言词条并行的启动请求）时
      // translate() 返回键本身，此时交给 ApplicationHttpError 用它自带的英文说法，而不是把裸键写进去。
      const transloco = injector.get(TranslocoService);
      const text = (key: string): string | undefined => {
        const value = transloco.translate(key);
        return value === key ? undefined : value;
      };
      return throwError(() =>
        ApplicationHttpError.from(
          error,
          error.status === 0 ? text('common.networkError') : undefined,
          text('common.traceId'),
        ),
      );
      //#else
      return throwError(() => ApplicationHttpError.from(error));
      //#endif
    }),
  );
};
