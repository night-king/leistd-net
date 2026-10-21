import { environmentBase, Environment } from './environment.base';

export const environment: Environment = {
  ...environmentBase,
  production: true,
  // 生产构建另经 fileReplacements 移除 Mock 代码与数据（见 angular.json）
  useMock: {
    enable: false,
    delay: 0,
    log: false,
  },
};
