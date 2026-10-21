#!/usr/bin/env node
/**
 * 模板前端 lint 规则（闸门 G-07）的自检：每个诊断各有违规片段、合法片段与允许的例外，
 * 全部经 ESLint 的 lintText 在内存中检查，不写入任何文件。
 *
 * 用法：node scripts/test-template-eslint-rules.mjs <已安装依赖的生成项目 frontend 目录>
 *
 * 规则写在 template/frontend/eslint.config.mjs，而它引用的插件只装在生成项目里，所以借生成项目的
 * node_modules 运行；开跑前先核对该目录的配置与模板源逐字节一致，保证检的是当前源码而不是旧产物。
 * 缺参数、缺依赖或配置不一致都直接失败，不跳过。
 */
import { readFileSync, existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const repoRoot = path.resolve(import.meta.dirname, '..');
const sourceConfig = path.join(repoRoot, 'template', 'frontend', 'eslint.config.mjs');

function fail(message) {
  console.error(`❌ ${message}`);
  process.exit(1);
}

const frontendArg = process.argv[2];
if (!frontendArg) {
  fail('缺少参数：已安装依赖的生成项目 frontend 目录（先 dotnet new fullstack-app 再 npm ci）。');
}
const frontendRoot = path.resolve(frontendArg);
const projectConfig = path.join(frontendRoot, 'eslint.config.mjs');
if (!existsSync(projectConfig)) fail(`${projectConfig} 不存在。`);
if (!readFileSync(projectConfig).equals(readFileSync(sourceConfig))) {
  fail(`${projectConfig} 与模板源 ${sourceConfig} 不一致，请重新生成项目后再运行。`);
}

let eslintEntry;
try {
  eslintEntry = createRequire(path.join(frontendRoot, 'package.json')).resolve('eslint');
} catch {
  fail(`在 ${frontendRoot} 解析不到 eslint，请先在该目录执行 npm ci。`);
}
const { ESLint } = await import(pathToFileURL(eslintEntry).href);
const eslint = new ESLint({ cwd: frontendRoot });

const feature = 'src/app/features/demo';
const component = (metadata = '') => `import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-demo',
  template: '<p>demo</p>',${metadata}
})
export class Demo {
  readonly strategy = ChangeDetectionStrategy;
}
`;
const statusSnippet = (condition) => `import { ApplicationHttpError } from '../../core/errors/application-http-error';

export function handle(err: ApplicationHttpError, state: { status: string }): boolean {
  return ${condition};
}
`;
const titleImport = `import { Title } from '@angular/platform-browser';

export const title = Title;
`;

const onPush = '@angular-eslint/prefer-on-push-component-change-detection';
const inject = '@angular-eslint/prefer-inject';
const statusBranch = 'local/no-status-code-branch';
const syntax = 'no-restricted-syntax';
const imports = 'no-restricted-imports';
const dto = 'Declare API contract types (*Dto)';
const title = 'Set LayoutService.title';
const forms = 'Use Angular Signal Forms';
const space = 'instead of space-x-*/space-y-*';
const weight = 'Use font weights 400/500/600';
const palette = 'instead of palette colors';
const hex = 'instead of hex color values';
const fontSize = 'Use the font-size tiers';

/**
 * @type {{ name: string, file: string, code: string, rule: string, message?: string, expect: 'report' | 'clean' }[]}
 * `expect: 'clean'` 覆盖合法写法与允许的例外，只断言目标诊断不出现。
 */
const cases = [
  // OnPush：Angular 22 默认即 OnPush，规则拦显式退回
  { name: 'OnPush 违规：Eager', file: `${feature}/demo.ts`, code: component('\n  changeDetection: ChangeDetectionStrategy.Eager,'), rule: onPush, expect: 'report' },
  { name: 'OnPush 违规：Default', file: `${feature}/demo.ts`, code: component('\n  changeDetection: ChangeDetectionStrategy.Default,'), rule: onPush, expect: 'report' },
  { name: 'OnPush 合法：省略即默认', file: `${feature}/demo.ts`, code: component(), rule: onPush, expect: 'clean' },
  { name: 'OnPush 例外：显式写出 OnPush', file: `${feature}/demo.ts`, code: component('\n  changeDetection: ChangeDetectionStrategy.OnPush,'), rule: onPush, expect: 'clean' },

  // inject()
  {
    name: 'inject 违规：构造函数注入',
    file: `${feature}/services/demo-service.ts`,
    code: `import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class DemoService {
  constructor(private readonly http: HttpClient) {}
}
`,
    rule: inject,
    expect: 'report',
  },
  {
    name: 'inject 合法：字段 inject()',
    file: `${feature}/services/demo-service.ts`,
    code: `import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class DemoService {
  readonly http = inject(HttpClient);
}
`,
    rule: inject,
    expect: 'clean',
  },
  {
    name: 'inject 例外：非 Angular 类的构造参数',
    file: `${feature}/models/demo.model.ts`,
    code: `export class Demo {
  constructor(readonly data: Partial<Demo>) {}
}
`,
    rule: inject,
    expect: 'clean',
  },

  // 不按单个状态码分支
  { name: '状态码 违规：status === 409', file: `${feature}/demo.ts`, code: statusSnippet('err.status === 409'), rule: statusBranch, expect: 'report' },
  { name: '状态码 违规：字面量在左', file: `${feature}/demo.ts`, code: statusSnippet('404 == err.status'), rule: statusBranch, expect: 'report' },
  {
    name: '状态码 违规：可选链与 HttpStatusCode',
    file: `${feature}/demo.ts`,
    code: `import { HttpStatusCode } from '@angular/common/http';

export const conflict = (err?: { status: number }) => err?.status !== HttpStatusCode.Conflict;
`,
    rule: statusBranch,
    expect: 'report',
  },
  {
    name: '状态码 违规：switch case',
    file: `${feature}/demo.ts`,
    code: `export function kind(err: { status: number }): string {
  switch (err.status) {
    case 409:
      return 'conflict';
    default:
      return 'other';
  }
}
`,
    rule: statusBranch,
    expect: 'report',
  },
  { name: '状态码 违规：[…].includes(status)', file: `${feature}/demo.ts`, code: statusSnippet('[409, 412].includes(err.status)'), rule: statusBranch, expect: 'report' },
  { name: '状态码 合法：按 code 分支', file: `${feature}/demo.ts`, code: statusSnippet(`err.code === 'Permission:ConcurrencyConflict'`), rule: statusBranch, expect: 'clean' },
  { name: '状态码 合法：status === 0（网络不可达）', file: `${feature}/demo.ts`, code: statusSnippet('err.status === 0'), rule: statusBranch, expect: 'clean' },
  { name: '状态码 合法：区间判断', file: `${feature}/demo.ts`, code: statusSnippet('err.status >= 500'), rule: statusBranch, expect: 'clean' },
  { name: '状态码 合法：非 HTTP 的 status 字段', file: `${feature}/demo.ts`, code: statusSnippet(`state.status === 'saving'`), rule: statusBranch, expect: 'clean' },
  { name: '状态码 例外：拦截器', file: 'src/app/core/interceptors/demo-interceptor.ts', code: statusSnippet('err.status === 401').replace('../../core/', '../'), rule: statusBranch, expect: 'clean' },
  { name: '状态码 例外：startup-service', file: 'src/app/core/services/startup-service.ts', code: statusSnippet('err.status === 401').replace('../../core/', '../'), rule: statusBranch, expect: 'clean' },

  // DTO 只放 .dto.ts
  { name: 'DTO 违规：models 里的 interface', file: 'src/app/shared/models/demo.model.ts', code: 'export interface DemoDto {\n  id: string;\n}\n', rule: syntax, message: dto, expect: 'report' },
  { name: 'DTO 违规：服务里的 type', file: `${feature}/services/demo-service.ts`, code: 'export type DemoOutputDto = { id: string };\n', rule: syntax, message: dto, expect: 'report' },
  { name: 'DTO 合法：前端模型', file: 'src/app/shared/models/demo.model.ts', code: 'export interface Demo {\n  id: string;\n}\n', rule: syntax, message: dto, expect: 'clean' },
  { name: 'DTO 例外：.dto.ts 文件', file: 'src/app/shared/dtos/demo.dto.ts', code: 'export interface DemoDto {\n  id: string;\n}\n', rule: syntax, message: dto, expect: 'clean' },

  // 页面不自调 Title
  { name: 'Title 违规：页面导入 Title', file: `${feature}/demo.ts`, code: titleImport, rule: imports, message: title, expect: 'report' },
  {
    name: 'Title 合法：同包其他导出',
    file: `${feature}/demo.ts`,
    code: `import { DomSanitizer } from '@angular/platform-browser';

export const sanitizer = DomSanitizer;
`,
    rule: imports,
    message: title,
    expect: 'clean',
  },
  { name: 'Title 例外：app.ts', file: 'src/app/app.ts', code: titleImport, rule: imports, message: title, expect: 'clean' },
  { name: 'Title 例外：app.spec.ts', file: 'src/app/app.spec.ts', code: titleImport, rule: imports, message: title, expect: 'clean' },
  {
    name: 'Title 例外块不放开表单限制',
    file: 'src/app/app.ts',
    code: `import { FormsModule } from '@angular/forms';

export const forms = FormsModule;
`,
    rule: imports,
    message: forms,
    expect: 'report',
  },
  {
    name: '表单 合法：Signal Forms',
    file: 'src/app/app.ts',
    code: `import { form } from '@angular/forms/signals';

export const create = form;
`,
    rule: imports,
    message: forms,
    expect: 'clean',
  },

  // 模板类名：间距
  { name: '间距 违规：space-y', file: `${feature}/demo.html`, code: '<div class="flex flex-col space-y-2"></div>', rule: syntax, message: space, expect: 'report' },
  { name: '间距 违规：带变体的负 space-x', file: `${feature}/demo.html`, code: '<div class="flex md:-space-x-2"></div>', rule: syntax, message: space, expect: 'report' },
  {
    name: '间距 违规：组件内联模板',
    file: `${feature}/demo.ts`,
    code: `import { Component } from '@angular/core';

@Component({
  selector: 'app-demo',
  template: '<div class="space-y-4"></div>',
})
export class Demo {}
`,
    rule: syntax,
    message: space,
    expect: 'report',
  },
  { name: '间距 合法：gap', file: `${feature}/demo.html`, code: '<div class="flex flex-col gap-2"></div>', rule: syntax, message: space, expect: 'clean' },
  { name: '间距 已知边界：[class] 绑定表达式不检查', file: `${feature}/demo.html`, code: `<div [class]="'space-y-2'"></div>`, rule: syntax, message: space, expect: 'clean' },

  // 模板类名：字重
  { name: '字重 违规：font-bold', file: `${feature}/demo.html`, code: '<h2 class="text-sm font-bold">x</h2>', rule: syntax, message: weight, expect: 'report' },
  { name: '字重 违规：带变体的 font-extrabold', file: `${feature}/demo.html`, code: '<h2 class="hover:font-extrabold">x</h2>', rule: syntax, message: weight, expect: 'report' },
  { name: '字重 违规：[class.font-bold] 绑定', file: `${feature}/demo.html`, code: '<h2 [class.font-bold]="active">x</h2>', rule: syntax, message: weight, expect: 'report' },
  { name: '字重 合法：font-semibold', file: `${feature}/demo.html`, code: '<h2 class="text-sm font-semibold">x</h2>', rule: syntax, message: weight, expect: 'clean' },

  // 模板类名：具体色板
  { name: '色板 违规：bg-blue-500', file: `${feature}/demo.html`, code: '<div class="bg-blue-500"></div>', rule: syntax, message: palette, expect: 'report' },
  { name: '色板 违规：暗色变体加透明度', file: `${feature}/demo.html`, code: '<div class="dark:text-red-600/50"></div>', rule: syntax, message: palette, expect: 'report' },
  { name: '色板 违规：单边边框 50 档', file: `${feature}/demo.html`, code: '<div class="border-x-gray-50"></div>', rule: syntax, message: palette, expect: 'report' },
  { name: '色板 合法：语义令牌', file: `${feature}/demo.html`, code: '<div class="bg-primary/10 text-primary border-destructive"></div>', rule: syntax, message: palette, expect: 'clean' },
  { name: '色板 例外：图像遮罩 bg-black/25 text-white', file: `${feature}/demo.html`, code: '<div class="bg-black/25 text-white"></div>', rule: syntax, message: palette, expect: 'clean' },
  { name: '色板 例外：二维码底色 bg-white', file: `${feature}/demo.html`, code: '<div class="bg-white p-2"></div>', rule: syntax, message: palette, expect: 'clean' },

  // 模板类名：十六进制色值
  { name: '色值 违规：bg-[#ff0000]', file: `${feature}/demo.html`, code: '<div class="bg-[#ff0000]"></div>', rule: syntax, message: hex, expect: 'report' },
  { name: '色值 合法：变量任意值', file: `${feature}/demo.html`, code: '<div class="bg-(--sidebar)"></div>', rule: syntax, message: hex, expect: 'clean' },
  {
    name: '色值 例外：品牌 SVG 的 fill 属性',
    file: `${feature}/demo.html`,
    code: '<svg viewBox="0 0 1 1" aria-hidden="true"><path fill="#24292f" d="M0 0h1v1H0z" /></svg>',
    rule: syntax,
    message: hex,
    expect: 'clean',
  },

  // 模板类名：档外字号
  { name: '字号 违规：text-[10px]', file: `${feature}/demo.html`, code: '<span class="text-[10px]">x</span>', rule: syntax, message: fontSize, expect: 'report' },
  { name: '字号 违规：text-[0.8rem]', file: `${feature}/demo.html`, code: '<span class="sm:text-[0.8rem]">x</span>', rule: syntax, message: fontSize, expect: 'report' },
  { name: '字号 合法：text-xs', file: `${feature}/demo.html`, code: '<span class="text-xs">x</span>', rule: syntax, message: fontSize, expect: 'clean' },
  { name: '字号 例外：ng-icon 图标尺寸', file: `${feature}/demo.html`, code: '<ng-icon name="lucideX" class="text-[10px]" />', rule: syntax, message: fontSize, expect: 'clean' },
  { name: '字号 例外：hlm-spinner 尺寸', file: `${feature}/demo.html`, code: '<hlm-spinner class="text-[10px]" />', rule: syntax, message: fontSize, expect: 'clean' },
];

const failures = [];
for (const testCase of cases) {
  const [result] = await eslint.lintText(testCase.code, {
    filePath: path.join(frontendRoot, testCase.file),
    warnIgnored: true,
  });
  const fatal = result.messages.filter((m) => m.fatal || m.ruleId === null);
  if (fatal.length > 0) {
    failures.push(`${testCase.name}：片段无法检查（${fatal.map((m) => m.message).join('；')}）`);
    continue;
  }
  const hits = result.messages.filter(
    (m) => m.ruleId === testCase.rule && (!testCase.message || m.message.includes(testCase.message)),
  );
  if (testCase.expect === 'report' && hits.length === 0) {
    failures.push(`${testCase.name}：应报 ${testCase.rule}${testCase.message ? `（${testCase.message}）` : ''}，实际未报`);
  } else if (testCase.expect === 'clean' && hits.length > 0) {
    failures.push(`${testCase.name}：不应报，实际报了「${hits[0].message}」`);
  }
}

// 每个诊断都必须同时有违规与放行用例，防止删掉一半后自检照样通过
const diagnostics = new Map();
for (const testCase of cases) {
  const key = `${testCase.rule}|${testCase.message ?? ''}`;
  const kinds = diagnostics.get(key) ?? new Set();
  kinds.add(testCase.expect);
  diagnostics.set(key, kinds);
}
for (const [key, kinds] of diagnostics) {
  if (!kinds.has('report') || !kinds.has('clean')) {
    failures.push(`诊断 ${key} 缺少${kinds.has('report') ? '放行' : '违规'}用例`);
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`❌ ${failure}`);
  console.error(`模板前端 lint 规则自检失败：${failures.length}/${cases.length}。`);
  process.exit(1);
}
console.log(`✅ 模板前端 lint 规则自检通过（${diagnostics.size} 个诊断，${cases.length} 个用例）。`);
