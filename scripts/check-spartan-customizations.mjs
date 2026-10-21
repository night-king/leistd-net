#!/usr/bin/env node
/**
 * Spartan 定制登记表闸门（G-10）：与上游有差异的 helm 组件集合，必须等于
 * `template/docs/standards/frontend-spartan.md` 登记表列出的组件集合。
 *
 * 上游按 Spartan CLI 生成组件的同一条路径还原：读锁定版本 `@spartan-ng/cli` 自带的组件模板
 * （`src/generators/ui/libs/<组件>/files`），代入 `components.json` 的 `importAlias`，再经 CLI 的
 * `createStyleMap` 与 `transformStyle` 套上 `components.json` 选定的样式；本地取
 * `template/frontend/libs/ui/<组件>/src`。两边都用模板的 Prettier 配置规整后逐文件比较，
 * 文件多出、缺少或内容不同都算该组件与上游有差异。
 *
 * 边界：只比对"有差异的组件集合"。登记表每行改动描述是否与源码一致不在判据内，改 helm 组件或
 * 升级时人工逐项核对。登记表第一列里括号内的名字（文件名、子组件）是说明，不计入组件集合。
 *
 * 用法：
 *   node scripts/check-spartan-customizations.mjs <已安装依赖的前端目录>
 *   node scripts/check-spartan-customizations.mjs --self-test <已安装依赖的前端目录>
 *
 * 依赖目录是执行过 npm ci 的生成项目 frontend（模板源码的 package.json 带条件行，不能直接安装）：
 * 只借它的 node_modules（Spartan CLI 与 Prettier），被检查的始终是本仓库模板源码。CLI 版本必须等于模板
 * package.json 锁定的版本、Prettier 版本必须等于模板锁文件里的版本，否则还原出的"上游"不是
 * 模板所依赖的那一个。缺参数、缺依赖或版本不一致都直接失败，不跳过。
 */
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const repoRoot = path.resolve(import.meta.dirname, '..');
const templateFrontend = path.join(repoRoot, 'template', 'frontend');
const libsRoot = path.join(templateFrontend, 'libs', 'ui');
const registryFile = path.join(repoRoot, 'template', 'docs', 'standards', 'frontend-spartan.md');
const localizationFrontend = path.join(repoRoot, 'template', '.template.config', 'localization', 'frontend');
const templateManifests = [
  path.join(templateFrontend, 'package.json'),
  path.join(localizationFrontend, 'package.json'),
];
const templateLockFiles = [
  path.join(templateFrontend, 'package-lock.json'),
  path.join(localizationFrontend, 'package-lock.json'),
];
const REGISTRY_HEADING = '已定制的 helm 组件';

function fail(message) {
  console.error(`❌ ${message}`);
  process.exit(1);
}

function readJson(file) {
  return JSON.parse(readFileSync(file, 'utf8'));
}

function walk(dir, base = dir) {
  const files = [];
  for (const entry of readdirSync(dir)) {
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      files.push(...walk(full, base));
    } else {
      files.push(path.relative(base, full).split(path.sep).join('/'));
    }
  }
  return files.sort();
}

// ---------------------------------------------------------------------------
// 依赖
// ---------------------------------------------------------------------------

async function loadTooling(frontendArg) {
  if (!frontendArg) {
    fail('缺少参数：已安装依赖的前端目录（生成项目的 frontend，先 npm ci）。');
  }
  const frontendRoot = path.resolve(frontendArg);
  const manifest = path.join(frontendRoot, 'package.json');
  if (!existsSync(manifest)) fail(`${manifest} 不存在。`);
  const require = createRequire(manifest);

  let cliManifest;
  let prettierEntry;
  try {
    cliManifest = require.resolve('@spartan-ng/cli/package.json');
    prettierEntry = require.resolve('prettier');
  } catch {
    fail(`在 ${frontendRoot} 解析不到 @spartan-ng/cli 或 prettier，请先在该目录执行 npm ci。`);
  }

  // 模板的 package.json 与锁文件带 //#if 条件行，不是合法 JSON，按键读取版本；两份变体的 CLI 必须一致
  const pins = new Set(
    templateManifests.map(
      (file) => readFileSync(file, 'utf8').match(/"@spartan-ng\/cli"\s*:\s*"([^"]+)"/)?.[1],
    ),
  );
  if (pins.size !== 1 || pins.has(undefined)) {
    fail(`模板两份 package.json 锁定的 @spartan-ng/cli 不一致或缺失：${[...pins].join('、')}`);
  }
  const [lockedCli] = pins;
  const cliDir = path.dirname(cliManifest);
  const installedCli = readJson(cliManifest).version;
  if (installedCli !== lockedCli) {
    fail(`已安装的 @spartan-ng/cli ${installedCli} 与模板锁定的 ${lockedCli} 不一致，请重新 npm ci。`);
  }

  const prettierModule = await import(pathToFileURL(prettierEntry).href);
  const prettier = prettierModule.default ?? prettierModule;
  const lockedPrettier = new Set(
    templateLockFiles.map(
      (file) =>
        readFileSync(file, 'utf8').match(/"node_modules\/prettier"\s*:\s*\{\s*"version"\s*:\s*"([^"]+)"/)?.[1],
    ),
  );
  if (!lockedPrettier.has(prettier.version)) {
    fail(
      `已安装的 prettier ${prettier.version} 不是模板锁文件里的版本（${[...lockedPrettier].join('、')}），请重新 npm ci。`,
    );
  }

  const cliRequire = createRequire(cliManifest);
  const stylesDir = path.join(cliDir, 'src', 'generators', 'base', 'lib', 'styles');
  const { createStyleMap } = cliRequire(path.join(stylesDir, 'create-style-map.js'));
  const { transformStyle } = cliRequire(path.join(stylesDir, 'transform.js'));
  if (typeof createStyleMap !== 'function' || typeof transformStyle !== 'function') {
    fail(`@spartan-ng/cli ${installedCli} 里找不到 createStyleMap / transformStyle，CLI 内部结构已变，需要更新本闸门。`);
  }

  return { cliDir, cliVersion: installedCli, prettier, createStyleMap, transformStyle };
}

// ---------------------------------------------------------------------------
// 还原上游与读取本地
// ---------------------------------------------------------------------------

async function createFormatter(prettier) {
  const options = (await prettier.resolveConfig(path.join(libsRoot, 'probe.ts'))) ?? {};
  return (relPath, source) =>
    prettier.format(source, { ...options, filepath: path.join(libsRoot, relPath) });
}

async function restoreUpstream(tooling, components, format) {
  const config = readJson(path.join(templateFrontend, 'components.json'));
  const css = readFileSync(
    path.join(tooling.cliDir, 'src', 'generators', 'ui', `style-${config.style}.css`),
    'utf8',
  );
  const styleMap = tooling.createStyleMap(css);
  const libsDir = path.join(tooling.cliDir, 'src', 'generators', 'ui', 'libs');

  const upstream = new Map();
  for (const component of components) {
    const filesDir = path.join(libsDir, component, 'files');
    const files = new Map();
    if (existsSync(filesDir)) {
      for (const rel of walk(filesDir)) {
        const target = rel.replace(/\.template$/, '');
        let source = readFileSync(path.join(filesDir, rel), 'utf8').replace(
          /<%[-=]\s*importAlias\s*%>/g,
          config.importAlias,
        );
        if (source.includes('<%')) {
          throw new Error(`上游模板 ${component}/files/${rel} 含本闸门未处理的 EJS 片段，需要更新闸门。`);
        }
        source = await tooling.transformStyle(source, { styleMap });
        files.set(target, await format(`${component}/src/${target}`, source));
      }
    }
    upstream.set(component, files);
  }
  return upstream;
}

async function readLocal(components, format) {
  const local = new Map();
  for (const component of components) {
    const srcDir = path.join(libsRoot, component, 'src');
    const files = new Map();
    for (const rel of existsSync(srcDir) ? walk(srcDir) : []) {
      const source = readFileSync(path.join(srcDir, rel), 'utf8');
      files.set(rel, await format(`${component}/src/${rel}`, source));
    }
    local.set(component, files);
  }
  return local;
}

// ---------------------------------------------------------------------------
// 登记表与比较（纯函数，自检直接调用）
// ---------------------------------------------------------------------------

/** 读登记表第一列的组件名：只取括号之外的反引号名字。 */
export function parseRegistry(markdown) {
  const lines = markdown.split(/\r?\n/);
  const heading = lines.findIndex((line) => line.includes(REGISTRY_HEADING));
  if (heading < 0) throw new Error(`登记表说明行「${REGISTRY_HEADING}」不存在`);
  let index = heading + 1;
  while (index < lines.length && !lines[index].trimStart().startsWith('|')) index++;
  const rows = [];
  for (; index < lines.length && lines[index].trimStart().startsWith('|'); index++) {
    rows.push(lines[index]);
  }
  if (rows.length < 3) throw new Error('登记表没有数据行');

  const registered = new Set();
  for (const row of rows.slice(2)) {
    const firstCell = row.trim().replace(/^\|/, '').split('|')[0];
    const outsideParens = firstCell.replace(/（[^）]*）|\([^)]*\)/g, '');
    for (const match of outsideParens.matchAll(/`([^`]+)`/g)) {
      registered.add(match[1]);
    }
  }
  return registered;
}

function describeDifference(upstreamFiles, localFiles) {
  const notes = [];
  for (const rel of new Set([...upstreamFiles.keys(), ...localFiles.keys()])) {
    if (!localFiles.has(rel)) notes.push(`缺少上游文件 ${rel}`);
    else if (!upstreamFiles.has(rel)) notes.push(`多出文件 ${rel}`);
    else if (upstreamFiles.get(rel) !== localFiles.get(rel)) notes.push(`改动 ${rel}`);
  }
  return notes.sort();
}

/** 返回违规列表；空数组表示有差异的组件集合与登记集合相等。 */
export function compare({ components, upstream, local, registered }) {
  const violations = [];
  const known = new Set(components);
  for (const name of registered) {
    if (!known.has(name)) violations.push(`登记了 libs/ui 中不存在的组件：${name}`);
  }

  const differing = new Map();
  for (const component of components) {
    const notes = describeDifference(upstream.get(component), local.get(component));
    if (notes.length > 0) differing.set(component, notes);
  }
  for (const [component, notes] of differing) {
    if (!registered.has(component)) {
      violations.push(`与上游有差异但未登记：${component}（${notes.join('；')}）`);
    }
  }
  for (const name of registered) {
    if (known.has(name) && !differing.has(name)) {
      violations.push(`登记了但与上游没有差异：${name}（删去该行，或核对定制是否被覆盖）`);
    }
  }
  return { violations, differing };
}

// ---------------------------------------------------------------------------
// 自检
// ---------------------------------------------------------------------------

async function selfTest(tooling) {
  const format = await createFormatter(tooling.prettier);
  const components = ['badge', 'kbd', 'separator', 'sidebar'];
  const upstream = await restoreUpstream(tooling, components, format);
  for (const component of components) {
    if (upstream.get(component).size === 0) {
      fail(`自检前提不成立：上游没有组件 ${component}`);
    }
  }

  const clone = (map) => new Map([...map].map(([key, files]) => [key, new Map(files)]));
  const customized = () => {
    const local = clone(upstream);
    const badge = local.get('badge');
    const [first] = badge.keys();
    badge.set(first, badge.get(first).replace(/'/, "'gate-probe "));
    return local;
  };
  const registry = (rows) =>
    [
      `- **${REGISTRY_HEADING}**：`,
      '',
      '| 组件 | 改动 | 原因 |',
      '| --- | --- | --- |',
      ...rows,
      '',
      '表后正文',
    ].join('\n');
  const badgeRow = '| `badge` | 新增变体 | 理由 |';

  const cases = [
    {
      name: '合法：登记且存在差异，其余组件与上游一致',
      local: customized(),
      markdown: registry([badgeRow]),
      expect: [],
    },
    {
      name: '括号内的文件名与子组件不计入组件集合',
      local: (() => {
        const local = customized();
        local.get('sidebar').set('lib/extra.token.ts', 'export const x = 1;\n');
        return local;
      })(),
      markdown: registry([badgeRow, '| `sidebar`（`hlm-sidebar-trigger`、`kbd`） | 新增令牌 | 理由 |']),
      expect: [],
    },
    {
      name: '改了未登记的组件应失败',
      local: (() => {
        const local = customized();
        const kbd = local.get('kbd');
        const [first] = kbd.keys();
        kbd.set(first, kbd.get(first).replace(/'/, "'gate-probe "));
        return local;
      })(),
      markdown: registry([badgeRow]),
      expect: ['与上游有差异但未登记：kbd'],
    },
    {
      name: '组件里多出文件也算差异',
      local: (() => {
        const local = customized();
        local.get('separator').set('lib/extra.token.ts', 'export const x = 1;\n');
        return local;
      })(),
      markdown: registry([badgeRow]),
      expect: ['与上游有差异但未登记：separator（多出文件 lib/extra.token.ts）'],
    },
    {
      name: '删掉一行登记应失败',
      local: (() => {
        const local = customized();
        local.get('sidebar').set('lib/extra.token.ts', 'export const x = 1;\n');
        return local;
      })(),
      markdown: registry(['| `sidebar` | 新增令牌 | 理由 |']),
      expect: ['与上游有差异但未登记：badge'],
    },
    {
      name: '登记了但没有差异应失败',
      local: customized(),
      markdown: registry([badgeRow, '| `separator` | 已被上游覆盖的旧定制 | 理由 |']),
      expect: ['登记了但与上游没有差异：separator'],
    },
    {
      name: '登记了不存在的组件应失败',
      local: customized(),
      markdown: registry([badgeRow, '| `no-such-component` | 拼错 | 理由 |']),
      expect: ['登记了 libs/ui 中不存在的组件：no-such-component'],
    },
  ];

  let failures = 0;
  for (const testCase of cases) {
    let violations;
    try {
      ({ violations } = compare({
        components,
        upstream,
        local: testCase.local,
        registered: parseRegistry(testCase.markdown),
      }));
    } catch (error) {
      violations = [`异常：${error.message}`];
    }
    const ok =
      testCase.expect.length === 0
        ? violations.length === 0
        : testCase.expect.every((expected) => violations.some((v) => v.includes(expected)));
    console.log(`${ok ? '✅' : '❌'} ${testCase.name}`);
    if (!ok) {
      failures++;
      console.log(`   期望：${testCase.expect.join('；') || '无违规'}`);
      for (const violation of violations) console.log(`   实际：${violation}`);
    }
  }

  // 解析不到登记表时必须报错，而不是当作"零登记"
  try {
    parseRegistry('# 没有登记表');
    console.log('❌ 缺少登记表应报错');
    failures++;
  } catch {
    console.log('✅ 缺少登记表应报错');
  }

  const total = cases.length + 1;
  console.log(`自检：${total - failures}/${total} 通过（@spartan-ng/cli ${tooling.cliVersion}）`);
  return failures === 0 ? 0 : 1;
}

// ---------------------------------------------------------------------------
// 入口
// ---------------------------------------------------------------------------

async function main() {
  const args = process.argv.slice(2);
  const isSelfTest = args[0] === '--self-test';
  const tooling = await loadTooling(isSelfTest ? args[1] : args[0]);
  if (isSelfTest) return selfTest(tooling);

  const components = readdirSync(libsRoot)
    .filter((entry) => statSync(path.join(libsRoot, entry)).isDirectory())
    .sort();
  const format = await createFormatter(tooling.prettier);
  const upstream = await restoreUpstream(tooling, components, format);
  const local = await readLocal(components, format);
  let registered;
  try {
    registered = parseRegistry(readFileSync(registryFile, 'utf8'));
  } catch (error) {
    fail(`${path.relative(repoRoot, registryFile)}：${error.message}`);
  }

  const { violations, differing } = compare({ components, upstream, local, registered });
  if (violations.length > 0) {
    console.error(`❌ Spartan 定制登记表与 libs/ui 实际差异不一致（@spartan-ng/cli ${tooling.cliVersion}）：`);
    for (const violation of violations) console.error(`   ${violation}`);
    return 1;
  }
  console.log(
    `✅ Spartan 定制登记表与上游差异一致：${differing.size} 个已定制组件（${[...differing.keys()].join('、')}），共 ${components.length} 个组件，@spartan-ng/cli ${tooling.cliVersion}。`,
  );
  return 0;
}

process.exit(await main());
