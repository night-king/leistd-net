# Spartan 维护约定

加组件、升级 Spartan 或修改 `frontend/libs/ui` 中的 helm 组件时遵循本文。组件用法见 [前端界面规范](./frontend-ui.md)，组件 API 以项目 `spartan` Skill、本地 `frontend/libs/ui` 源码与锁定版本为准。

- **两层结构**：brain 层 `@spartan-ng/brain` 是无头基元，作为 npm 依赖引入、不改；helm 层是样式实现，通过 CLI **复制进本项目** `frontend/libs/ui/`，属自有代码，可自由修改。
- **摇树**：`frontend/libs/package.json` 声明 `"sideEffects": false`，未用到的 helm 模块不进产物。因此 libs 下的模块不能靠被导入本身产生必须保留的全局效果（裸导入样式或 polyfill、顶层注册、改全局对象）；顶层纯初始化、导出函数里的 DOM 操作与 Angular 声明不受影响。加组件或升级同步 Spartan 文件时逐个核对。
- **加组件**：`ng g @spartan-ng/cli:ui --name=<comp>`，把对应 helm 组件生成到 `frontend/libs/ui/`。
- **风格**：`components.json` 的 `style` 为 `vega`。新增或重新生成组件都按它输出，页面上的尺寸与圆角跟随组件，不另写一套。
- **先用样式表，再改组件**：能用语义 token 或 `frontend/src/styles.css` 里按 `data-slot`、状态属性写的规则实现的调整，不改 helm。由样式表承担的调整以 `styles.css` 为准，例如：系统开启"减少动效"时关闭浮层的进出场动画与侧栏、抽屉、导航菜单的位移过渡；对话框与抽屉页头为右上角关闭按钮让出宽度；选择框与组合框的值区放不下时单行省略，箭头留在控件内（上游把选择框值区写成 flex，省略号不生效；组合框值区没有收缩约束）；触屏下菜单项、选项、导航链接、侧栏菜单按钮与分段控件项补足 44px，折叠的图标侧栏随之加宽。只有样式表区分不了（按尺寸档位改高度）、要新增变体或改组件模板时才改 helm，并在下表登记。
- **已定制的 helm 组件**（升级时逐个对照上游手动合入）：

| 组件 | 改动 | 原因 |
| --- | --- | --- |
| `button` | `default` / `sm` / `lg` 加 `pointer-coarse:h-11`，`icon` / `icon-sm` / `icon-lg` 加 `pointer-coarse:size-11`；`destructive` 暗色底 `dark:bg-destructive/20` 改 `/10`、悬停 `dark:hover:bg-destructive/30` 改 `/20` | 触屏设备的点按目标不小于 44px，页面不再逐处补；`xs` / `icon-xs` 用于组合框标签移除等嵌在控件内的位置，不改。尺寸档位只体现在 class 上，样式表选不出来。淡色底与同源文字色越接近对比越低，暗色底收到与亮色相同的 alpha（上限见 `styles.css` 色板注释）；只调 `--destructive` 无法同时满足常态与悬停两档 |
| `input`、`input-group` | 加 `pointer-coarse:h-11`；`input-group-button` 的 `xs` 加 `pointer-coarse:h-11`，`icon-xs` / `icon-sm` 加 `pointer-coarse:size-11` | 与按钮同高，表单里并排时对齐；组内按钮（显示口令、清除）触屏点按目标不小于 44px，与组同高 |
| `select`（trigger） | `data-[size=default]`、`data-[size=sm]` 下加 `pointer-coarse:h-11` | 同上 |
| `sidebar`（`hlm-sidebar-menu-button`） | 折叠为图标时加 `pointer-coarse:group-data-[collapsible=icon]:size-11!`、`pointer-coarse:group-data-[collapsible=icon]:p-3.5!`；`lg` 档加 `pointer-coarse:group-data-[collapsible=icon]:p-1.5!` | 上游把折叠按钮写成带 `!` 的 32px 见方，样式表里不分层的 `!important` 盖不过层内的重要声明；为保持上游的级联关系与图标居中，在组件里加触屏档，内边距按 16px 图标与 `lg` 档 32px 头像重新居中。侧栏的加宽与其余项的最小高度在 `styles.css` |
| `badge` | `destructive` 暗色底 `dark:bg-destructive/20` 改 `/10`；新增 `success`、`warning` 变体（`bg-success/10`、`bg-warning/10` 底 + 同源文字色、无描边，与 `destructive` 同构） | 暗色对比理由同 `button`；正向状态（启用、已验证、成功）与待处理状态（需要关注的计数）各收成一个变体，页面不各写一串工具类，对比度只在一处调整 |
| `dropdown-menu`（`hlm-dropdown-menu-focus-on-hover`） | 菜单项随指针移焦点的监听由 `mouseenter` 改为 `pointermove`，触屏拖动不移焦点，已是焦点项不重复设置 | 菜单弹在静止的光标下时浏览器也派发 `mouseenter`，焦点被抢到光标下那一项，键盘打开本应落在首项（Radix 同样跟随 `pointermove`）；上游至今仍是 `mouseenter` |
| `dialog`（`hlm-dialog-content`）、`sheet`（`hlm-sheet-content`）、`sidebar`（`hlm-sidebar-trigger`）、`utils`（新增 `hlm-a11y-labels.token.ts` 并从 `utils` 入口导出） | 关闭按钮与侧栏开关的读屏名改读 `injectHlmA11yLabels()`；`hlm-dialog-content` 的 `closeLabel`、`hlm-sidebar-trigger` 的 `srOnlyText` 未传时取令牌 | 这些文案渲染在组件模板里，页面无处传入；令牌默认英文，多语言项目在应用配置里用 `provideHlmA11yLabels` 接上随语言切换的译文。上游升级曾把读取改回写死英文，合入时逐个保留 |

登记表按组件对照上游：凡与上游有差异的组件都必须有一行，没有差异的组件不登记；行内写全该组件的全部改动，升级或评审时逐项核对改动内容与源码一致。

用 `pointer-coarse` 而不是屏幕宽度判断：平板横屏很宽，但仍是手指操作。

升级步骤：`@spartan-ng/brain` 与 `@spartan-ng/cli` 在 `frontend/package.json` 锁定为同一精确版本，一起升级后跑 `ng g @spartan-ng/cli:healthcheck`；未定制的组件用 `ng g @spartan-ng/cli:migrate-helm-libraries --libraries=<name>` 同步到新版本（传 `--libraries` 即非交互，一次一个组件）；`frontend/libs/ui` 在 `.prettierignore` 里，CLI 写出的是制表符缩进，同步后用 `prettier --ignore-path <空文件> --write "libs/ui/**/*.ts"` 按项目格式重排，否则差异淹没在空白里；表中已定制的组件不执行 `migrate-helm-libraries`（会用上游版本覆盖自定义改动），对照上游变更逐个手动合入。helm 与 CLI 版本脱节时，新参数与无障碍改进不会自动到位——升级 CLI 不等于 helm 已更新。`chart` 依赖的 `@tanstack/angular-charts`、`@tanstack/charts` 按 CLI 的 `supported-ui-libraries.json` 给出的精确版本安装，不先于 Spartan 单独升级：helm 的图表适配层按那个版本生成。
