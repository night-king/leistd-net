# 前端多语言规范

默认语言英语，支持 en + zh-CN 运行时切换。编码规范见 [前端开发规范](./coding-frontend.md)，表单校验提示见 [前端表单规范](./frontend-forms.md)。文中路径相对项目根。

## 1. 方案与默认语言

- **运行时库 Transloco**（`@jsverse/transloco`）：JSON 词条运行时加载，用户即时切换语言、单包部署——**不用** Angular 编译期 `$localize`（那是按 locale 出多包、无法运行时切换）。
- **默认语言英语（`en`）**，支持 `en` + `zh-CN`；回落语言 `en`。
- 全局词条 `frontend/public/i18n/{en,zh-CN}.json` 只放跨功能文案（common、validation、layout、menu 等）；功能词条放 `frontend/public/i18n/<scope>/{en,zh-CN}.json`，现有 scope 即该目录下的子目录。
- scope 文件直接放该命名空间的内容，不重复包一层 scope 名：`frontend/public/i18n/roles/en.json` 的 `create` 对应运行时键 `roles.create`。

## 2. 文案归属

| 类别 | 归属 | 用法 |
| --- | --- | --- |
| UI 静态文案（菜单、按钮、标签） | 前端词条（权威） | 模板结构指令 `*transloco="let t"` 内 `{{ t('menu.users') }}`；TS 见第 4 节 |
| 业务错误消息 | **后端资源**（权威，见 [API 规范](./api.md)） | 前端直接显示后端已本地化的 `detail`，不在前端重复维护业务错误词条 |

- 后端按 `Accept-Language` 返回本地化消息，前端如何展示与分支见[错误处理](./coding-frontend.md#6-错误处理)。前端词条只提供网络断开、后端不可达等客户端兜底。

## 3. 关键接线

- `frontend/src/app/core/services/language-service.ts` 是切换语言的唯一入口（`setDeviceLang` / `applyAccountLang` / `resetToDeviceLang`），不直接调 `transloco.setActiveLang()`：绕过它就没有设备偏好、`<html lang>` 与加载失败的退回。行为契约：
  - 先加载全局及所有已访问 scope 的目标语言词条，成功后才切换；加载期间与失败时保持原语言并上报，再次选择会重试。
  - 连续切换只有最后一次生效；返回的 Promise 在加载落定后 resolve、不 reject。
  - 设备偏好存 localStorage，账户偏好只在内存生效；首帧前等待初始全局词条，失败退回英语。
- `frontend/src/app/core/i18n/translation-scopes.ts`：路由解析器读取 `provideTranslocoScope` 的登记，组件创建前等到活动语言的词条。加载失败时取消导航、保留原页面；首次深链没有原页面时显示启动失败卡片并重试，恢复后进入原目标 URL（含查询与锚点）。
- `frontend/src/app/core/i18n/transloco-loader.ts` 按 `{baseHref}i18n/[{scope}/]{lang}.json` 加载，兼容子路径部署；`frontend/src/app/core/interceptors/accept-language-interceptor.ts` 置拦截器链首位（链在 `frontend/src/app/app.interceptors.ts`）。
- 回落策略 `provideLanguageFallbackStrategy()` 由 `frontend/src/app/app.config.ts` 与单测装配 `frontend/src/app/core/i18n/transloco.testing.ts` 共同登记，新增测试装配沿用后者。
- 语言选择器 `frontend/src/app/core/components/language-switcher` 用 Spartan dropdown-menu，挂在 `frontend/src/app/layout/components/default-header` 最右图标区。

## 4. 新增文案

- **词条随功能归属，这是业务项目持续扩展的长期约定。** 不按词条数量选择性拆分，每个独立功能都拥有自己的 scope；通用布局和真正跨功能的文案才放全局。权限授权弹窗属于 permissions 功能，即使被其它管理页嵌入，也由 permissions scope 持有词条，由宿主路由一起预加载。
- 先分析引用归属：只有单一功能使用的文案放该功能 scope，真正跨功能的文案留在全局。新的独立懒加载功能新建 scope，两语言文件同时维护；不要把同一文案复制到多个 scope。路由登记 `providers: [provideTranslocoScope('orders')]` 和 `resolve: { translations: resolveTranslationScopes }`，注册与预加载读取同一来源；嵌套功能在自己的路由登记 scope。
- UI 文案在对应的 en、zh-CN 文件同时加键。键集合、插值、引用与 scope 登记由 `scripts/check-i18n.py` 检查，判据见脚本文件头；动态键、非字面量路由或登记表达式不在静态判据内，新用法须补检查，不得绕过预加载约定。
- **模板文案用结构指令**（Transloco 官方写法，一个模板一个订阅，切换语言时整个模板重绘）：组件模板最外层包 `<ng-container *transloco="let t">`（组件 `imports` 引 `TranslocoDirective`），块内一律 `t('key', params)`；传给子组件的文案（如分页器的 `labels`）也在模板里用 `t` 组装。不给模板写"返回译文的组件方法"（漏读语言信号就停在旧语言），按状态选文案写 `cond ? t('a') : t('b')`。
- 只使用一个功能命名空间的模板可写 `*transloco="let t; prefix: 'orders'"`，正文用 `t('title')`；`prefix` 是 8.4 的正式输入，`read` 已弃用。同时使用 common/validation 的模板保留完整键 `t('orders.title')` 与 `t('common.save')`。配置 `scopes.autoPrefixKeys: false`，避免 scope 注入后完整键被重复加前缀。关闭多语言时，外层裁成普通 ng-container，英文表使用与正文相同的键；使用 prefix 的模板英文表也必须用相对键。
- **TS 里随语言变化的文案**（导航菜单、筛选项、页面标题等由 `computed` / `effect` 产出的）一律用信号 API，在字段或构造函数里建、`computed` 里读：固定的键用 `translateSignal('orders.title', {}, { scope: 'orders' })`（全局键省略 scope）；一组键或键随数据变化（类别、动作码）用 `translateObjectSignal('orders.status', {}, { scope: 'orders' })` 取成对象再索引（键仍用完整前缀），带点号的键用 `frontend/src/app/core/i18n/translation-text.ts` 的 `textAt` 逐级取。词条未到时分别是空串、空对象，到达后自行更新。**不要**在 `computed` / `effect` 里「读 `activeLang()` + 同步 `translate()`」：漏读一处就停在旧语言，词条未到时的裸键还会被缓存。参数随每行数据变化的句子在 TS 里只定键与参数，由模板 `t(key, params)` 取。
- **事件发生时的一次性文案**（toast、确认框）直接 `transloco.translate()`：语言激活前与路由进入前词条已加载，取到的总是真实文案，不需要随语言重算。要留在界面上的文案（写进 signal、随后渲染的错误说明）存键，由模板 `t` 取，否则切换语言时它停在旧语言。
- 业务错误文案：改后端资源（见 [API 规范](./api.md#41-异常本地化)），**不在前端加**。
- 货币/数字用 Angular `CurrencyPipe`/`DecimalPipe`。注意 **`LOCALE_ID` 是启动期注入、不随运行时语言切换自动改变**；如需格式也跟随切换，需自行传 locale 参数或重建相关视图，别假设它会自动联动。

## 5. 伪语言与加语言

- **伪语言**：`frontend/src/app/core/i18n/pseudo-locale.testing.ts` 由英文词条生成，只用于单测：字母换成带附加符号的同形字，连同方括号补到标签容量 max(⌈英文近似列宽 × 1.3⌉, 14 列)（CJK 记 2 列）。容量是默认测试预算，不是像素容量；来自词条的静态标签没有方括号即为写死文案。
- **加语言**：同一改动里加前后端词条，改语言清单（`frontend/src/app/core/services/language-service.ts` 的 `SUPPORTED_LANGS`、`LANG_OPTIONS`，`frontend/_mock/api/setting.ts`，后端 `SettingConstant.SupportedLanguages`），把 `scripts/check-i18n.py` 写死 en/zh-CN 的键与占位符比较扩到全部语言，用缺键、错占位符夹具证伪。比英文宽的语言可加长度门禁，上限取标签容量，句子类词条按键排除。
