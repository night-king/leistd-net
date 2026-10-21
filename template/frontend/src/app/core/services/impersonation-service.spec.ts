//#if (LocalIdentity)
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { ImpersonationService } from './impersonation-service';

describe('ImpersonationService', () => {
  const key = 'impersonation.exitedNotice';

  function create(): ImpersonationService {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    return TestBed.inject(ImpersonationService);
  }

  afterEach(() => sessionStorage.removeItem(key));

  it('consumes the exit notice flag only once', () => {
    const service = create();
    sessionStorage.setItem(key, '1');

    expect(service.consumeExitedNotice()).toBe(true);
    expect(service.consumeExitedNotice()).toBe(false);
    expect(sessionStorage.getItem(key)).toBeNull();
  });

  it('shows no notice without the flag', () => {
    expect(create().consumeExitedNotice()).toBe(false);
  });

  it('does not throw when storage is unavailable and just shows no notice', () => {
    const service = create();
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });

    expect(service.consumeExitedNotice()).toBe(false);
  });
});
//#endif
