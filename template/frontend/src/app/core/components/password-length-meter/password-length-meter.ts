import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmFieldDescription } from '@spartan-ng/helm/field';

//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { PASSWORD_MIN_LENGTH } from '../../validation/password-rule';

/**
 * 新口令的长度进度（注册、找回密码、修改与设置密码、管理员重置共用）：口令策略只看长度（见 PASSWORD_MIN_LENGTH
 * 与服务端 PasswordPolicy），这里不另立复杂度规则，只把「离最小长度还差多少」画成四格，够长即达标。放在口令输入框下方。
 *
 * 界面上只有四格，不另写规则说明：长度不够或超长时由字段校验错误说明。进度文字只给读屏软件；组件自身是字段描述，输入框经 aria-describedby 关联到它。
 */
@Component({
  selector: 'app-password-length-meter',
  // prettier-ignore
  imports: [
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './password-length-meter.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // 自身即所在字段的描述：描述指令只在声明它的模板里找字段，写在本组件模板内就关联不到输入框
  hostDirectives: [HlmFieldDescription],
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
  'common.passwordLength.progress': 'At least {{min}} characters, {{remaining}} more to go',
  'common.passwordLength.met': 'Length requirement met',
};
//#endif
