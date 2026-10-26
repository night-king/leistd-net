import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleCheck, lucideCircleX, lucideRotateCcw } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmComboboxImports } from '@spartan-ng/helm/combobox';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmSwitchImports } from '@spartan-ng/helm/switch';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { firstValueFrom } from 'rxjs';

//#if (Email)
import { EmailTest } from './widgets/email-test/email-test';
//#endif
//#if (ExternalLogin)
// prettier-ignore
import {
  EXTERNAL_LOGIN_GROUP,
  ExternalLoginProviders,
  ExternalLoginProviderSettings,
  partitionExternalLoginSettings,
} from './widgets/external-login-providers/external-login-providers';
//#endif
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { LanguageService } from '../../../../core/services/language-service';
//#endif
import { SessionContextService } from '../../../../core/services/session-context-service';
import { SettingService } from '../../../../core/settings/setting-service';
import { SETTINGS } from '../../../../core/settings/setting.constants';
import { SettingOutputDto } from '../../../../core/settings/setting.dto';
import { InfoHint } from '../../../../shared/components/info-hint/info-hint';
import { ComboboxRevealSelected } from '../../../../shared/directives/combobox-reveal-selected';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
// prettier-ignore
import {
  browserTimeZone,
  LOG_LEVEL_DESCRIPTIONS,
  SETTING_CHOICES,
  SettingChoice,
  timeZoneGroups,
  timeZoneMatches,
  timeZoneOffsetLabel,
} from '../../setting-choices';
// prettier-ignore
import {
  groupPath,
  groupSettings,
  SettingGroup,
  SettingScope,
  settingsInScope,
  SettingsPageState,
} from '../../settings-page-state';

/**
 * 单项设置的写入状态。`saved` 在 {@link SAVED_HINT_MS} 后回到常态；
 * `error` 保留到下一次尝试，失败必须一直可见。
 */
interface RowState {
  status: 'saving' | 'saved' | 'error';
  message?: string;
}

/** 一个分组的渲染内容：分组本身加上按专属布局拆走之后剩下的普通行。 */
interface GroupView extends SettingGroup {
  /** 按通用网格渲染的行。 */
  rows: SettingOutputDto[];
  /** 整组都是进程级设置：生效范围在分组顶部说一次，不在每行重复。 */
  hostScopeOnce: boolean;
  //#if (ExternalLogin)
  /** 外部登录分组按提供方拆出的卡片；其他分组为空。 */
  externalLoginProviders: ExternalLoginProviderSettings[];
  //#endif
}

/** 继承来源的文案，由模板按当前语言传入。 */
interface InheritedSourceLabels {
  followSystem: string;
  followOrganization: string;
  deploymentDefault: string;
}

/** "已保存"提示的停留时长：看得见，又不与下一次改动的状态混淆。 */
const SAVED_HINT_MS = 2000;

/**
 * "保存中"的最短可见时长：同机部署下写入几十毫秒就返回，转圈一闪而过，
 * 用户分不清"已保存"是这次点击的结果还是上次的残留。
 */
export const SAVING_MIN_MS = 400;

/**
 * 设置区块：按作用域与分组渲染设置项，逐行即改即存。
 *
 * 账户作用域写当前用户偏好，系统作用域写当前上下文的默认值（需要 App.Settings）。
 * 快照由外壳的 {@link SettingsPageState} 提供，本组件不取数。值为空表示未覆盖，
 * 继承值只作占位符展示，否则"恢复默认"看起来没有效果。
 */
@Component({
  selector: 'app-setting-section',
  //#if (IncludeLocalization)
  imports: [
    NgIcon,
    NgTemplateOutlet,
    HlmBadge,
    HlmButton,
    HlmInput,
    HlmSpinner,
    ...HlmComboboxImports,
    ...HlmFieldImports,
    ...HlmSelectImports,
    ...HlmSwitchImports,
    ...HlmTooltipImports,
    TranslocoDirective,
    ComboboxRevealSelected,
    InfoHint,
    //#if (Email)
    EmailTest,
    //#endif
    //#if (ExternalLogin)
    ExternalLoginProviders,
    //#endif
  ],
  //#else
  imports: [
    NgIcon,
    NgTemplateOutlet,
    HlmBadge,
    HlmButton,
    HlmInput,
    HlmSpinner,
    ...HlmComboboxImports,
    ...HlmFieldImports,
    ...HlmSelectImports,
    ...HlmSwitchImports,
    ...HlmTooltipImports,
    ComboboxRevealSelected,
    InfoHint,
    //#if (Email)
    EmailTest,
    //#endif
    //#if (ExternalLogin)
    ExternalLoginProviders,
    //#endif
  ],
  //#endif
  providers: [provideIcons({ lucideCircleCheck, lucideCircleX, lucideRotateCcw })],
  templateUrl: './setting-section.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingSection {
  private readonly settingService = inject(SettingService);
  private readonly sessionContext = inject(SessionContextService);
  private readonly pageState = inject(SettingsPageState);
  //#if (IncludeLocalization)
  private readonly languageService = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  /**
   * 作用域由路由数据经输入绑定给出：个人偏好在 `/workspace/settings`，系统默认值在
   * `/platform/settings`。未声明时按账户处理，不默认打开写全租户的页面。
   */
  readonly scope = input<SettingScope>('account');

  /** 只渲染这一个分组（路由参数，URL 写法见 {@link groupPath}）；不给则渲染本作用域的全部分组。 */
  readonly group = input<string | undefined>(undefined);

  /** 不渲染的分组标识：有自己专属面板的分组（如通知偏好）从通用面板里排除。 */
  readonly exclude = input<readonly string[]>([]);

  protected readonly loading = this.pageState.loading;
  protected readonly loadError = this.pageState.loadError;

  /** 错误态里的重试：重取整页共用的快照，外壳的面板导航随之恢复。 */
  protected reload(): void {
    this.pageState.load();
  }

  /** 按行记录写入状态：用户会连续操作多个控件，整页一个状态无法把反馈落到对应行。 */
  private readonly rowState = signal<Record<string, RowState>>({});

  /** 写入链的队尾。见 {@link write} 里为什么必须串行。 */
  private writeQueue: Promise<void> = Promise.resolve();
  /** 每行最近一次写入的序号：同一行连续写入时，只有最后一次给出这一行的状态。 */
  private readonly writeGenerations = new Map<string, number>();
  protected readonly settings = this.pageState.settings;
  protected readonly drafts = signal<Record<string, string>>({});
  private readonly labelFns = new Map<string, (value: unknown) => string>();

  /** 发信参数的面板底部给"发送测试邮件"：改完参数最想确认的就是能否收到。 */
  protected readonly showEmailTest = computed(
    () => this.scope() === 'system' && this.groups().some((group) => group.key === 'Email'),
  );
  //#if (ExternalLogin)

  /** 外部登录分组的写入成功次数：提供方卡片据此在保存或恢复默认后重取生效状态。 */
  protected readonly externalLoginRevision = signal(0);
  //#endif

  /** 分组由后端下发的分组标识驱动，前端不另列清单，否则漏登记的新设置会从界面上静默消失。 */
  protected readonly groups = computed<SettingGroup[]>(() => {
    const groups = groupSettings(settingsInScope(this.settings(), this.scope()));
    const only = this.group();
    if (only) {
      return groups.filter((group) => groupPath(group.key) === only);
    }

    const excluded = new Set(this.exclude());
    return groups.filter((group) => !excluded.has(group.key));
  });

  /** 模板按它渲染：每个分组要走通用网格的行、是否统一说明生效范围，以及专属布局拆走的部分。 */
  protected readonly groupViews = computed<GroupView[]>(() =>
    this.groups().map((group) => {
      const view: GroupView = {
        ...group,
        rows: group.settings,
        hostScopeOnce: isHostScopeOnly(group.settings),
        //#if (ExternalLogin)
        externalLoginProviders: [],
        //#endif
      };
      //#if (ExternalLogin)

      // 外部登录按提供方分卡片，开关打开了没生效时管理员要在同一张卡片上看到缺什么；
      // 不符合命名约定的设置留在通用网格里，不能静默消失
      if (this.scope() === 'system' && group.key === EXTERNAL_LOGIN_GROUP) {
        const { providers, others } = partitionExternalLoginSettings(group.settings);
        view.rows = others;
        view.externalLoginProviders = providers;
      }
      //#endif

      return view;
    }),
  );

  /** 已在分组顶部说过生效范围的分组：其中的行不再各挂一个同样的徽章。 */
  private readonly hostScopeOnceGroups = computed(
    () => new Set(this.groupViews().flatMap((view) => (view.hostScopeOnce ? [view.key] : []))),
  );

  /** 该行是否单独挂"全实例生效"徽章：进程级设置，且所在分组没有在顶部统一说明。 */
  protected showsRowHostScope(setting: SettingOutputDto): boolean {
    return setting.allowsHostScope && !this.hostScopeOnceGroups().has(setting.group);
  }

  /** 输入框显示本层覆盖值而非生效值，否则租户页会显示个人偏好，保存即写成租户默认值。 */
  protected draftOf(setting: SettingOutputDto, scope: SettingScope): string {
    const draft = this.drafts()[this.draftKey(setting, scope)];
    if (draft !== undefined) {
      return draft;
    }

    // 进程级设置存在宿主的租户层那一行上，因此这里同样读 tenantValue
    return (scope === 'account' ? setting.userValue : setting.tenantValue) ?? '';
  }

  /**
   * 时区候选在字段上构造一次：要为四百多个时区构造 `Intl` formatter，结果只取决于运行时环境。
   * 不要改成模板里调用的函数，那会每轮变更检测重跑。
   */
  protected readonly timeZones = timeZoneGroups();

  private readonly timeZoneIndex = new Map(
    this.timeZones.flatMap((group) => group.zones.map((zone) => [zone.value, zone] as const)),
  );

  /** 时区用可搜索的分组下拉：四百多项无搜索难以选择，手挑短清单又会漏掉部署地。 */
  protected isTimeZone(setting: SettingOutputDto): boolean {
    return setting.name === SETTINGS.display.timeZone;
  }

  /** combobox 的过滤器拿到的是选项的 value（IANA 名），按它取回选项再做匹配。 */
  protected readonly timeZoneFilter = (value: unknown, search: string): boolean => {
    const option = this.timeZoneIndex.get(String(value ?? ''));
    return option ? timeZoneMatches(option, search) : false;
  };

  /** 选项的文案取 IANA 名：存的就是它，偏移由触发器另行拼接（见 {@link timeZoneTextOf}）。 */
  protected readonly timeZoneToString = (value: unknown): string => String(value ?? '');

  /**
   * 时区在触发器上分两段写：`Asia/Shanghai · UTC+8`。放不下时只省略时区名，偏移始终可见——
   * 确认时区多半看的是偏移。选中值与继承来的占位同一写法；占位是"跟随系统"这类文案时没有偏移。
   */
  protected timeZoneTextOf(
    setting: SettingOutputDto,
    scope: SettingScope,
    labels: InheritedSourceLabels,
  ): { name: string; offset: string } {
    const name = this.displayTextOf(setting, scope, labels);
    return { name, offset: timeZoneOffsetLabel(name) };
  }

  /** 这一项当前的写入状态；没有即常态。 */
  protected stateOf(setting: SettingOutputDto, scope: SettingScope): RowState | undefined {
    return this.rowState()[this.draftKey(setting, scope)];
  }

  /** 取值封闭的设置渲染下拉，其余渲染文本框；返回 undefined 表示没有候选项。 */
  protected choicesOf(setting: SettingOutputDto): readonly SettingChoice[] | undefined {
    return SETTING_CHOICES[setting.name];
  }

  /** 行下方描述的 id：开关不会自动关联所在字段的描述，要显式传给它的 aria-describedby。 */
  protected descriptionIdOf(setting: SettingOutputDto): string {
    return `${setting.name}-description`;
  }

  /** 布尔设置由服务端标注并渲染为开关；前端不另列清单，免得漏登记的项渲染成文本框。 */
  protected isBoolean(setting: SettingOutputDto): boolean {
    return setting.isBoolean === true;
  }

  /** 机密设置（加密落库）用口令框：服务端从不下发其值，框里只有本次输入。 */
  protected inputTypeOf(setting: SettingOutputDto): string {
    if (setting.isSecret) {
      return 'password';
    }
    return this.isNumeric(setting) ? 'number' : 'text';
  }

  /** 带取值区间的设置用数字输入框，并把上下界交给浏览器。 */
  protected isNumeric(setting: SettingOutputDto): boolean {
    // 按"是不是数"判定，不按"是不是 null"：服务端省掉值为 null 的属性，非数值型设置根本不带这两个字段
    return typeof setting.minimum === 'number' && typeof setting.maximum === 'number';
  }

  /** 开关显示生效值（本层未覆盖时取继承值）：开关无法用占位符表达"跟随上层"。 */
  protected isChecked(setting: SettingOutputDto, scope: SettingScope): boolean {
    return (this.draftOf(setting, scope) || this.inheritedOf(setting, scope)) === 'true';
  }

  /**
   * 「跟随系统」时本设备上实际生效的值：语言与时区三层都没设时，占位符须显示探测到的值，
   * 否则空框看着像没配置。只对个人页有意义——系统页的"跟随系统"指组织里每台设备各自决定，
   * 管理员自己这台设备的探测值不是组织的值，显示出来会误导。
   */
  protected systemDefaultOf(setting: SettingOutputDto, scope: SettingScope): string {
    if (scope !== 'account') {
      return '';
    }

    if (setting.name === SETTINGS.display.timeZone) {
      return browserTimeZone() ?? '';
    }

    //#if (IncludeLocalization)
    if (setting.name === SETTINGS.display.language) {
      return this.languageService.activeLang();
    }

    //#endif
    return '';
  }

  /** 候选项的展示文案；没有匹配项时退回原始值。 */
  protected labelOf(setting: SettingOutputDto, value: string): string {
    const choice = this.choicesOf(setting)?.find((c) => c.value === value);
    return choice ? this.choiceLabelOf(choice) : value;
  }

  /** 一个候选项的文案：带词条的按当前语言翻译。 */
  protected choiceLabelOf(choice: SettingChoice): string {
    if (!choice.labelKey) {
      return choice.label;
    }

    //#if (IncludeLocalization)
    return this.transloco.translate(choice.labelKey);
    //#else
    return this.t(choice.labelKey);
    //#endif
  }

  /**
   * 下拉的取名函数按设置名（与界面语言）缓存：每轮新建闭包会让 `itemToString` 输入变化，触发反复重算；
   * 换了语言则要换一个新函数，选中项的文字才会跟着变。
   */
  protected labelFnOf(setting: SettingOutputDto): (value: unknown) => string {
    //#if (IncludeLocalization)
    const key = `${this.languageService.activeLang()}:${setting.name}`;
    //#else
    const key = setting.name;
    //#endif
    let fn = this.labelFns.get(key);
    if (!fn) {
      fn = (value: unknown) => this.labelOf(setting, String(value ?? ''));
      this.labelFns.set(key, fn);
    }

    return fn;
  }

  /** 下拉选中即写入：下拉没有"编辑中"的中间态，再要求点保存只是多一步。 */
  protected onSelect(
    setting: SettingOutputDto,
    scope: SettingScope,
    value: string | null | undefined,
  ): void {
    // 值没变就不写：spartan 的 select 在重新渲染时也会发一次 valueChange。
    if ((value ?? '') === this.draftOf(setting, scope)) {
      return;
    }

    void this.write(setting, value ? value : null, scope);
  }

  /** 本层未覆盖时以占位符展示继承来的值：账户继承系统默认值，系统继承代码默认值。 */
  protected inheritedOf(setting: SettingOutputDto, scope: SettingScope): string {
    const inherited =
      scope === 'account' ? (setting.tenantValue ?? setting.defaultValue) : setting.defaultValue;
    return inherited ?? '';
  }

  /**
   * 继承值的展示文案（占位符）：只写正在生效的值，有候选项时换成标签，避免同一控件上出现「中文」与
   * `zh-CN` 两种写法。值从哪来由 {@link inheritedSourceOf} 写在标签后面：两者拼在一起时，设置页的
   * 固定宽度放不下，被省略的正是值。没有继承值时看有没有"跟随系统"探测到的值，空占位符会让正在
   * 生效的值看不见；探测不到（系统页不探测管理员本机）时占位就是"跟随系统"。
   *
   * @param labels 来源文案，由模板按当前语言传入。
   */
  protected inheritedLabelOf(
    setting: SettingOutputDto,
    scope: SettingScope,
    labels: InheritedSourceLabels,
  ): string {
    const inherited = this.inheritedOf(setting, scope) || this.systemDefaultOf(setting, scope);
    if (inherited.length > 0) {
      return this.labelOf(setting, inherited);
    }

    return isDeviceDefaulted(setting) ? labels.followSystem : '';
  }

  /**
   * 语言与时区在本层未覆盖时，说清生效值从哪来（个人 → 组织 → 部署基线 → 设备）：个人页继承到的是
   * "跟随组织"（组织值或部署基线），系统页继承到的是"部署默认"，用的是设备探测值时是"跟随系统"。
   * 本层已覆盖、或占位本身已是"跟随系统"时不写。
   *
   * @param labels 来源文案，由模板按当前语言传入。
   */
  protected inheritedSourceOf(
    setting: SettingOutputDto,
    scope: SettingScope,
    labels: InheritedSourceLabels,
  ): string {
    if (!isDeviceDefaulted(setting) || this.draftOf(setting, scope).length > 0) {
      return '';
    }

    if (this.inheritedOf(setting, scope).length > 0) {
      return scope === 'account' ? labels.followOrganization : labels.deploymentDefault;
    }

    return this.systemDefaultOf(setting, scope).length > 0 ? labels.followSystem : '';
  }

  /** 触发器上正在显示的全文：本层的值或继承来的占位。放不下时控件里单行省略，悬停提示给出全文。 */
  protected displayTextOf(
    setting: SettingOutputDto,
    scope: SettingScope,
    labels: InheritedSourceLabels,
  ): string {
    const draft = this.draftOf(setting, scope);
    return draft.length > 0
      ? this.labelOf(setting, draft)
      : this.inheritedLabelOf(setting, scope, labels);
  }

  protected onInput(setting: SettingOutputDto, scope: SettingScope, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.drafts.update((drafts) => ({ ...drafts, [this.draftKey(setting, scope)]: value }));
  }

  /** 输入类控件在原生 `change`（失焦或回车）时提交，不按键即写：输入 60 会途经同样合法的 6。 */
  protected onCommit(setting: SettingOutputDto, scope: SettingScope, event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim();

    // 写入裁剪后的值：草稿随之同步（见 write），界面显示的就是实际存入的值。
    void this.write(setting, value.length === 0 ? null : value, scope);
  }

  /** 开关：状态本身就是提交，没有中间态。 */
  protected onToggle(setting: SettingOutputDto, scope: SettingScope, checked: boolean): void {
    void this.write(setting, checked ? 'true' : 'false', scope);
  }

  /** 选中的日志级别的说明文案键；不是日志级别或认不出的取值返回空串。 */
  protected levelHintKeyOf(setting: SettingOutputDto, scope: SettingScope): string {
    if (
      setting.name !== SETTINGS.logging.minimumLevel &&
      setting.name !== SETTINGS.logging.requestLevel
    ) {
      return '';
    }

    const effective = this.draftOf(setting, scope) || this.systemOrInheritedValue(setting, scope);
    return LOG_LEVEL_DESCRIPTIONS[effective] ?? '';
  }

  /** 本层没设值时实际生效的那个值（继承来的，或跟随系统探测到的）。 */
  protected systemOrInheritedValue(setting: SettingOutputDto, scope: SettingScope): string {
    return this.inheritedOf(setting, scope) || this.systemDefaultOf(setting, scope);
  }

  // 同一项设置在两个作用域里各有独立草稿：同名共用一份会让个人偏好的未保存输入
  // 出现在系统默认值的输入框里。
  private draftKey(setting: SettingOutputDto, scope: SettingScope): string {
    return `${scope}:${setting.name}`;
  }

  /** 清除本层级的值，读取回落到下一层。 */
  protected reset(setting: SettingOutputDto, scope: SettingScope): void {
    void this.write(setting, null, scope);
  }

  /** 写入一项：经 {@link enqueue} 串行执行 PUT → 重取快照 → 全局发布。 */
  private write(setting: SettingOutputDto, value: string | null, scope: SettingScope): void {
    const key = this.draftKey(setting, scope);
    // 控件在写入期间照常可用，焦点留在原处：禁用会把焦点弹到页面上，键盘与读屏用户得从头找回来。
    // 再次操作就再排一次写入，以最后一次为准。草稿即最新意图：前一次写入刷新的快照不会把控件拨回旧值。
    this.drafts.update((drafts) => ({ ...drafts, [key]: value ?? '' }));
    const generation = (this.writeGenerations.get(key) ?? 0) + 1;
    this.writeGenerations.set(key, generation);
    const isLatest = () => this.writeGenerations.get(key) === generation;
    this.setRowState(key, { status: 'saving' });

    // 串行：两条链并发时 GET 响应可能乱序，旧快照会覆盖新快照。
    const startedAt = Date.now();
    this.enqueue(async () => {
      try {
        const request =
          scope === 'account'
            ? this.settingService.setForCurrentUser({ name: setting.name, value })
            : this.settingService.setForCurrentTenant({ name: setting.name, value });

        await firstValueFrom(request);
        //#if (ExternalLogin)
        // 写入已生效就重取状态，不等快照：快照刷新失败不影响本实例的方案目录已经变了
        if (setting.group === EXTERNAL_LOGIN_GROUP) {
          this.externalLoginRevision.update((revision) => revision + 1);
        }
        //#endif

        // 走会话上下文而非 load()：语言由设置派生，只刷新数据不会重新应用；
        // 本页的快照就是会话快照，刷新完即已更新。
        await this.sessionContext.refreshSettings();

        // 快照到手后才丢草稿，否则 PUT 成功而 GET 失败时界面会退回旧值。只丢最后一次写入、且仍等于
        // 本次写入值的草稿：之后又排了写入时草稿属于那一次（A→B→A 里两个 A 不是同一份意图）；
        // 回车提交后用户可以接着改，尚未提交的新输入也不能被抹掉。
        if (isLatest()) {
          this.drafts.update((drafts) => {
            if (drafts[key] !== (value ?? '')) {
              return drafts;
            }

            const next = { ...drafts };
            delete next[key];
            return next;
          });
        }

        await this.publishRowState(key, generation, startedAt, { status: 'saved' });
      } catch (error: unknown) {
        // 失败就地报在该行且不自动消失（toast 会离开该行且易被顶掉）；上一份快照仍有效。
        await this.publishRowState(key, generation, startedAt, {
          status: 'error',
          message: applicationErrorMessage(error),
        });
      }
    });
  }

  /** 写入链串行化：前一条失败也要让后一条继续。 */
  private enqueue(task: () => Promise<void>): void {
    this.writeQueue = this.writeQueue.then(task, task);
  }

  private setRowState(key: string, state: RowState): void {
    this.rowState.update((current) => ({ ...current, [key]: state }));
  }

  /** 把"保存中"补到最短可见时长；已经够久了就立刻返回。 */
  private holdSavingState(startedAt: number): Promise<void> {
    const remaining = SAVING_MIN_MS - (Date.now() - startedAt);
    return remaining <= 0
      ? Promise.resolve()
      : new Promise<void>((resolve) => setTimeout(resolve, remaining));
  }

  /**
   * 发布一次写入的结果：这一行之后又有写入时，本次结果不再代表这一行，状态由最后一次写入给出。
   * 补足"保存中"的最短时长之后再判一次：等待期间又排进来的写入正在保存，旧结果不能盖住它。
   */
  private async publishRowState(
    key: string,
    generation: number,
    startedAt: number,
    state: RowState,
  ): Promise<void> {
    if (this.writeGenerations.get(key) !== generation) {
      return;
    }

    await this.holdSavingState(startedAt);
    if (this.writeGenerations.get(key) !== generation) {
      return;
    }

    if (state.status === 'saved') {
      this.markSaved(key, generation);
    } else {
      this.setRowState(key, state);
    }
  }

  /**
   * 成功状态是暂时的：留一小会儿让人看见，然后回到常态。只清本次写入的提示：之后那次写入的提示
   * 按它自己的时长停留。
   */
  private markSaved(key: string, generation: number): void {
    this.setRowState(key, { status: 'saved' });
    setTimeout(() => {
      if (
        this.writeGenerations.get(key) === generation &&
        this.rowState()[key]?.status === 'saved'
      ) {
        this.rowState.update((current) => {
          const next = { ...current };
          delete next[key];
          return next;
        });
      }
    }, SAVED_HINT_MS);
  }
}

/** 组织与部署都没设时由设备决定的设置（语言与时区）：占位文案要说清值从哪层来。 */
function isDeviceDefaulted(setting: SettingOutputDto): boolean {
  //#if (IncludeLocalization)
  if (setting.name === SETTINGS.display.language) {
    return true;
  }

  //#endif
  return setting.name === SETTINGS.display.timeZone;
}

/** 整组都是进程级设置（与每行"全实例生效"徽章同一判据）；空组不算，没有行可说明。 */
function isHostScopeOnly(settings: readonly SettingOutputDto[]): boolean {
  return settings.length > 0 && settings.every((setting) => setting.allowsHostScope);
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.followSystem': 'Follows your system',
  'settings.followOrganization': 'Follows your organization',
  'settings.deploymentDefault': 'Deployment default',
  'settings.timeZoneSearch': 'Search city or UTC offset',
  'settings.timeZoneToggle': 'Show time zones',
  'settings.timeZoneEmpty': 'No matching time zone',
  'settings.browserTimeZone': 'browser',
  'settings.secretSet': 'Set (type a new value to replace it)',
  'settings.secretUnset': 'Not set',
  'settings.reset': 'Reset to default',
  'settings.hostScope.hint':
    'One value for the whole system; changes reach every server within about 30 seconds',
  'settings.savedEverywhere': 'Saved; reaches every server within about 30 seconds',
  'settings.saving': 'Saving…',
  'settings.saved': 'Saved',
  'settings.empty': 'No configurable items in this section',
  'settings.loadFailed': "Couldn't load settings",
  'theme.mode.light': 'Light',
  'theme.mode.system': 'System',
  'theme.mode.dark': 'Dark',
  'common.retry': 'Retry',
  'settings.logLevelHints.Verbose':
    'Logs everything, including every SQL statement and request detail. For short troubleshooting only; left on, it fills the disk quickly.',
  'settings.logLevelHints.Debug':
    'Logs debugging details. Turn it on while investigating; not recommended day to day.',
  'settings.logLevelHints.Information': 'Logs the normal business flow. The default level.',
  'settings.logLevelHints.Warning':
    'Logs only warnings and errors; the normal flow is not written.',
  'settings.logLevelHints.Error':
    'Logs only errors. You may miss clues that are wrong without raising an error.',
  'settings.logLevelHints.Fatal':
    'Logs only failures that stop the process. Almost the same as turning logging off.',
};
//#endif
