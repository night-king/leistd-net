import { PagedRequestDto } from '../../../shared/dtos/paged-request.dto';

export type OpenApplicationType = 'web' | 'native' | 'service';
export type OpenApplicationClientType = 'public' | 'confidential';

export interface OpenApplicationOutputDto {
  id: string;
  clientId: string;
  displayName?: string;
  applicationType: OpenApplicationType;
  clientType: OpenApplicationClientType;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
  requirements: string[];
  settings: Record<string, unknown>;
  properties: Record<string, unknown>;
  clientSecret?: string;
  hasClientSecret: boolean;
  /** 授权是否跟随签发时的登录会话；`null` 表示登记早于该设置，编辑时须明确选择。 */
  sessionBound: boolean | null;
  creationTime: string;
}

export interface CreateOpenApplicationInputDto {
  clientId: string;
  displayName?: string;
  applicationType: OpenApplicationType;
  clientType: OpenApplicationClientType;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
  requirements: string[];
  sessionBound: boolean;
}

export interface UpdateOpenApplicationInputDto {
  displayName?: string;
  applicationType: OpenApplicationType;
  clientType: OpenApplicationClientType;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
  requirements: string[];
  sessionBound: boolean;
}

/** 可授予开放应用的 scope；授予时的权限值为 `scp:` 加上 name。 */
export interface OpenApplicationScopeOutputDto {
  name: string;
  displayName: string;
  /** 只能授予 client_credentials 的机器客户端 */
  machineOnly: boolean;
  /** API 受众；标准和机器 scope 没有此值。 */
  audience?: string | null;
}

export interface ResetOpenApplicationSecretOutputDto {
  clientSecret: string;
}

export interface GetOpenApplicationsInputDto extends PagedRequestDto {
  keyword?: string;
  applicationType?: OpenApplicationType;
  clientType?: OpenApplicationClientType;
}
