import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { lastValueFrom } from 'rxjs';

import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { CaptchaOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

/**
 * 图形验证码图片（注册与找回密码共用）：创建即取一张，点击换一张；输入框与校验留在父表单，
 * 提交时取 {@link token}。每取到一张新图发出 `changed`：旧码随旧令牌作废，父表单据此清空已填的码。
 * 令牌只能用一次，父页面在请求失败后调 {@link reload} 换新；换图一开始就作废旧令牌，取图失败时
 * 令牌保持为空并给出「重新获取」，不自动重试。
 */
@Component({
  selector: 'app-captcha-image',
  // prettier-ignore
  imports: [
    HlmButton,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './captcha-image.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
})
export class CaptchaImage {
  private readonly accountService = inject(AccountService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly changed = output<void>();

  protected readonly captcha = signal<CaptchaOutputDto | null>(null);
  protected readonly loadFailed = signal(false);
  /** 当前图片的令牌；正在换图或取图失败时为 null。 */
  readonly token = computed(() => this.captcha()?.captchaToken ?? null);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    // 旧令牌可能已被上一次请求用掉：换图期间不再交出去
    this.captcha.set(null);
    this.loadFailed.set(false);
    try {
      this.captcha.set(await lastValueFrom(this.accountService.getCaptcha()));
      this.changed.emit();
    } catch (error) {
      this.loadFailed.set(true);
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('common.requestError'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error('Request failed', { description: applicationErrorMessage(error) });
      //#endif
    }
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.captcha.refresh': "Can't see clearly? Click to refresh",
  'account.captcha.alt': 'Captcha',
  'common.retry': 'Retry',
};
//#endif
