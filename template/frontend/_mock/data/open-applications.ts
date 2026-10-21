import {
  OpenApplicationOutputDto,
  OpenApplicationScopeOutputDto,
} from '../../src/app/features/platform/dtos/open-application.dto';

export interface MockOpenApplication extends OpenApplicationOutputDto {
  clientSecret?: string;
}

const DESKTOP_SCHEME = 'companyname-projectname-desktop';

export const OPEN_APPLICATIONS: MockOpenApplication[] = [
  {
    id: 'companyname-projectname-web',
    clientId: 'companyname-projectname-web',
    displayName: 'MyProject Web',
    applicationType: 'web',
    clientType: 'public',
    redirectUris: ['http://localhost:4200/auth/callback'],
    postLogoutRedirectUris: ['http://localhost:4200/auth/logout-callback'],
    permissions: [
      'ept:authorization',
      'ept:end_session',
      'ept:token',
      'gt:authorization_code',
      'gt:refresh_token',
      'rst:code',
      'scp:openid',
      'scp:profile',
      'scp:email',
      'scp:roles',
      'scp:offline_access',
    ],
    requirements: ['ft:pkce'],
    settings: {},
    properties: {},
    hasClientSecret: false,
    sessionBound: true,
    creationTime: '2026-05-01T09:00:00Z',
  },
  {
    id: 'companyname-projectname-desktop',
    clientId: 'companyname-projectname-desktop',
    displayName: 'MyProject Desktop',
    applicationType: 'native',
    clientType: 'public',
    redirectUris: [`${DESKTOP_SCHEME}://oauth/callback`],
    postLogoutRedirectUris: [`${DESKTOP_SCHEME}://oauth/logout-callback`],
    permissions: [
      'ept:authorization',
      'ept:end_session',
      'ept:token',
      'gt:authorization_code',
      'gt:refresh_token',
      'rst:code',
      'scp:openid',
      'scp:profile',
      'scp:email',
      'scp:roles',
      'scp:offline_access',
    ],
    requirements: ['ft:pkce'],
    settings: {},
    properties: {},
    hasClientSecret: false,
    sessionBound: false,
    creationTime: '2026-05-02T10:30:00Z',
  },
  {
    id: 'companyname-projectname-service',
    clientId: 'companyname-projectname-service',
    displayName: 'MyProject Service Client',
    applicationType: 'service',
    clientType: 'confidential',
    redirectUris: [],
    postLogoutRedirectUris: [],
    permissions: ['ept:token', 'gt:client_credentials'],
    requirements: [],
    settings: {},
    properties: {},
    hasClientSecret: true,
    sessionBound: false,
    creationTime: '2026-05-03T14:15:00Z',
    clientSecret: 'mock-service-secret',
  },
];

/** 服务端 scope 目录：OIDC 标准 scope、本服务 API、仅限机器的内部 scope。 */
export const OPEN_APPLICATION_SCOPES: OpenApplicationScopeOutputDto[] = [
  { name: 'openid', displayName: 'OpenID', machineOnly: false },
  { name: 'profile', displayName: 'Profile', machineOnly: false },
  { name: 'email', displayName: 'Email', machineOnly: false },
  { name: 'roles', displayName: 'Roles', machineOnly: false },
  { name: 'offline_access', displayName: 'Offline access', machineOnly: false },
  {
    name: 'companyname-projectname-api',
    displayName: 'API',
    machineOnly: false,
    audience: 'companyname-projectname-api',
  },
  //#if (IncludeMultiTenancy)
  {
    name: 'tenant-routing.read',
    displayName: 'Read tenant connection routing metadata',
    machineOnly: true,
  },
  {
    name: 'tenant-migration.read',
    displayName: 'Read tenant connection migration metadata',
    machineOnly: true,
  },
  //#endif
];
