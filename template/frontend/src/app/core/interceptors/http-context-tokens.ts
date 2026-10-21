import { HttpContextToken } from '@angular/common/http';

export const SILENT_AUTH = new HttpContextToken<boolean>(() => false);

/** 标记请求取的是站点自身的静态资源（如词条），`urlFormatInterceptor` 不加网关前缀。 */
export const SKIP_GATEWAY = new HttpContextToken<boolean>(() => false);
