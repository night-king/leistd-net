#!/usr/bin/env python3
"""Markdown 章节锚点检查：`[文字](file.md#章节)` 与 `[文字](#章节)` 指向的标题必须存在。

文件级链接由 `check-doc-references.py` 检查，它丢弃 `#` 之后的部分；
章节改名、拆分到别的文件、或被模板条件裁剪删掉时，链接照样"能打开"，只是落在文件顶部——
读者与 AI 都不会察觉跳错了位置。本检查补上这一半：

- 锚点按 GitHub 渲染规则从标题算出（github-slugger）：小写；删除字母、数字、组合符号、
  连接符（`_`）、空格与 `-` 以外的字符（中文保留，全角标点删除）；空格逐个换成 `-`；
  重名标题依次加 `-1`、`-2`。标题里的行内代码、链接、强调按渲染后的文字计。
- 围栏代码块里的 `#` 行不是标题，里面的链接也不检查；行内代码里的链接同样不检查。
- `<a id="x">` / `<a name="x">` 显式锚点视为有效。
- 只检查指向 `.md` 的链接；外链、非 Markdown 目标与目标文件不存在的链接不在本检查范围
  （文件存在性由 check-doc-references.py 负责）。

用法：
  python3 scripts/check-markdown-anchors.py <目录>...   检查目录下全部 .md
  python3 scripts/check-markdown-anchors.py --self-test  规则自检

矩阵对**生成后的项目**运行本检查：条件裁剪删掉被链接章节的情形只有在生成产物上才看得见。
"""
import os
import re
import shutil
import sys
import tempfile
import unicodedata
from urllib.parse import unquote

SKIPPED_DIRS = {'node_modules', 'bin', 'obj', '.git', 'dist', '.angular', '.tmp'}

HEADING_RE = re.compile(r'^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$')
FENCE_RE = re.compile(r'^ {0,3}(`{3,}|~{3,})')
LINK_RE = re.compile(r'!?\[[^\]]*\]\((?P<target>[^)]+)\)')
INLINE_CODE_RE = re.compile(r'(`+)(.+?)\1')
HTML_ANCHOR_RE = re.compile(r'<a\s+[^>]*?(?:id|name)\s*=\s*["\']([^"\']+)["\']', re.IGNORECASE)
SCHEME_RE = re.compile(r'^[A-Za-z][A-Za-z0-9+.-]*:')


def heading_text(raw):
    """把标题源码折成渲染后的纯文本（GitHub 用渲染文本算锚点）。"""
    codes = []

    def keep_code(match):
        codes.append(match.group(2).strip())
        return f'\x00{len(codes) - 1}\x00'

    text = INLINE_CODE_RE.sub(keep_code, raw)
    text = re.sub(r'!\[([^\]]*)\]\([^)]*\)', r'\1', text)        # 图片 → alt
    text = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', text)         # 链接 → 文字
    text = re.sub(r'<[^>]+>', '', text)                           # 行内 HTML
    text = re.sub(r'(?<!\w)(\*{1,3}|_{1,3})(?=\S)(.+?)(?<=\S)\1(?!\w)', r'\2', text)  # 强调
    return re.sub(r'\x00(\d+)\x00', lambda m: codes[int(m.group(1))], text)


def slug_base(text):
    kept = []
    for char in text.lower():
        category = unicodedata.category(char)
        if char in (' ', '-') or category[0] in ('L', 'M', 'N') or category == 'Pc':
            kept.append(char)
    return ''.join(kept).replace(' ', '-')


class Slugger:
    """与 github-slugger 同算法：重名时在原 slug 后递增编号，且跳过已被占用的结果。"""

    def __init__(self):
        self.occurrences = {}

    def slug(self, text):
        original = slug_base(text)
        result = original
        while result in self.occurrences:
            self.occurrences[original] += 1
            result = f'{original}-{self.occurrences[original]}'
        self.occurrences[result] = 0
        return result


def iter_lines_outside_fences(lines):
    """逐行产出 (行号, 文本)，跳过围栏代码块与开头的 YAML frontmatter。"""
    index = 0
    if lines and lines[0].strip() == '---':
        for closing in range(1, len(lines)):
            if lines[closing].strip() == '---':
                index = closing + 1
                break
    fence = None
    for number in range(index, len(lines)):
        line = lines[number]
        match = FENCE_RE.match(line)
        if fence:
            if match and match.group(1)[0] == fence[0] and len(match.group(1)) >= len(fence) \
                    and not line.strip().lstrip(fence[0]):
                fence = None
            continue
        if match:
            fence = match.group(1)
            continue
        yield number + 1, line


def collect_anchors(lines):
    slugger = Slugger()
    anchors = set()
    for _, line in iter_lines_outside_fences(lines):
        match = HEADING_RE.match(line)
        if match:
            anchors.add(slugger.slug(heading_text(match.group(2) or '')))
        for html_anchor in HTML_ANCHOR_RE.findall(line):
            anchors.add(html_anchor)
    return anchors


def iter_anchor_links(lines):
    """产出 (行号, 原始目标, 路径部分, 片段)；只含带片段的链接。"""
    for number, line in iter_lines_outside_fences(lines):
        visible = INLINE_CODE_RE.sub('', line)
        for match in LINK_RE.finditer(visible):
            raw = match.group('target').strip()
            if raw.startswith('<') and '>' in raw:
                raw = raw[1:raw.index('>')]
            else:
                raw = raw.split()[0] if raw.split() else ''
            if '#' not in raw or SCHEME_RE.match(raw):
                continue
            path, fragment = raw.split('#', 1)
            path = path.split('?', 1)[0]
            yield number, raw, unquote(path), unquote(fragment)


def read_lines(path):
    with open(path, encoding='utf-8') as handle:
        return handle.read().splitlines()


def check_root(root):
    root = os.path.abspath(root)
    anchor_cache = {}

    def anchors_of(path):
        if path not in anchor_cache:
            anchor_cache[path] = collect_anchors(read_lines(path))
        return anchor_cache[path]

    problems = []
    checked = 0
    for directory, subdirectories, files in os.walk(root):
        subdirectories[:] = sorted(d for d in subdirectories if d not in SKIPPED_DIRS)
        for name in sorted(files):
            if not name.lower().endswith('.md'):
                continue
            source = os.path.join(directory, name)
            for number, raw, path, fragment in iter_anchor_links(read_lines(source)):
                if not fragment or re.search(r'[{}*]', path):
                    continue
                if path:
                    if not path.lower().endswith('.md'):
                        continue
                    target = os.path.join(root, path.lstrip('/')) if path.startswith('/') \
                        else os.path.join(directory, path)
                    target = os.path.normpath(target)
                    if not os.path.isfile(target):
                        continue
                else:
                    target = source
                checked += 1
                if fragment not in anchors_of(target):
                    relative = os.path.relpath(source, root).replace(os.sep, '/')
                    problems.append((relative, number, raw, fragment))
    return problems, checked


SELF_TEST_FILES = {
    'docs/a.md': '\n'.join([
        '---',
        'title: frontmatter 里的 # 不是标题',
        '---',
        '# 总览',
        '## 1. 安装（Install）',
        '## 重复',
        '## 重复',
        '## `IClock` 接口',
        '## **强调**与 [链接](https://example.com) 标题',
        '```md',
        '## 代码里的标题',
        '[围栏内链接不检查](#nope-in-fence)',
        '```',
        '[有效跨文件](b.md#目标章节)',
        '[有效跨文件中文与标点](./b.md#3-数据访问ef-core)',
        '[有效本文件](#1-安装install)',
        '[有效重复标题](#重复-1)',
        '[有效行内代码标题](#iclock-接口)',
        '[有效强调与链接标题](#强调与-链接-标题)',
        '[有效显式锚点](b.md#custom-id)',
        '[有效百分号编码](b.md#%E7%9B%AE%E6%A0%87%E7%AB%A0%E8%8A%82)',
        '[缺失跨文件](b.md#不存在)',
        '[缺失重复编号](#重复-2)',
        '[围栏中的标题不算](#代码里的标题)',
        '[裁剪删掉的章节](b.md#仅本地化分支)',
        '[frontmatter 不算](#不是标题)',
        '`[行内代码里的链接不检查](#nope-inline)`',
        '[外链不检查](https://example.com/x.md#nope)',
        '[非 Markdown 不检查](../src/a.cs#L10)',
        '[目标不存在由文件检查负责](missing.md#x)',
    ]),
    # 模拟条件裁剪后的生成产物：`仅本地化分支` 一节已被删除
    'docs/b.md': '\n'.join([
        '# B',
        '## 目标章节',
        '## 3. 数据访问（EF Core）',
        '<a id="custom-id"></a>',
    ]),
}

SELF_TEST_EXPECTED = {
    ('docs/a.md', '不存在'),
    ('docs/a.md', '重复-2'),
    ('docs/a.md', '代码里的标题'),
    ('docs/a.md', '仅本地化分支'),
    ('docs/a.md', '不是标题'),
}

SELF_TEST_SLUGS = [
    ('9. 多语言（i18n）', '9-多语言i18n'),
    ('AI 协作', 'ai-协作'),
    ('3.8.1 权限定义与界面一一对应', '381-权限定义与界面一一对应'),
    ('Leistd 框架 API', 'leistd-框架-api'),
    ('A  B', 'a--b'),
    ('snake_case 与 C#', 'snake_case-与-c'),
]


def self_test():
    failures = []
    for text, expected in SELF_TEST_SLUGS:
        actual = Slugger().slug(heading_text(text))
        if actual != expected:
            failures.append(f'  标题「{text}」算得 {actual!r}，期望 {expected!r}')

    slugger = Slugger()
    sequence = [slugger.slug(t) for t in ('x', 'x', 'x-1', 'x')]
    if sequence != ['x', 'x-1', 'x-1-1', 'x-2']:
        failures.append(f'  重名编号序列为 {sequence}，期望 github-slugger 的 x, x-1, x-1-1, x-2')

    workspace = tempfile.mkdtemp(prefix='md-anchors-')
    try:
        for relative, content in SELF_TEST_FILES.items():
            path = os.path.join(workspace, relative)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'w', encoding='utf-8') as handle:
                handle.write(content + '\n')
        problems, checked = check_root(workspace)
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    actual = {(relative, fragment) for relative, _, _, fragment in problems}
    if actual != SELF_TEST_EXPECTED:
        failures.append(f'  夹具命中 {sorted(actual)}，期望 {sorted(SELF_TEST_EXPECTED)}')
    if checked != 13:
        failures.append(f'  夹具检查了 {checked} 条锚点链接，期望 13（被跳过的链接种类有变）')

    if failures:
        print('❌ 锚点规则自检失败：')
        print('\n'.join(failures))
        return 1
    print(f'✅ 锚点规则自检通过（{len(SELF_TEST_SLUGS) + 2} 例）。')
    return 0


def main(argv):
    if '--self-test' in argv:
        return self_test()
    roots = [a for a in argv if not a.startswith('-')]
    if not roots:
        print('用法：check-markdown-anchors.py <目录>... | --self-test', file=sys.stderr)
        return 2

    problems = []
    checked = 0
    for root in roots:
        if not os.path.isdir(root):
            print(f'❌ 目录不存在：{root}', file=sys.stderr)
            return 2
        root_problems, root_checked = check_root(root)
        checked += root_checked
        problems += [(root, *p) for p in root_problems]

    if problems:
        print(f'❌ Markdown 章节锚点失效（{len(problems)} 处）：')
        for root, relative, number, raw, fragment in problems:
            print(f'  {relative}:{number} -> {raw}（目标文件没有标题锚点 #{fragment}）')
        return 1
    print(f'✅ Markdown 章节锚点有效（{checked} 条）。')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
