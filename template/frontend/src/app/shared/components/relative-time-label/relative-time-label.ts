import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import { formatAppDate } from '../../pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../utils/english-text';
//#endif
import { formatRelativeTime } from '../../utils/relative-time';

/**
 * 近期列表里的"多久以前"：刚刚 / N 分钟前 / 昨天 HH:mm / 月日时刻，完整时刻放进可键盘聚焦的提示。
 * `now` 由调用方的分钟级时钟给出，时钟走动时文字随之更新；字号与颜色由调用方在宿主上设。
 *
 * ```html
 * <app-relative-time-label class="text-muted-foreground text-xs" [instant]="row.time" [now]="now()"
 *   [timeZone]="timeZone()" [locale]="displayLocale()" />
 * ```
 */
@Component({
  selector: 'app-relative-time-label',
  // prettier-ignore
  imports: [
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './relative-time-label.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'shrink-0 tabular-nums' },
})
export class RelativeTimeLabel {
  readonly instant = input.required<string>();
  readonly now = input.required<number>();
  /** 实际生效的展示时区（经 `resolveDisplayTimeZone` 解析后的值）。 */
  readonly timeZone = input.required<string>();
  readonly locale = input<string>();
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  protected readonly relative = computed(() =>
    formatRelativeTime(this.instant(), this.now(), this.timeZone(), this.locale()),
  );
  protected readonly full = computed(() =>
    formatAppDate(this.instant(), 'full', this.timeZone(), this.locale()),
  );
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.time.justNow': 'Just now',
  'common.time.yesterdayAt': 'Yesterday {{time}}',
};
//#endif
