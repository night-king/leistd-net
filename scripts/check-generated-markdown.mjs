#!/usr/bin/env node
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const [projectsPath, prettierPath, expectedVersion, output] = process.argv.slice(2);
const prettier = await import(pathToFileURL(path.resolve(prettierPath)).href);
if (prettier.version !== expectedVersion) throw new Error('Prettier differs from template lock');
const seen = new Set();
let files = 0;
function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    if (['node_modules', '.git', '.angular', 'dist', 'coverage'].includes(entry.name)) return [];
    const file = path.join(dir, entry.name);
    return entry.isDirectory() ? walk(file) : file.endsWith('.md') ? [file] : [];
  });
}
for (const project of JSON.parse(readFileSync(projectsPath, 'utf8'))) {
  const frontend = path.join(project, 'frontend');
  if (!readdirSync(project).includes('frontend')) continue;
  for (const file of walk(frontend)) {
    const info = await prettier.getFileInfo(file, { ignorePath: path.join(frontend, '.prettierignore') });
    if (info.ignored) continue;
    const options = (await prettier.resolveConfig(file, { editorconfig: true })) ?? {};
    const source = readFileSync(file, 'utf8');
    const key = createHash('sha256').update(source).update(JSON.stringify(options)).update(path.extname(file)).digest('hex');
    files++;
    if (seen.has(key)) continue;
    if (!(await prettier.check(source, { ...options, filepath: file }))) throw new Error(`Generated Markdown format: ${file}`);
    seen.add(key);
  }
}
if (files === 0 || seen.size === 0) throw new Error('No generated frontend Markdown checked');
await import('node:fs/promises').then((fs) => fs.writeFile(output, JSON.stringify({ Files: files, UniqueContents: seen.size })));
console.log(`Generated frontend Markdown: ${files} files, ${seen.size} distinct contents/configurations.`);
