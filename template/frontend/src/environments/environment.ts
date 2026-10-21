import { environmentBase, Environment } from './environment.base';

/**
 * 本机开发环境（`npm start`）：网关保持空值，由 proxy.conf.mjs 转发给本机后端；生产构建经
 * angular.json 的 fileReplacements 换成 `environment.prod.ts`。
 */
export const environment: Environment = {
  ...environmentBase,
};
