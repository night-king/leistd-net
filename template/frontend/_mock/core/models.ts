import { HttpHeaders, HttpRequest } from '@angular/common/http';

export class MockException extends Error {
  constructor(
    public status: number,
    public error?: any,
  ) {
    super();
  }
}

export interface MockResponse {
  status?: number;
  headers?: HttpHeaders;
  body?: any;
  /** 模拟延迟，单位：毫秒。 */
  delay?: number;
}

export interface MockRequest {
  readonly original: HttpRequest<any>;
  readonly url: string;
  readonly queryParams: Record<string, any>;
  readonly headers: HttpHeaders;
  readonly body: any;
  params: any;
}

export interface MockConfig {
  enable: boolean;
  include?: string | string[];
  exclude?: string | string[];
  /** 模拟延迟，单位：毫秒。 */
  delay?: number;
  log?: boolean;
}
