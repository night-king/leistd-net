export default {
  extends: ['stylelint-config-standard', 'stylelint-config-clean-order'],
  plugins: ['stylelint-declaration-block-no-ignored-properties'],
  rules: {
    'plugin/declaration-block-no-ignored-properties': true,
    'selector-type-no-unknown': [
      true,
      {
        ignore: ['custom-elements'],
      },
    ],
    'selector-pseudo-element-no-unknown': [
      true,
      {
        ignorePseudoElements: ['ng-deep'],
      },
    ],
    'at-rule-no-unknown': [
      true,
      {
        ignoreAtRules: ['plugin', 'custom-variant', 'theme', 'apply', 'layer'],
      },
    ],
    'import-notation': 'string',
    // 该规则按 CSS 语法定义校验 at-rule 前奏，两类前奏它判不了：
    // 1. Tailwind v4 的 at-rule（与 at-rule-no-unknown 同一份清单）根本没有 CSS 语法定义；
    // 2. `@import '...'`——纯 <string> 前奏（正是上面 import-notation 要求的写法）会被判为非法，
    //    带 layer()/supports() 的同类写法反而通过，是规则自身的语法匹配缺陷。
    'at-rule-prelude-no-invalid': [
      true,
      {
        ignoreAtRules: ['plugin', 'custom-variant', 'theme', 'apply', 'import'],
      },
    ],
    // Spartan UI 主题变量用 oklch 小数记法（ui-theme 生成器的官方输出），
    // 不强制改写为百分比/度数，避免与官方产物及后续 healthcheck 升级冲突。
    'hue-degree-notation': null,
    'lightness-notation': null,
    'alpha-value-notation': null,
    'color-function-notation': null,
  },
};
