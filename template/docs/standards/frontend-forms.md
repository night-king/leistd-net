# 前端表单规范

表单的写法与校验提示，同时遵循 [前端界面规范](./frontend-ui.md)。字段有标签、帮助文本、校验提示和提交状态；服务端返回的字段错误回填到表单，见[错误处理](./coding-frontend.md#6-错误处理)。

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
- 新的错误类型在 `public/i18n/{en,zh-CN}.json` 的 `validation` 段各加一句；键是拼出来的，静态引用查不到，所以表单用到的每种类型（自定义 `kind`、所用内置验证器、没给 `error` 的 `pattern`）都要在两种语言里有句子。服务端返回的字段错误不走这里。
<!--#else-->
- 新的错误类型在 `frontend/src/app/shared/utils/english-text.ts` 的校验提示表里加一句，它并入每个组件的 `t`。服务端返回的字段错误不走这里。
<!--#endif-->
- Signal Forms 仍为 experimental，API 可能在小版本间变化：锁定 Angular 版本，升级后回归所有表单。
- **字段说明不要做成独立方框。** 口令规则、格式提示这类说明放在描述位（`hlm-field-description`）：标签在上的表单里贴在字段下方、与输入框同宽同起止，设置页标签在左时跟随标签列；带边框底色的独立块看着像另一个输入控件，宽度也与输入框对不齐。
