import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { MOCK_APIS, mockInterceptor } from './interceptor';
//#if (OpenIddictServer)
import {
  CreateOpenApplicationInputDto,
  OpenApplicationOutputDto,
} from '../../src/app/features/platform/dtos/open-application.dto';
//#endif
import { environment } from '../../src/environments/environment';
//#if (OpenIddictServer)
import { OPEN_APPLICATION_API } from '../api/open-application';
// USERS 只被开放应用那组用例用到（建一个有权限的会话）
import { USERS } from '../data/user';
//#endif
//#if (LocalIdentity)
// setMockSessionUserId 的使用范围更宽：清会话是所有有本地身份的形态都要做的
import { setMockSessionUserId } from '../utils/current-user';
//#endif

describe('mockInterceptor', () => {
  const originalUseMock = environment.useMock;

  beforeEach(() => {
    environment.useMock = true;
    //#if (OpenIddictServer)
    // 开放应用接口有权限门（与后端策略一一对应），本组用例关注的是拦截器本身，
    // 因此先建立一个有权限的会话，避免被 401 挡在门外。
    setMockSessionUserId(USERS.find((user) => user.isSuperAdmin)!.id);
    //#endif
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([mockInterceptor])),
        provideHttpClientTesting(),
        {
          provide: MOCK_APIS,
          // prettier-ignore
          useValue: {
                        //#if (OpenIddictServer)
                        ...OPEN_APPLICATION_API,
                        //#endif
                        'GET /api/items': [{ id: 'all' }],
                        'GET /api/items/:id': (request: {
                            params: Record<string, string>;
                        }) => ({
                            id: request.params['id'],
                        }),
                    },
        },
      ],
    });
  });

  afterEach(() => {
    environment.useMock = originalUseMock;
    //#if (LocalIdentity)
    setMockSessionUserId(null);
    //#endif
  });

  it('resolves an exact route to its mocked payload', async () => {
    const response = await firstValueFrom(
      TestBed.inject(HttpClient).get<{ id: string }[]>('/api/items'),
    );

    expect(response).toEqual([{ id: 'all' }]);
  });

  it('matches a parameterized route and decodes the path parameter', async () => {
    const response = await firstValueFrom(
      TestBed.inject(HttpClient).get<{ id: string }>('/api/items/item-42'),
    );

    expect(response).toEqual({ id: 'item-42' });
  });

  it('fails with 501 when no mock route matches an /api request', async () => {
    await expect(
      firstValueFrom(TestBed.inject(HttpClient).get('/api/unknown')),
    ).rejects.toMatchObject({
      status: 501,
    });
  });
  //#if (OpenIddictServer)

  it('returns a confidential client secret only in the create response', async () => {
    const clientId = `spec-confidential-${crypto.randomUUID()}`;
    const input: CreateOpenApplicationInputDto = {
      clientId,
      displayName: 'Spec confidential client',
      applicationType: 'service',
      clientType: 'confidential',
      redirectUris: [],
      postLogoutRedirectUris: [],
      permissions: ['ept:token', 'gt:client_credentials'],
      requirements: [],
      sessionBound: false,
    };
    const http = TestBed.inject(HttpClient);

    const created = await firstValueFrom(
      http.post<OpenApplicationOutputDto>('/api/v1/open-applications', input),
    );
    expect(created.clientSecret).toContain('mock-secret-');
    expect(created.hasClientSecret).toBe(true);

    const stored = await firstValueFrom(
      http.get<OpenApplicationOutputDto>(`/api/v1/open-applications/${clientId}`),
    );
    expect(stored.clientSecret).toBeUndefined();
    expect(stored.sessionBound).toBe(false);
  });

  it('rejects open applications without an explicit session binding choice', async () => {
    const http = TestBed.inject(HttpClient);
    const input = {
      clientId: `spec-unset-${crypto.randomUUID()}`,
      applicationType: 'web',
      clientType: 'public',
      redirectUris: ['https://spa.example.test/callback'],
      postLogoutRedirectUris: [],
      permissions: ['ept:authorization', 'ept:token', 'gt:authorization_code', 'rst:code'],
      requirements: ['ft:pkce'],
    };

    await expect(
      firstValueFrom(http.post('/api/v1/open-applications', input)),
    ).rejects.toMatchObject({ status: 400 });
  });
  //#endif
});
