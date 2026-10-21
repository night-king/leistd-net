// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  //#if (IncludeLocalization)
  inject,
  //#endif
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { form, required, disabled, validate, FormField } from '@angular/forms/signals';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService, translateSignal } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCircleCheck } from '@ng-icons/lucide';
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDialogImports } from '@spartan-ng/helm/dialog';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmSwitch } from '@spartan-ng/helm/switch';

import { DialogLoading } from '../../../../../../shared/components/dialog-loading/dialog-loading';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import {
  CreateOpenApplicationInputDto,
  OpenApplicationClientType,
  OpenApplicationOutputDto,
  OpenApplicationScopeOutputDto,
  OpenApplicationType,
  UpdateOpenApplicationInputDto,
} from '../../../../dtos/open-application.dto';
import { UriListEditor } from '../uri-list-editor/uri-list-editor';

type OpenApplicationTemplate = 'web' | 'desktop' | 'service';

interface OpenApplicationEditFormModel {
  clientId: string;
  displayName: string;
  applicationType: OpenApplicationType;
  clientType: OpenApplicationClientType;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
  requirements: string[];
  /** `null`：登记早于该设置，保存前须明确选择。 */
  sessionBound: boolean | null;
}

const authorizationCodePermissions = [
  'ept:authorization',
  'ept:end_session',
  'ept:token',
  'gt:authorization_code',
  'gt:refresh_token',
  'rst:code',
  'scp:openid',
  'scp:profile',
  'scp:email',
  'scp:roles',
  'scp:offline_access',
];

@Component({
  selector: 'app-open-application-edit-dialog',
  imports: [
    FormField,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmInput,
    HlmSpinner,
    HlmSeparator,
    HlmSwitch,
    ...HlmDialogImports,
    ...HlmFieldImports,
    ...HlmSelectImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    DialogLoading,
    UriListEditor,
  ],
  providers: [provideIcons({ lucideCircleCheck })],
  templateUrl: './open-application-edit-dialog.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpenApplicationEditDialog {
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif
  readonly visible = model(false);
  readonly loading = input(false);
  readonly saving = input(false);
  readonly application = input<OpenApplicationOutputDto | null>(null);
  /** 可授予的 scope，由父页面从服务端取得：服务端能签发哪些 scope（含配置的下游 API）只有它知道。 */
  readonly scopes = input<OpenApplicationScopeOutputDto[]>([]);
  readonly saved = output<CreateOpenApplicationInputDto | UpdateOpenApplicationInputDto>();

  // 标准 scope 用本地译名，其余（本服务与下游 API、机器 scope）用服务端给的展示名
  private readonly scopeOptions = computed(() =>
    this.scopes().map((scope) => ({
      label: this.permissionLabels()[`scp:${scope.name}`] ?? scope.displayName,
      value: `scp:${scope.name}`,
      group: 'Scopes',
    })),
  );

  protected readonly formModel = signal<OpenApplicationEditFormModel>(this.createEmptyModel());
  selectedTemplate = signal<OpenApplicationTemplate | null>(null);

  isEditMode = computed(() => !!this.application());
  isTemplateLocked = computed(() => !!this.selectedTemplate() && !this.isEditMode());
  isConfidentialClient = computed(() => this.formModel().clientType === 'confidential');
  isServiceType = computed(() => this.formModel().applicationType === 'service');

  readonly applicationForm = form(this.formModel, (path) => {
    required(path.clientId, { when: () => !this.isEditMode() });
    disabled(path.clientId, { when: () => this.isEditMode() });
    // 跨字段：Native / Public 客户端必须启用 PKCE。
    validate(path.requirements, (ctx) => {
      const requirements = ctx.value();
      const applicationType = ctx.valueOf(path.applicationType);
      const clientType = ctx.valueOf(path.clientType);
      return (applicationType === 'native' || clientType === 'public') &&
        !requirements.includes('ft:pkce')
        ? { kind: 'pkceRequired' }
        : null;
    });
    // 会话绑定没有安全的默认值：存量登记未设置时必须由管理员明确选择（机器客户端不涉及会话，保存时固定为否）。
    validate(path.sessionBound, (ctx) =>
      ctx.value() === null && ctx.valueOf(path.applicationType) !== 'service'
        ? { kind: 'required' }
        : null,
    );
  });

  //#if (IncludeLocalization)
  // 这些文案不经模板直接交给下拉与触发器：取成翻译信号，词条到达与语言切换时随之重算
  private readonly texts = {
    templateWeb: translateSignal('openApp.template.web', {}, { scope: 'openApp' }),
    templateDesktop: translateSignal('openApp.template.desktop', {}, { scope: 'openApp' }),
    templateService: translateSignal('openApp.template.service', {}, { scope: 'openApp' }),
    appTypeWeb: translateSignal('openApp.appType.web', {}, { scope: 'openApp' }),
    appTypeNative: translateSignal('openApp.appType.native', {}, { scope: 'openApp' }),
    appTypeService: translateSignal('openApp.appType.service', {}, { scope: 'openApp' }),
    clientTypePublic: translateSignal('openApp.clientType.publicLabel', {}, { scope: 'openApp' }),
    clientTypeConfidential: translateSignal(
      'openApp.clientType.confidentialLabel',
      {},
      { scope: 'openApp' },
    ),
    authorizationEndpoint: translateSignal(
      'openApp.permission.authorizationEndpoint',
      {},
      { scope: 'openApp' },
    ),
    tokenEndpoint: translateSignal('openApp.permission.tokenEndpoint', {}, { scope: 'openApp' }),
    endSessionEndpoint: translateSignal(
      'openApp.permission.endSessionEndpoint',
      {},
      { scope: 'openApp' },
    ),
    authorizationCode: translateSignal(
      'openApp.permission.authorizationCode',
      {},
      { scope: 'openApp' },
    ),
    authorizationCodeFlow: translateSignal(
      'openApp.permission.authorizationCodeFlow',
      {},
      { scope: 'openApp' },
    ),
    refreshToken: translateSignal('openApp.permission.refreshToken', {}, { scope: 'openApp' }),
    tokenExchange: translateSignal('openApp.permission.tokenExchange', {}, { scope: 'openApp' }),
    clientCredentials: translateSignal(
      'openApp.permission.clientCredentials',
      {},
      { scope: 'openApp' },
    ),
    codeResponse: translateSignal('openApp.permission.codeResponse', {}, { scope: 'openApp' }),
    scopeOpenid: translateSignal('openApp.permission.scopeOpenid', {}, { scope: 'openApp' }),
    scopeProfile: translateSignal('openApp.permission.scopeProfile', {}, { scope: 'openApp' }),
    scopeEmail: translateSignal('openApp.permission.scopeEmail', {}, { scope: 'openApp' }),
    scopeRoles: translateSignal('openApp.permission.scopeRoles', {}, { scope: 'openApp' }),
    scopeOfflineAccess: translateSignal(
      'openApp.permission.scopeOfflineAccess',
      {},
      { scope: 'openApp' },
    ),
    forcePkce: translateSignal('openApp.requirement.forcePkce', {}, { scope: 'openApp' }),
  };

  readonly templateOptions = computed(() => [
    { label: this.texts.templateWeb(), value: 'web' as const },
    { label: this.texts.templateDesktop(), value: 'desktop' as const },
    { label: this.texts.templateService(), value: 'service' as const },
  ]);

  readonly applicationTypeOptions = computed(() => [
    { label: this.texts.appTypeWeb(), value: 'web' as const },
    { label: this.texts.appTypeNative(), value: 'native' as const },
    { label: this.texts.appTypeService(), value: 'service' as const },
  ]);

  readonly clientTypeOptions = computed(() => [
    { label: this.texts.clientTypePublic(), value: 'public' as const },
    { label: this.texts.clientTypeConfidential(), value: 'confidential' as const },
  ]);

  readonly permissionOptions = computed(() => [
    { label: this.texts.authorizationEndpoint(), value: 'ept:authorization', group: 'Endpoints' },
    { label: this.texts.tokenEndpoint(), value: 'ept:token', group: 'Endpoints' },
    { label: this.texts.endSessionEndpoint(), value: 'ept:end_session', group: 'Endpoints' },
    { label: this.texts.authorizationCode(), value: 'gt:authorization_code', group: 'Grant Types' },
    { label: this.texts.refreshToken(), value: 'gt:refresh_token', group: 'Grant Types' },
    { label: this.texts.clientCredentials(), value: 'gt:client_credentials', group: 'Grant Types' },
    {
      label: this.texts.tokenExchange(),
      value: 'gt:urn:ietf:params:oauth:grant-type:token-exchange',
      group: 'Grant Types',
    },
    { label: this.texts.codeResponse(), value: 'rst:code', group: 'Response Types' },
    ...this.scopeOptions(),
    ...this.scopes()
      .filter((scope) => !!scope.audience)
      .map((scope) => ({
        label: scope.displayName,
        value: `aud:${scope.audience}`,
        group: 'Audiences',
      })),
  ]);

  readonly requirementOptions = computed(() => [
    { label: this.texts.forcePkce(), value: 'ft:pkce' },
  ]);

  readonly permissionLabels = computed<Record<string, string>>(() => ({
    'ept:authorization': this.texts.authorizationEndpoint(),
    'ept:token': this.texts.tokenEndpoint(),
    'ept:end_session': this.texts.endSessionEndpoint(),
    'gt:authorization_code': this.texts.authorizationCodeFlow(),
    'gt:refresh_token': this.texts.refreshToken(),
    'gt:client_credentials': this.texts.clientCredentials(),
    'gt:urn:ietf:params:oauth:grant-type:token-exchange': this.texts.tokenExchange(),
    'rst:code': this.texts.codeResponse(),
    'scp:openid': this.texts.scopeOpenid(),
    'scp:profile': this.texts.scopeProfile(),
    'scp:email': this.texts.scopeEmail(),
    'scp:roles': this.texts.scopeRoles(),
    'scp:offline_access': this.texts.scopeOfflineAccess(),
  }));

  readonly applicationTypeLabels = computed<Record<string, string>>(() => ({
    web: this.texts.appTypeWeb(),
    native: this.texts.appTypeNative(),
    service: this.texts.appTypeService(),
  }));

  readonly clientTypeLabels = computed<Record<string, string>>(() => ({
    public: this.texts.clientTypePublic(),
    confidential: this.texts.clientTypeConfidential(),
  }));
  //#else
  readonly templateOptions = computed(() => [
    { label: 'Web PKCE client', value: 'web' as const },
    { label: 'Desktop PKCE client', value: 'desktop' as const },
    { label: 'Service confidential client', value: 'service' as const },
  ]);

  readonly applicationTypeOptions = computed(() => [
    { label: 'Web', value: 'web' as const },
    { label: 'Desktop/Native', value: 'native' as const },
    { label: 'Service', value: 'service' as const },
  ]);

  readonly clientTypeOptions = computed(() => [
    { label: 'Public', value: 'public' as const },
    { label: 'Confidential', value: 'confidential' as const },
  ]);

  readonly permissionOptions = computed(() => [
    { label: 'Authorization endpoint', value: 'ept:authorization', group: 'Endpoints' },
    { label: 'Token endpoint', value: 'ept:token', group: 'Endpoints' },
    { label: 'End session endpoint', value: 'ept:end_session', group: 'Endpoints' },
    { label: 'Authorization code', value: 'gt:authorization_code', group: 'Grant Types' },
    { label: 'Refresh token', value: 'gt:refresh_token', group: 'Grant Types' },
    { label: 'Client credentials', value: 'gt:client_credentials', group: 'Grant Types' },
    {
      label: 'Token Exchange',
      value: 'gt:urn:ietf:params:oauth:grant-type:token-exchange',
      group: 'Grant Types',
    },
    { label: 'Code response', value: 'rst:code', group: 'Response Types' },
    ...this.scopeOptions(),
    ...this.scopes()
      .filter((scope) => !!scope.audience)
      .map((scope) => ({
        label: scope.displayName,
        value: `aud:${scope.audience}`,
        group: 'Audiences',
      })),
  ]);

  readonly requirementOptions = computed(() => [{ label: 'Force PKCE', value: 'ft:pkce' }]);

  readonly permissionLabels = computed<Record<string, string>>(() => ({
    'ept:authorization': 'Authorization endpoint',
    'ept:token': 'Token endpoint',
    'ept:end_session': 'End session endpoint',
    'gt:authorization_code': 'Authorization code flow',
    'gt:refresh_token': 'Refresh token',
    'gt:client_credentials': 'Client credentials',
    'gt:urn:ietf:params:oauth:grant-type:token-exchange': 'Token Exchange',
    'rst:code': 'Code response',
    'scp:openid': 'Identity',
    'scp:profile': 'Profile',
    'scp:email': 'Email',
    'scp:roles': 'Roles',
    'scp:offline_access': 'Offline access',
  }));

  readonly applicationTypeLabels = computed<Record<string, string>>(() => ({
    web: 'Web',
    native: 'Desktop/Native',
    service: 'Service',
  }));

  readonly clientTypeLabels = computed<Record<string, string>>(() => ({
    public: 'Public',
    confidential: 'Confidential',
  }));
  //#endif

  /**
   * Select 触发器上的文本：不传 `itemToString` 会显示原始值（如 `native`）。写成箭头函数属性，
   * 传给 input 的引用须稳定。
   */
  readonly applicationTypeToLabel = (value: string): string =>
    this.applicationTypeLabels()[value] ?? value;

  readonly clientTypeToLabel = (value: string): string => this.clientTypeLabels()[value] ?? value;

  readonly templateToLabel = (value: string): string =>
    this.templateOptions().find((option) => option.value === value)?.label ?? value;

  constructor() {
    // 同时依赖 visible 与 application：每次对话框打开都重置表单，避免新建模式残留上次输入
    // （application 信号从 null 到 null 不变化时 effect 不会重跑，需借 visible 触发）
    effect(() => {
      const application = this.application();
      if (!this.visible()) {
        return;
      }
      if (application) {
        this.formModel.set({
          clientId: application.clientId,
          displayName: application.displayName ?? '',
          applicationType: application.applicationType,
          clientType: application.clientType,
          redirectUris: [...application.redirectUris],
          postLogoutRedirectUris: [...application.postLogoutRedirectUris],
          permissions: [...application.permissions],
          requirements: [...application.requirements],
          // 编辑不重置：沿用已登记的值，未设置的保持未设置
          sessionBound: application.sessionBound,
        });
      } else {
        this.formModel.set(this.createEmptyModel());
      }
    });
  }

  createEmptyModel(): OpenApplicationEditFormModel {
    return {
      clientId: '',
      displayName: '',
      applicationType: 'web',
      clientType: 'public',
      redirectUris: [],
      postLogoutRedirectUris: [],
      permissions: [...authorizationCodePermissions],
      requirements: ['ft:pkce'],
      // 新建默认是浏览器应用，随登录会话收敛
      sessionBound: true,
    };
  }

  applyTemplate(template: OpenApplicationTemplate | null | undefined) {
    if (!template) {
      this.selectedTemplate.set(null);
      return;
    }
    this.selectedTemplate.set(template);
    if (template === 'desktop') {
      this.formModel.update((model) => ({
        ...model,
        clientId: model.clientId || 'my-desktop-app',
        //#if (IncludeLocalization)
        displayName:
          model.displayName || this.transloco.translate('openApp.template.desktopDisplayName'),
        //#else
        displayName: model.displayName || 'My Desktop App',
        //#endif
        applicationType: 'native',
        clientType: 'public',
        redirectUris: ['my-desktop-app://oauth/callback'],
        postLogoutRedirectUris: ['my-desktop-app://oauth/logout-callback'],
        permissions: [...authorizationCodePermissions],
        requirements: ['ft:pkce'],
        // 桌面端持有自己的刷新令牌离线续期，不跟随浏览器登录会话
        sessionBound: false,
      }));
      return;
    }

    if (template === 'service') {
      this.formModel.update((model) => ({
        ...model,
        applicationType: 'service',
        clientType: 'confidential',
        redirectUris: [],
        postLogoutRedirectUris: [],
        permissions: ['ept:token', 'gt:client_credentials'],
        requirements: [],
        sessionBound: false,
      }));
      return;
    }

    this.formModel.update((model) => ({
      ...model,
      applicationType: 'web',
      clientType: 'public',
      permissions: [...authorizationCodePermissions],
      requirements: ['ft:pkce'],
      sessionBound: true,
    }));
  }

  // hlm-switch 为 CVA（checked 非 ModelSignal），Signal Forms 的 [formField] 不适配；
  // 直接以 [checked]/(checkedChange) 回写模型信号。
  setSessionBound(checked: boolean): void {
    this.formModel.update((model) => ({ ...model, sessionBound: checked }));
    this.applicationForm.sessionBound().markAsTouched();
  }

  onApplicationTypeChange(value: OpenApplicationType | null | undefined) {
    if (!value) {
      return;
    }
    this.formModel.update((model) => ({ ...model, applicationType: value }));
  }

  onClientTypeSelect(clientType: OpenApplicationClientType | null | undefined) {
    if (!clientType) {
      return;
    }
    this.formModel.update((model) => ({
      ...model,
      clientType,
      requirements:
        clientType === 'public'
          ? this.ensurePkce(model.requirements)
          : model.requirements.filter((r) => r !== 'ft:pkce'),
    }));
  }

  addRedirectUri(uri: string) {
    this.formModel.update((model) => ({
      ...model,
      redirectUris: model.redirectUris.includes(uri)
        ? model.redirectUris
        : [...model.redirectUris, uri],
    }));
  }

  addPostLogoutRedirectUri(uri: string) {
    this.formModel.update((model) => ({
      ...model,
      postLogoutRedirectUris: model.postLogoutRedirectUris.includes(uri)
        ? model.postLogoutRedirectUris
        : [...model.postLogoutRedirectUris, uri],
    }));
  }

  removeRedirectUri(uri: string) {
    this.formModel.update((model) => ({
      ...model,
      redirectUris: model.redirectUris.filter((item) => item !== uri),
    }));
  }

  removePostLogoutRedirectUri(uri: string) {
    this.formModel.update((model) => ({
      ...model,
      postLogoutRedirectUris: model.postLogoutRedirectUris.filter((item) => item !== uri),
    }));
  }

  /** 桥接 hlm-dialog 声明式 state 到对外 visible 契约。 */
  onDialogStateChange(state: BrnDialogState): void {
    this.visible.set(state === 'open');
    if (state === 'closed') {
      this.onHide();
    }
  }

  onHide() {
    this.visible.set(false);
    this.formModel.set(this.createEmptyModel());
    this.selectedTemplate.set(null);
  }

  save() {
    if (this.applicationForm().invalid()) {
      this.applicationForm().markAsTouched();
      return;
    }

    const model = this.formModel();
    // 校验已保证非服务类型时有值；机器客户端没有用户会话，固定为否
    const sessionBound = model.applicationType === 'service' ? false : model.sessionBound === true;
    if (this.isEditMode()) {
      this.saved.emit({
        displayName: model.displayName,
        applicationType: model.applicationType,
        clientType: model.clientType,
        redirectUris: model.redirectUris,
        postLogoutRedirectUris: model.postLogoutRedirectUris,
        permissions: model.permissions,
        requirements: model.requirements,
        sessionBound,
      });
      return;
    }

    this.saved.emit({
      clientId: model.clientId.trim(),
      displayName: model.displayName,
      applicationType: model.applicationType,
      clientType: model.clientType,
      redirectUris: model.redirectUris,
      postLogoutRedirectUris: model.postLogoutRedirectUris,
      permissions: model.permissions,
      requirements: model.requirements,
      sessionBound,
    });
  }

  private ensurePkce(requirements: string[]) {
    return requirements.includes('ft:pkce') ? requirements : [...requirements, 'ft:pkce'];
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'openApp.dialog.editHeader': 'Edit open application',
  'openApp.dialog.createHeader': 'New open application',
  'openApp.dialog.description': "Configure the client's identity, type, and OAuth capabilities.",
  'openApp.dialog.loading': 'Loading open application...',
  'openApp.field.template': 'Application template',
  'openApp.field.templatePlaceholder': 'Select a template to quickly fill in',
  'openApp.field.displayName': 'Application name',
  'openApp.field.displayNamePlaceholder': 'e.g. My Desktop App',
  'openApp.field.clientId': 'Client ID',
  'openApp.field.clientIdPlaceholder': 'e.g. my-desktop-app',
  'openApp.field.applicationType': 'Application type',
  'openApp.field.clientType': 'Client type',
  'openApp.section.callback': 'Callback URIs',
  'openApp.field.redirectUris': 'Redirect URIs',
  'openApp.redirectUri.hint': 'Callback URI after sign-in completes',
  'openApp.redirectUri.placeholder':
    'https://example.com/callback or my-desktop-app://oauth/callback',
  'common.add': 'Add',
  'openApp.redirectUri.invalid': 'Please enter a valid absolute URI without a fragment.',
  'openApp.redirectUri.empty': 'No Redirect URI configured yet',
  'common.remove': 'Remove',
  'openApp.field.postLogoutRedirectUris': 'Post Logout Redirect URIs',
  'openApp.postLogoutUri.hint': 'Redirect URI after logout',
  'openApp.postLogoutUri.empty': 'No Post Logout Redirect URI configured yet',
  'openApp.section.authorization': 'Authorization capabilities',
  'openApp.service.autoConfigured': 'Configured automatically from the template',
  'openApp.service.description':
    'Server-side client credentials flow; no callback URIs or PKCE required.',
  'openApp.field.permissions': 'Permissions',
  'openApp.permissions.placeholder': 'Select authorization capabilities',
  'openApp.field.requirements': 'Requirements',
  'openApp.requirements.placeholder': 'Select security requirements',
  'openApp.requirement.forcePkce': 'Force PKCE',
  'openApp.requirements.offlineAccessHint':
    'refresh_token is allowed; consider also granting the offline_access scope.',
  'openApp.sessionBound.title': 'Bound to the sign-in session',
  'openApp.sessionBound.hint':
    'Authorization codes and refresh tokens stop working once the user signs out, the device is revoked, or the session goes idle. Turn on for browser (BFF) clients; turn off for offline clients that must keep refreshing.',
  'openApp.sessionBound.unset':
    'This application was registered before session binding existed. Choose explicitly before saving.',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};
//#endif
