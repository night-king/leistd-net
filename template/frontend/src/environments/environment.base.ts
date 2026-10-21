import { MockConfig } from '../../_mock/core/models';

export interface Environment {
  production: boolean;
  //#if (LocalIdentity)
  /**
   * 是否启用哈希路由（`#/path`）。只在本地身份形态下提供：OIDC 回调地址是无 fragment 的普通路径，
   * 哈希路由下回调组件不会渲染。
   */
  useHash: boolean;
  //#endif
  /**
   * 是否启用 Mock 服务，仅在非生产环境有效。
   * - `false`: 关闭 Mock
   * - `true`: 开启所有 Mock
   * - `object`: 按模块开启 Mock (特性开关)
   */
  useMock: boolean | MockConfig;
  api: {
    /** 网关地址；保持空值，请求以相对路径访问同源 API（同镜像托管、部署代理或开发代理）。 */
    gateway: string;
  };
}

export const environmentBase: Environment = {
  production: false,
  //#if (LocalIdentity)
  useHash: false,
  //#endif
  useMock: false, // 默认关闭
  api: {
    gateway: '',
  },
};
