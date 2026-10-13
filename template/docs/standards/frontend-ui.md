# 前端界面规范

界面设计、组件与样式、导航的规范。编码与分层见 [前端开发规范](./coding-frontend.md)。

## 1. 设计原则

- 以用户任务为中心，而不是以数据表单为中心；主要动作、关键状态、辅助信息分层呈现。
- 错误态、空状态、加载态必须可解释并可恢复。异步内容区分加载中、加载失败、确实为空：失败要说出来并给出重试；已有内容时刷新失败，保留原内容并提示。请求被后续请求取代时（快速翻页、改筛选），"加载中"跟随当前请求。
- 表单字段有标签、帮助文本、校验提示和提交状态；列表另考虑筛选与分页。
- 可访问性：交互元素可键盘访问；触屏点按目标 ≥ 44px（Spartan 按钮 `xs` 以外的尺寸已内置，页面不补）；颜色不是唯一信息表达；表单错误与字段关联；遵守系统"减少动效"：不播放位移、缩放动画，保留颜色与焦点反馈（Spartan 组件做法见 [Spartan 维护约定](./frontend-spartan.md)）。

### 1.1 数据表格与列表

- 表格用 TanStack Table（`@tanstack/angular-table`，服务端 `manualPagination`/`manualSorting`/`rowCount`）；分页与筛选复用 `frontend/src/app/shared/components/table-paginator`、`faceted-filter`。
- 列按优先级（`primary`/`secondary`/`tertiary`）裁剪：视口档位（`tableViewportSignal()`）给上限，带吸附操作列的表格外框挂 `[appTableFit]="tableViewport()" [appTableFitContent]="rows()"`，放不下再降一档（更新数据须换新数组）。被裁的列由 TanStack 行展开补偿（按 `getRowId` 的实体 id 记），不另建展开状态。主列用 `TITLE_COLUMN_META` 与 `TITLE_CONTENT_CLASS`：最窄档截断长名称。
- 操作列按"常用优先、破坏性置后"排序：≤3 项桌面端平铺（icon 按钮 + tooltip），>3 项显示 2 个高频操作 + `…` 溢出菜单；`<sm` 一律收进 `…`，破坏性操作用 `variant="destructive"` 并以分隔线隔开。

## 2. 组件与样式

### 2.1 组件库优先

- 先查本地 `frontend/libs/ui` 的现成组件与锁定版本；新增组件或核对 API 时再查项目内 `spartan` Skill 与匹配版本的 [Spartan UI 官方文档](https://spartan.ng/)
- 尽量沿用 Spartan 的默认组件与风格（`hlm*` 指令 / `hlm-*` 组件）
- 仅在无法满足需求时才可创建自定义组件，不在 `shared` 重建组件库

### 2.2 样式方案

- **必须** 优先使用 Tailwind CSS v4 的原子类进行布局和微调
- 任何自定义样式都**必须**与 Spartan UI 的主题风格保持一致（基于 Spartan 主题的 CSS 变量与 `.dark` class）
- 所有页面一律使用语义化主题变量，不写具体色值（`blue-*`、hex 等）。换品牌改 `frontend/src/styles.css` 里的令牌：品牌色亮暗成对改（同一色相，亮色 L≈0.52、暗色 L≈0.72），中性色带一点跟随品牌色相的彩度。`--input`（输入框、复选框、描边按钮的边界）与 `--border`（装饰分隔线）分开调，前者深一档、带品牌色相，看得出轮廓又不压过内容，聚焦由 `--ring` 强指示；选中态只靠底色深浅区分不够时由 `styles.css` 按 `data-slot` 补不低于 3:1 的描边
  - 例外只有三类：图像遮罩及其上文字（`bg-black/25 text-white`）、二维码底色（`bg-white`，扫码需浅底）、第三方品牌标识 SVG 的官方色值
- 居中或带 `text-balance` 的标题与导语加 `break-keep wrap-anywhere`：中文优先在标点和空格处换行，超长连续文本才在内部断开以免溢出；正文段落不加
<!--#if (LocalIdentity)-->
- 登录、注册、强制启用两步验证共用 `frontend/src/app/features/account/components/auth-shell`：五五分栏，左栏是独立贴边的品牌面板（标识，居中的标题、导语与三条不随参数变化的亮点，底部一行信任语），右栏是主题/语言切换与居中的表单（不再套卡片）；lg 以下品牌面板隐藏，标识移到表单栏顶部。这几页在一次登录里连续经过，外观必须一致
<!--#endif-->

### 2.3 有限语义色板

- 状态/分类标签**只用项目约定的有限几个语义色**，不随意扩色。
- **分类标签用中性色，不用告警色表达"非告警"的分类**（不要用 warn 橙表达一个普通分类）。

> `例`：用 Spartan badge 的有限变体 `default` / `secondary` / `destructive` / `success` / `outline`（`hlmBadge`）；分类标签一律用中性变体（`secondary`）；`success` 只用于启用、已验证、成功这类正向状态。

### 2.4 组件设计

- **单一职责**：每个组件只关注一个功能点，不把业务流程、数据请求和展示状态混在一个文件
- **数据流**: 遵循"单向数据流"原则（`[input]` 向下, `(output)` 向上）
- **变更检测**：共享与展示型组件使用 `ChangeDetectionStrategy.OnPush`

### 2.5 图标 / 文案跨页一致

- 跨页的图标尺寸、文案措辞要统一；**对齐用实测（浏览器/像素）确认，不靠目测**。
- 图标使用 `@ng-icons` + lucide：模板写 `<ng-icon name="lucideXxx">`，并在组件级用 `provideIcons({ lucideXxx })` 按需注册（不全局注册全部图标）。

### 2.6 表单规范

- **必须** 使用 Angular Signal Forms（`@angular/forms/signals`）：以 `form()` 构建表单模型，模板用 `[formField]` 绑定字段，不用 `[(ngModel)]` / Reactive Forms（eslint 拦截 `FormsModule`、`ReactiveFormsModule`）。
- 表单 UI **必须** 走 Spartan Field 组件族：`hlm-field` 容器 + `hlmFieldLabel` 标签 + `hlm-field-error` 错误展示，配合 `hlmInput` / `hlm-select` 等输入组件。校验态由 brain 层（`BrnField`）读取，与具体表单引擎解耦。
- **校验提示按错误类型取词条**：验证器不带 `message`，模板按 `error.kind` 取 `validation.<kind>`，错误对象整个作参数（项目 `spartan` Skill 的表单示例显示 `error.message`，以本节为准）：

  ```html
  @for (error of form.email().errors(); track error.kind) {
    <hlm-field-error [validator]="error.kind">{{ t('validation.' + error.kind, error) }}</hlm-field-error>
  }
  ```

  内置错误的参数就是错误对象上的字段，占位符照它命名（`minLength` 错误的 `{{minLength}}`、`maxLength` 错误的 `{{maxLength}}`）。字段特有的说法给自定义类型：内置验证器用 `error` 选项（`pattern(path.username, /^[a-zA-Z0-9_]{3,64}$/, { error: { kind: 'usernamePattern' } })`），跨字段规则由 `validate()` 返回 `{ kind: 'passwordMismatch' }`。同一字段只让一个验证器报同一类型——长度与字符集共用一句时合成一条正则，分开写同一句会出现几遍。
<!--#if (IncludeLocalization)-->
- 新的错误类型在 `public/i18n/{en,zh-CN}.json` 的 `validation` 段各加一句；键是拼出来的，静态引用查不到，所以表单用到的每种类型（自定义 `kind`、所用内置验证器、没给 `error` 的 `pattern`）都要在两种语言里有句子。服务端返回的字段错误不走这里（见[错误处理](./coding-frontend.md#6-错误处理)）。
<!--#else-->
- 新的错误类型在 `frontend/src/app/shared/utils/english-text.ts` 的校验提示表里加一句，它并入每个组件的 `t`。服务端返回的字段错误不走这里（见[错误处理](./coding-frontend.md#6-错误处理)）。
<!--#endif-->
- Signal Forms 仍为 experimental，API 可能在小版本间变化：锁定 Angular 版本，升级后回归所有表单。

### 2.7 Spartan 维护

加组件、升级 Spartan 或修改 `frontend/libs/ui` 中的 helm 组件时读 [Spartan 维护约定](./frontend-spartan.md)：其中登记了已定制组件，升级时须逐个手动合入。

### 2.8 视觉规范

字号按**层级**用，每一档只有一个职责，不出现档外值（`text-[10px]` 之类）。正文基准是 14px——按钮、输入框、表格、卡片描述都是这个字号，这是组件已经定下的。

| 档位 | 用途                   | 写法                                    |
| ---- | ---------------------- | --------------------------------------- |
| 24px | 页面标题（有则一个）   | `text-2xl font-semibold tracking-tight` |
| 16px | 面板标题、卡片标题     | `text-base font-medium`                 |
| 14px | 分区标题               | `text-sm font-semibold`                 |
| 14px | 正文、表格、按钮、输入 | `text-sm`                               |
| 12px | 辅助说明、时间戳、徽章 | `text-xs`                               |
| 30px | 仅仪表盘大数字         | `text-3xl` + `tabular-nums`             |

- 营销页（着陆页、认证页品牌面板）可以用更大的展示字号（`text-4xl` 到 `text-6xl` 的 Tailwind 档位，仍不写任意值）、网格背景、光晕与渐变字；这些装饰只限营销页，应用内页面不用。
- 页面不必都有可见标题：列表页可直接从工具栏开始（面包屑与标签页已说明位置）；有页面标题时用 24px 一档，一页一个。
- 字重只用 400 / 500 / 600，不用 `font-bold` / `font-extrabold`。
- 16px 一档即 card、dialog、sheet、alert-dialog 标题组件的默认样式，不再手加 `font-semibold`。
- 加在图标、`hlm-spinner` 上的 `text-*` 是图标尺寸，不受上表约束。
- 输入框的 `text-base md:text-sm` 不要改：iOS Safari 遇到小于 16px 的输入框会在聚焦时放大页面。

圆角三档，由元素的角色决定：

| 档位           | 用于                                                                         |
| -------------- | ---------------------------------------------------------------------------- |
| `rounded-xl`   | 页面级容器：卡片、对话框、表格外框、设置面板的分区卡片                       |
| `rounded-md`   | 控件（按钮、输入框，组件已是）；嵌在卡片或对话框里的块、代码框、行项、图标块 |
| `rounded-full` | 徽章、头像                                                                   |

嵌套的块用 `md` 不用 `xl`：内外同圆角就分不出层次。组件内部的小元素（菜单项、清除按钮）跟随所在组件的写法。

- **托盘卡**（`frontend/src/app/shared/components/stat-panel`）：灰底外框放标题与右侧操作，内层白卡放内容，用于仪表盘这类同屏多块数据；内卡圆角按嵌套规则取 `rounded-lg`（外框 `rounded-xl` 减去 4px 间距）。普通页面区块仍用 Spartan Card，不套托盘。
- **图表**用 Spartan `chart`（TanStack Charts），序列颜色只取 `--chart-1…5` 令牌、主题用 `HLM_CHART_THEME`，不写色值；`chart` 只在用到它的懒加载页面里引入。

- **当前项用中性色，不用品牌色**：侧栏菜单沿用 Spartan 的 `data-active` 样式（`sidebar-accent` 底），细描边由 `styles.css` 统一补；顶栏导航沿用 navigation-menu 的默认当前态；抽屉与设置面板里的 ghost 按钮挂 `aria-[current=page]:bg-muted`。品牌色留给主操作、焦点环与链接。
- **间距**：兄弟元素之间用 `flex` / `grid` + `gap-*`，不用 `space-y-*` 与逐个元素的外边距。页面外框统一 `p-4 sm:p-6`。
- **暗色**：表面分五层逐级提亮（`sidebar` < `background` < `card` < `popover` < `muted`），不用纯黑；层级都在 `styles.css` 里定好，页面不写 `dark:` 颜色覆盖。
- **图标 + 文案的空态一律用 flex 列**（`flex flex-col items-center justify-center gap-2`），不靠给 `ng-icon` 加 `block`：它的 host 样式按行内盒渲染，`block` 压不住，图标与文字会挤在一行、基线错开。同款写法见通知面板的空态。
- **字段说明不要做成独立方框。** 口令规则、格式提示这类说明贴在字段下方的描述位（`hlm-field-description`），与输入框同宽同起止；带边框底色的独立块看着像另一个输入控件，宽度也与输入框对不齐。

## 3. 导航与菜单分组

导航菜单是**信息架构**，不是控件清单：分组摆错了既不报错也不影响功能，只是让人找不到入口。两个区各有一套菜单，都定义在 `frontend/src/app/layout/services/navigation-service.ts` 一处，管理平台的侧栏与工作空间的顶栏都从这里读，判据相同。

| 分组               | 放什么                                     | 现有入口                                                                             |
| ------------------ | ------------------------------------------ | ------------------------------------------------------------------------------------ |
| 工作               | 进来先看的东西：本区落地页、待办、我的任务 | 平台「仪表盘」/ 工作空间「工作台」                                                   |
| 业务               | 这个系统"做业务"的地方                     | 模板只放一个「示例模块」占位，下游项目在这里加自己的                                 |
| 身份与访问         | 谁能用、能用什么                           | 用户管理、角色管理、租户管理                                                         |
| 开发者             | 面向集成方的东西                           | 开放应用（OAuth 客户端）                                                             |
| 审计               | 谁在什么时候做了什么                       | 操作记录（登录记录、导出记录同属这一类）                                             |
| 系统               | 本租户（或宿主）的默认值与策略             | 系统设置（注册验证、登录安全、邮件发送、运维、审计等面板，一个后端设置分组一个面板） |

- **不设「其它」这类兜底组。** 新入口必须落进上表某一类；落不进就新开一类并把判据补进表里。
- **按用户的目的分组，不按后端模块或表结构分组。** 用户找的是"我要做什么"，不是"这属于哪个服务"。
- **管理平台的菜单分组与后端的权限分组同名同序。** 每个菜单项只挂它对应的根权限，菜单项标题与根权限显示名一致，这是与后端共同遵守的命名约定，见 [后端开发规范](./coding-backend.md#39-权限)。菜单项的权限必须列进 `PLATFORM_ENTRY_PERMISSIONS`，由 `default-sidebar.spec.ts` 核对。
- **个人设置只有一处：头像菜单里的「个人设置」**（工作空间的 `/workspace/settings`），管理人员从管理平台进的也是同一处；菜单树与管理平台都不另放入口。系统设置放管理平台「系统」组，作用域不同，不合并。
- 监控、健康检查、后台任务这类"系统怎么跑"的东西以后若要进导航，新开「运维」组；日志级别这类运维**配置**放在系统设置的「运维」面板里，不进导航。
- **头像菜单里不放「切换租户」。** 会话租户在登录时定案，换租户只能退出后在登录页重选。
- 菜单项只按权限裁剪（`permissions` 任一命中即可见），整组为空时**整组消失**，不留空标题——空标题看起来像加载失败。
- 命令面板（⌘K / Ctrl+K，侧栏与顶栏的搜索入口）列的是当前区按权限裁剪后的同一份菜单加另一区入口，不另建命令清单；新增菜单项自动出现在面板里。
- **按"能不能做"分支，不按"我是谁"分支。** 区分宿主侧与租户侧看权限：服务端下发的权限已按侧别过滤。不要从 `TenantContextService` 推导侧别——本地身份形态下它只是登录页的路由提示，可能与会话租户不一致。权限表达不了的显示决定由页面所属的业务端点连同数据下发结论。
- 侧栏分组骨架由 `default-sidebar.spec.ts`、头像菜单构成由 `user-menu.spec.ts`（按完整序列断言）钉住，改导航同时改用例。
- **页面标题只设 `LayoutService.title`**（面包屑末级）：每个布局页在构造函数或 `effect` 里设一次，本地化形态用 `translateSignal`，语言切换随之更新。页头按它输出视觉隐藏的 `h1`（未设不输出），布局内页面的标题因此从 `h2` 起、逐级不跳；登录、注册、错误页等不在这两种布局里的页面自带 `h1`。浏览器标签页标题由根组件统一合成为「页面标题 · 应用名」（`Title` 服务），页面不要自己调 `Title`；页头随布局销毁时清空页面标题，登录页、落地页等不设标题的页因此只显示应用名。应用名是 `frontend/src/app/app.ts` 的 `APP_NAME`，本地化形态取词条 `app.name`、缺词条时回落到它；改项目名时两处连同 `frontend/src/index.html` 的 `<title>` 一起改。
- 设置页的面板（个人设置、系统设置各一套）走子路由，面板名进 URL：刷新、分享、头像菜单直达都落在同一面板。
- **布局按服务对象选**：管理平台面向内部员工，条目多、会增长、需要按权限整组裁剪，用侧栏（`DefaultLayout`）；工作空间面向业务用户，内容优先，用顶栏（`WorkspaceLayout`），菜单组默认靠左、用文字链接；标 `placement: 'end'` 的组靠右、以图标按钮呈现（与主题、通知、语言同排，名称放在提示与可访问名里），模板自带的分组都不标。工作空间的主导航超过 7 项，或需要分组标题与按权限整组裁剪时，把路由换回 `DefaultLayout`——菜单定义不用动。
<!--#if (!IncludeLocalization)-->

## 4. 界面文案

- 组件模板里的界面文案写成 `t('模块.语义')`，解析到组件的 `protected readonly t = englishText(ENGLISH)`：按组件文件末尾的英文表 `ENGLISH` 取值，`{{name}}` 占位按参数替换（`frontend/src/app/shared/utils/english-text.ts`）。新增文案在同一组件的表里加一条键；表里没有的键会原样显示成键名。
- 组件 TS 里的提示文案（toast、确认框）直接写英文，或经同一个 `t` 取表里的键。
<!--#endif-->
