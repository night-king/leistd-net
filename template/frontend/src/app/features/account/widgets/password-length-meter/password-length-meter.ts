import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmFieldImports } from '@spartan-ng/helm/field';

import { PASSWORD_MIN_LENGTH } from '../../../../core/validation/password-rule';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif

/**
 * 新口令的长度进度（注册与找回密码共用）：口令策略只看长度（见 PASSWORD_MIN_LENGTH 与服务端 PasswordPolicy），
 * 这里不另立复杂度规则，只把「离最小长度还差多少」画成四格，够长即达标。放在口令输入框下方。
 */
@Component({
  selector: 'app-password-length-meter',
  // prettier-ignore
  imports: [
    ...HlmFieldImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './password-length-meter.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex flex-col gap-1.5', 'data-testid': 'password-strength' },
})
export class PasswordLengthMeter {
  readonly password = input.required<string>();

  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly min = PASSWORD_MIN_LENGTH;
  protected readonly segments = [1, 2, 3, 4];
  protected readonly state = computed(() => {
    const length = this.password().length;
    return {
      met: length >= PASSWORD_MIN_LENGTH,
      filled: Math.min(4, Math.ceil((length / PASSWORD_MIN_LENGTH) * 4)),
      remaining: Math.max(0, PASSWORD_MIN_LENGTH - length),
    };
  });
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.passwordLength.progress': 'At least {{min}} characters, {{remaining}} more to go',
  'account.passwordLength.met': 'Length requirement met',
};
//#endif
