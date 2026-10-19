// @ts-check
import { readdirSync, realpathSync } from 'node:fs';
import path from 'node:path';

import eslint from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';
import angular from 'angular-eslint';

import eslintConfigPrettier from 'eslint-config-prettier/flat';
import { createNodeResolver, importX } from 'eslint-plugin-import-x';
import unusedImports from 'eslint-plugin-unused-imports';

// 解析器返回真实路径，项目经符号链接打开时也要按真实路径比对
const projectRoot = realpathSync(import.meta.dirname);

// 显式解析器：方向检查与跨功能检查都按它解析出的真实文件判断，不看路径字符串
const resolver = createNodeResolver({
  extensions: ['.ts', '.mjs', '.js', '.json'],
  conditionNames: ['types', 'import', 'default'],
  tsconfig: { configFile: path.join(projectRoot, 'tsconfig.json') },
});

// 目录依赖方向，见 docs/standards/coding-frontend.md 第 2 节。功能目录按磁盘现状生成，新增功能自动纳入。
const app = 'src/app';
const features = readdirSync(path.join(projectRoot, app, 'features'), { withFileTypes: true })
  .filter((entry) => entry.isDirectory())
  .map((entry) => `${app}/features/${entry.name}`);
const assembly = ['app.ts', 'app.config.ts', 'app.interceptors.ts', 'app.routes.ts'].map(
  (file) => `${app}/${file}`,
);

/**
 * 生成 `import-x/no-restricted-paths` 的规则区。功能之间的引用由下方 `local/feature-boundaries` 检查。
 *
 * @param {{ mock: boolean }} restrict `mock`：是否禁止引用 `_mock`（测试文件放开）。
 */
function directionZones({ mock }) {
  const zone = (target, from, message) => ({ target, from, message });
  const mockFrom = mock ? ['_mock'] : [];
  return [
    zone(
      `${app}/core`,
      [`${app}/layout`, `${app}/features`, ...assembly, ...mockFrom],
      'core may depend only on core and shared.',
    ),
    zone(
      `${app}/layout`,
      [`${app}/features`, ...assembly, ...mockFrom],
      'layout may depend only on core, layout and shared.',
    ),
    zone(
      `${app}/shared`,
      [`${app}/core`, `${app}/layout`, `${app}/features`, ...assembly, ...mockFrom],
      'shared may depend only on shared.',
    ),
    ...features.map((feature) =>
      zone(
        feature,
        [`${app}/layout`, ...assembly, ...mockFrom],
        'A feature may depend only on core, shared and itself.',
      ),
    ),
    zone(
      '_mock',
      [`${app}/layout`, ...assembly],
      '_mock must not depend on layout or the application assembly.',
    ),
  ];
}

const featureRoots = features.map((feature) => path.join(projectRoot, feature) + path.sep);
const featureOf = (/** @type {string} */ file) =>
  featureRoots.find((root) => file.startsWith(root));
const routeLoaders = new Set(['loadComponent', 'loadChildren']);

/**
 * 动态导入是否直接位于路由文件里 `loadComponent`/`loadChildren` 的加载函数中，如
 * `loadComponent: () => import('...').then((m) => m.Page)`，或块体里 `return import(...)...`。
 * 加载函数内嵌套回调里的 `import()` 与其他位置的都不算懒加载路由。
 *
 * @param {any} node ImportExpression
 */
function isRouteLoader(node) {
  let current = node.parent;
  while (
    ['MemberExpression', 'CallExpression', 'AwaitExpression', 'ChainExpression'].includes(
      current?.type,
    )
  ) {
    current = current.parent;
  }
  if (current?.type === 'ReturnStatement' && current.parent?.type === 'BlockStatement') {
    current = current.parent.parent;
  }
  return (
    (current?.type === 'ArrowFunctionExpression' || current?.type === 'FunctionExpression') &&
    current.parent?.type === 'Property' &&
    routeLoaders.has(current.parent.key?.name)
  );
}

/** 功能之间不互相依赖；唯一例外是路由文件经 loadComponent/loadChildren 懒加载其他功能的页面。 */
const featureBoundaries = {
  meta: {
    type: 'problem',
    messages: {
      crossFeature:
        'A feature may not depend on another feature; move what features share into core or shared. Route files may lazy-load other features only through loadComponent/loadChildren.',
    },
    schema: [],
  },
  create(/** @type {any} */ context) {
    let filename = context.physicalFilename;
    try {
      filename = realpathSync(filename);
    } catch {
      // 标准输入等没有磁盘文件的情形按原路径比对
    }
    const own = featureOf(filename);
    if (!own) return {};
    const isRoutes = filename.endsWith('.routes.ts');
    const check = (/** @type {any} */ node, /** @type {any} */ source) => {
      // 非字符串字面量的 import() 由 no-restricted-syntax 拦截；不带 from 的 export 没有 source
      if (source?.type !== 'Literal' || typeof source.value !== 'string') return;
      const resolved = resolver.resolve(source.value, filename);
      const target = resolved.found && resolved.path ? featureOf(resolved.path) : undefined;
      if (!target || target === own) return;
      if (isRoutes && node.type === 'ImportExpression' && isRouteLoader(node)) return;
      context.report({ node: source, messageId: 'crossFeature' });
    };
    return {
      ImportDeclaration: (/** @type {any} */ node) => check(node, node.source),
      ExportNamedDeclaration: (/** @type {any} */ node) => check(node, node.source),
      ExportAllDeclaration: (/** @type {any} */ node) => check(node, node.source),
      ImportExpression: (/** @type {any} */ node) => check(node, node.source),
    };
  },
};

const httpStatusOperators = new Set(['===', '!==', '==', '!=']);

/**
 * 页面按稳定 `code` 分支，不按单个 HTTP 状态码（coding-frontend.md 第 6 节）。拦截 `.status` 与
 * 100–599 的数字或 `HttpStatusCode.*` 的相等比较、`switch (x.status)` 的同类 `case` 与
 * `[409, …].includes(x.status)`；`status === 0`（网络不可达）与区间判断不算单个状态码。
 */
const noStatusCodeBranch = {
  meta: {
    type: 'problem',
    messages: {
      statusBranch:
        'Branch on the stable error code (ApplicationHttpError.code with API_ERROR_CODES), not on a single HTTP status code.',
    },
    schema: [],
  },
  create(/** @type {any} */ context) {
    const unwrap = (/** @type {any} */ node) =>
      node?.type === 'ChainExpression' ? node.expression : node;
    const isStatus = (/** @type {any} */ node) => {
      const target = unwrap(node);
      return (
        target?.type === 'MemberExpression' && !target.computed && target.property.name === 'status'
      );
    };
    const isStatusCode = (/** @type {any} */ node) => {
      const target = unwrap(node);
      if (target?.type === 'Literal') {
        return typeof target.value === 'number' && target.value >= 100 && target.value <= 599;
      }
      return (
        target?.type === 'MemberExpression' &&
        target.object.type === 'Identifier' &&
        target.object.name === 'HttpStatusCode'
      );
    };
    const report = (/** @type {any} */ node) => context.report({ node, messageId: 'statusBranch' });
    return {
      BinaryExpression(/** @type {any} */ node) {
        if (!httpStatusOperators.has(node.operator)) return;
        if (
          (isStatus(node.left) && isStatusCode(node.right)) ||
          (isStatus(node.right) && isStatusCode(node.left))
        ) {
          report(node);
        }
      },
      SwitchCase(/** @type {any} */ node) {
        if (node.test && isStatusCode(node.test) && isStatus(node.parent.discriminant)) {
          report(node);
        }
      },
      CallExpression(/** @type {any} */ node) {
        const callee = unwrap(node.callee);
        if (
          callee?.type === 'MemberExpression' &&
          callee.property.name === 'includes' &&
          callee.object.type === 'ArrayExpression' &&
          callee.object.elements.some(isStatusCode) &&
          isStatus(node.arguments[0])
        ) {
          report(node);
        }
      },
    };
  },
};

// 导入路径一律写成普通字符串字面量：模板字符串与表达式不经 import-x 解析，方向与存在性都检查不到
const importPathSyntax = {
  selector: "ImportExpression[source.type!='Literal']",
  message: 'Write import() paths as plain string literals so they can be resolved and checked.',
};

// API 契约类型只放 `{name}.dto.ts`（coding-frontend.md 第 3、4 节）；`*.dto.ts` 自身由后面的配置块放开
const dtoOutsideDtoFileSyntax = {
  selector:
    ':matches(TSInterfaceDeclaration, TSTypeAliasDeclaration, ClassDeclaration, TSEnumDeclaration)[id.name=/Dto$/]',
  message: 'Declare API contract types (*Dto) in a {name}.dto.ts file under dtos/.',
};

const formsRestriction = {
  name: '@angular/forms',
  importNames: ['FormsModule', 'ReactiveFormsModule', 'NgModel', 'NgForm', 'NgControl'],
  message:
    'Use Angular Signal Forms (@angular/forms/signals); FormsModule/ReactiveFormsModule/ngModel are not allowed.',
};

// 浏览器标签页标题由根组件统一合成（frontend-navigation.md 第 2 节），页面只设 LayoutService.title
const titleRestriction = {
  name: '@angular/platform-browser',
  importNames: ['Title'],
  message:
    'Set LayoutService.title instead; only the root component (app.ts) composes the browser title through Title.',
};

/*
 * 模板类名档位（frontend-ui.md 第 2.2、2.6 节）。只检查静态 `class="…"` 与 `[class.xxx]` 的类名；
 * 已知边界：`[class]`/`[ngClass]` 绑定的表达式、带插值的 `class="a {{b}}"` 与 TS 字符串（host、cva）
 * 不在覆盖内，留给评审。具体色板只拦带色阶的色名与十六进制任意值，`black`/`white` 不带色阶，
 * 图像遮罩与二维码底色两类例外因此天然放过；`ng-icon`、`hlm-spinner` 上的 `text-*` 是图标尺寸。
 */
const classToken = String.raw`(^|\s)([^\s]*:)?`;
const paletteUtilities =
  'bg|text|border(-[xytrblse])?|ring|ring-offset|outline|fill|stroke|from|via|to|decoration|divide|shadow|accent|caret|placeholder';
const paletteHues =
  'slate|gray|zinc|neutral|stone|mauve|olive|mist|taupe|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose';
/** 静态 class 属性或 `[class.xxx]` 绑定的类名命中 `pattern`（esquery 正则，不得含 `/`）。 */
const classMatching = (/** @type {string} */ pattern) =>
  `:matches(TextAttribute[name='class'][value=/${pattern}/], BoundAttribute[name=/${pattern}/])`;
const templateClassSyntax = [
  {
    selector: classMatching(String.raw`${classToken}-?space-[xy]-`),
    message: 'Separate siblings with flex/grid + gap-* instead of space-x-*/space-y-*.',
  },
  {
    selector: classMatching(String.raw`${classToken}font-(bold|extrabold|black)(\s|$)`),
    message: 'Use font weights 400/500/600 only (font-normal/font-medium/font-semibold).',
  },
  {
    selector: classMatching(
      String.raw`${classToken}(${paletteUtilities})-(${paletteHues})-(50|[1-9]00|950)(?![0-9])`,
    ),
    message:
      'Use semantic theme tokens (primary, muted, destructive, …) instead of palette colors.',
  },
  {
    selector: classMatching(String.raw`-\[#[0-9a-fA-F]{3,8}\]`),
    message: 'Use semantic theme tokens instead of hex color values.',
  },
  {
    selector: `:not(Element[name=/^(ng-icon|hlm-spinner)$/]) > ${classMatching(String.raw`${classToken}text-\[[0-9.]`)}`,
    message:
      'Use the font-size tiers (text-xs/sm/base/2xl/3xl); arbitrary text-[…] sizes are only for ng-icon and hlm-spinner.',
  },
];

const tests = ['**/*.spec.ts', '**/*.testing.ts'];

export default defineConfig(
  globalIgnores(['.angular/**', 'coverage/**', 'dist/**']),
  {
    files: ['**/*.ts'],
    plugins: {
      'import-x': importX,
      local: {
        rules: {
          'feature-boundaries': featureBoundaries,
          'no-status-code-branch': noStatusCodeBranch,
        },
      },
      'unused-imports': unusedImports,
    },
    settings: {
      // libs/ui 经 tsconfig paths 以 @spartan-ng/helm/* 引用，排序上与 npm 包同组
      'import-x/external-module-folders': ['node_modules', 'libs/ui'],
      // 显式解析器：解析不到的路径会被方向检查静默放过，所以同时开启 no-unresolved
      'import-x/resolver-next': [resolver],
    },
    extends: [
      eslint.configs.recommended,
      ...tseslint.configs.recommended,
      ...tseslint.configs.stylistic,
      ...angular.configs.tsRecommended,
      eslintConfigPrettier,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/component-selector': [
        'error',
        { type: 'element', prefix: 'app', style: 'kebab-case' },
      ],
      '@angular-eslint/directive-selector': [
        'error',
        { type: 'attribute', prefix: 'app', style: 'camelCase' },
      ],
      '@angular-eslint/no-pipe-impure': 'error',
      // 推荐集已含以下两条；显式登记，推荐集调整时闸门不随之静默消失。Angular 22 起 OnPush 是默认策略，
      // 规则拦的是显式退回 Eager/Default，不要求逐个组件写出 OnPush
      '@angular-eslint/prefer-inject': 'error',
      '@angular-eslint/prefer-on-push-component-change-detection': [
        'error',
        { allowExplicitOnPush: true },
      ],
      '@angular-eslint/prefer-output-readonly': 'error',
      '@typescript-eslint/no-unused-vars': 'off',
      'import-x/no-duplicates': 'error',
      'import-x/no-restricted-paths': [
        'error',
        { basePath: projectRoot, zones: directionZones({ mock: true }) },
      ],
      'import-x/no-unassigned-import': 'error',
      'import-x/no-unresolved': 'error',
      'import-x/order': [
        'error',
        {
          alphabetize: { order: 'asc' },
          'newlines-between': 'always',
          groups: ['builtin', 'external', 'internal', ['parent', 'sibling', 'index'], 'type'],
        },
      ],
      'local/feature-boundaries': 'error',
      'local/no-status-code-branch': 'error',
      'no-restricted-imports': ['error', { paths: [formsRestriction, titleRestriction] }],
      'unused-imports/no-unused-imports': 'error',
      'unused-imports/no-unused-vars': [
        'error',
        { vars: 'all', varsIgnorePattern: '^_', args: 'after-used', argsIgnorePattern: '^_' },
      ],
      'prefer-template': 'error',
      'no-restricted-syntax': ['error', importPathSyntax, dtoOutsideDtoFileSyntax],
    },
  },
  {
    files: ['**/*.dto.ts'],
    rules: {
      'no-restricted-syntax': ['error', importPathSyntax],
    },
  },
  {
    // 根组件负责合成浏览器标题，它的 spec 要注入 Title 断言结果
    files: ['src/app/app.ts', 'src/app/app.spec.ts'],
    rules: {
      'no-restricted-imports': ['error', { paths: [formsRestriction] }],
    },
  },
  {
    // 认证处置（401 跳转、受限会话的 403）与启动时的未登录判定属于协议层，按状态码分支
    files: ['src/app/core/interceptors/**/*.ts', 'src/app/core/services/startup-service.ts'],
    rules: {
      'local/no-status-code-branch': 'off',
    },
  },
  {
    files: tests,
    rules: {
      'import-x/no-restricted-paths': [
        'error',
        { basePath: projectRoot, zones: directionZones({ mock: false }) },
      ],
    },
  },
  {
    files: ['_mock/**/*.ts'],
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
  {
    files: ['**/*.html'],
    extends: [...angular.configs.templateRecommended, ...angular.configs.templateAccessibility],
    rules: {
      'no-restricted-syntax': ['error', ...templateClassSyntax],
    },
  },
);
