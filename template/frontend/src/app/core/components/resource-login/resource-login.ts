//#if (RemoteTokenAuth)
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';

//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { AuthService } from '../../services/auth-service';

@Component({
  selector: 'app-resource-login',
  //#if (IncludeLocalization)
  imports: [TranslocoDirective, HlmButton, HlmCardImports],
  //#else
  imports: [HlmButton, HlmCardImports],
  //#endif
  templateUrl: './resource-login.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResourceLogin {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  signIn(): void {
    this.auth.startLogin(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/workspace');
  }
}
//#if (!IncludeLocalization)

const ENGLISH = {
  'common.signIn': 'Sign in',
  'common.continueToSignIn': 'Continue to sign in',
} as const;
//#endif
//#endif
