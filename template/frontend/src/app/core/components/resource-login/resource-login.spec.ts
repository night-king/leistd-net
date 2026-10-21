//#if (RemoteTokenAuth)
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';

import { ResourceLogin } from './resource-login';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif
import { AuthService } from '../../services/auth-service';

describe('ResourceLogin', () => {
  const startLogin = vi.fn();

  function render(query: Record<string, string> = {}): HTMLElement {
    startLogin.mockReset();
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        { provide: AuthService, useValue: { startLogin } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(query) } },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });
    const fixture = TestBed.createComponent(ResourceLogin);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  // 登录入口走组件库按钮，不在页面里手写按钮样式
  it('renders the sign-in action as a library button that does not start login by itself', () => {
    const button = render().querySelector('button');

    expect(button?.getAttribute('data-slot')).toBe('button');
    expect(startLogin).not.toHaveBeenCalled();
  });

  it('starts login with the requested returnUrl', () => {
    render({ returnUrl: '/workspace/settings' }).querySelector('button')?.click();

    expect(startLogin).toHaveBeenCalledExactlyOnceWith('/workspace/settings');
  });

  it('falls back to the workspace when no returnUrl is given', () => {
    render().querySelector('button')?.click();

    expect(startLogin).toHaveBeenCalledExactlyOnceWith('/workspace');
  });
});
//#endif
