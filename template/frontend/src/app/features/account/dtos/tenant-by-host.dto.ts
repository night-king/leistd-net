/**
 * 域名对租户的定案结果：`host` 表示已按宿主定案，`undecided` 表示域名不表态、记住的租户或请求头
 * 仍可决定。两者不能合并，否则宿主域上会显示上次记住的租户。
 */
export type HostTenantDecision = 'undecided' | 'host' | 'tenant';

/** 匿名响应里的租户只有名字：未认证者不该读出租户主键，也不该分辨租户是否存在或启用。 */
export interface AnonymousTenantOutputDto {
  name: string;
}

/** 按当前主机名解析租户的结果。 */
export interface TenantByHostOutputDto {
  decision: HostTenantDecision;
  /**
   * 定案到的租户，只在 `decision === 'tenant'` 时可能有值；取不到时 `decision` 仍为 `tenant`，
   * 界面不退回手选。
   */
  tenant?: AnonymousTenantOutputDto;
}
