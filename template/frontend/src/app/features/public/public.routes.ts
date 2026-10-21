import { Routes } from '@angular/router';
//#if (IncludeLocalization)
import { provideTranslocoScope } from '@jsverse/transloco';
//#endif
//#if (IncludeLocalization)

import { resolveTranslationScopes } from '../../core/i18n/translation-scopes';
//#endif

/** 公共页面路由，作为 Landing Layout 的子路由。 */
export const PUBLIC_ROUTES: Routes = [
  {
    path: '',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('landing')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () => import('./components/landing/landing').then((m) => m.Landing),
  },
];
