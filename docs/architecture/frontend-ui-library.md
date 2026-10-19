# 前端组件体系

## 技术组成

模板前端（`template/frontend`）采用 **Spartan UI（spartan.ng）** 作为 UI 组件库。

- 组件消费模式：headless 逻辑层 `@spartan-ng/brain`（npm 依赖）+ 样式层 helm（经 CLI 复制进 `template/frontend/libs/ui/`，**属自有代码**）。
- 样式基座：Tailwind CSS v4，组件主题走 CSS 变量（oklch token）。
- 图标：`@ng-icons` + Lucide。
- 字体：正文默认 **Geist**，经 `@fontsource/geist` 自托管（离线可用、无 CDN），业务项目可替换。
- 表单：Angular Signal Forms（`@angular/forms/signals`），不使用 Reactive Forms 与 `ngModel`。
- 数据表格：`@tanstack/angular-table` headless 引擎，展示层为自有组件。
- 支持范围：紧跟 Angular 最近两个大版本；具体允许的 Angular 版本以锁定依赖中 Spartan Brain 的 `peerDependencies` 为准。

表格裁剪与列表状态、表单写法、HTTP 错误反馈等执行规则见生成项目的[前端列表与表格规范](../../template/docs/standards/frontend-lists.md)、[前端表单规范](../../template/docs/standards/frontend-forms.md)与[前端编码规范](../../template/docs/standards/coding-frontend.md)，通用界面规则见[前端界面规范](../../template/docs/standards/frontend-ui.md)。

## 依据

1. helm 代码归仓库所有，可直接审查、定制并避免运行时主题封装。
2. Brain 与 Tailwind CSS v4、`.dark` 深色模式和 zoneless Angular 直接组合。
3. Spartan、Tailwind、TanStack Table、Lucide 和 Geist 均满足当前许可要求。

## 维护约束

- 主题保留亮、暗、系统三态和一套品牌 token；业务项目通过 CSS 变量调整品牌。
- Angular 与 Spartan 版本必须位于双方支持范围内。
- helm 组件升级与定制登记见生成项目的 [Spartan 维护约定](../../template/docs/standards/frontend-spartan.md)。
- 修改组件约定时同步 `template/docs/standards/` 与生成项目 Skill。
- 仓库根与模板各带一份 Spartan Skill；当前本地调整为先核对锁定版本和已复制 Helm 代码、按需运行 CLI info、避免交互式生成。同步上游 Skill 时核对这些差异并保持两份一致。
