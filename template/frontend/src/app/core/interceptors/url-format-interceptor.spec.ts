import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { SKIP_GATEWAY } from './http-context-tokens';
import { GATEWAY_SERVICE_NAME, urlFormatInterceptor } from './url-format-interceptor';
import { environment } from '../../../environments/environment';
import { MOCKED_URL } from '../mock/mocked-url';

describe('urlFormatInterceptor', () => {
  const originalGateway = environment.api.gateway;
  let mocked: (url: string) => boolean;

  beforeEach(() => {
    mocked = () => false;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([urlFormatInterceptor])),
        provideHttpClientTesting(),
        { provide: MOCKED_URL, useValue: (url: string) => mocked(url) },
      ],
    });
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    environment.api.gateway = originalGateway;
  });

  function send(url: string, context?: HttpContext) {
    TestBed.inject(HttpClient).get(url, { context }).subscribe();
    const [request] = TestBed.inject(HttpTestingController).match(() => true);
    request.flush({});
    return request.request;
  }

  it('prefixes relative urls with the gateway and sends credentials', () => {
    environment.api.gateway = 'https://gateway.example.com/';

    const request = send('/api/v1/users?offset=0');

    expect(request.url).toBe('https://gateway.example.com/api/v1/users?offset=0');
    expect(request.withCredentials).toBe(true);
  });

  it('inserts the gateway service name between the gateway and the path', () => {
    environment.api.gateway = 'https://gateway.example.com';

    const request = send('/api/v1/users', new HttpContext().set(GATEWAY_SERVICE_NAME, '/crm'));

    expect(request.url).toBe('https://gateway.example.com/crm/api/v1/users');
  });

  // 调用方给的绝对地址是别的服务，不能把本服务的会话 Cookie 带过去
  it('forwards absolute urls unchanged and without credentials', () => {
    environment.api.gateway = 'https://gateway.example.com';

    const request = send('https://other.example.com/api/v1/items');

    expect(request.url).toBe('https://other.example.com/api/v1/items');
    expect(request.withCredentials).toBe(false);
  });

  it('leaves site assets marked with SKIP_GATEWAY on the site itself', () => {
    environment.api.gateway = 'https://gateway.example.com';

    const request = send('/i18n/en.json', new HttpContext().set(SKIP_GATEWAY, true));

    expect(request.url).toBe('/i18n/en.json');
  });

  // Mock 按路径匹配，加了网关前缀就匹配不上
  it('leaves requests answered by the mock unchanged', () => {
    environment.api.gateway = 'https://gateway.example.com';
    mocked = (url) => url.startsWith('/api/v1/users');

    expect(send('/api/v1/users').url).toBe('/api/v1/users');
    expect(send('/api/v1/roles').url).toBe('https://gateway.example.com/api/v1/roles');
  });
});
