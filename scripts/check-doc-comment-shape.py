#!/usr/bin/env python3
"""Framework 与 Template 源码中的 XML 文档注释形态检查。

本闸门只强制机械可判且不依赖语义的规则：

1. Framework 的 `///` 不挂 private/internal 成员，避免实现说明进入随包 XML。
2. `///` 不写 IntelliSense 与 docfx 无法渲染的 Markdown `**…**`。

公共成员的 XML 覆盖由编译器 CS1591 保证。`<remarks>` 和 `<example>`
只输出统计；是否必要必须结合 API 契约判断，不设行数、比例或 DI 文件配额。
Template 不产出随包 XML，因此允许非公开成员使用 XML 注释；测试源码不在射程内。
"""
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..'))
# (标签, 根目录, 是否检查非公开成员, 是否跳过 tests)
SCAN_TARGETS = (
    ('Framework', os.path.join(ROOT, 'framework'), True, True),
    ('Template backend source', os.path.join(ROOT, 'template', 'backend', 'src'), False, False),
)

# 非公开成员的声明起始。`protected` 与 `protected internal` 对派生类可见，属公共表面，不在此列。
NONPUBLIC_RE = re.compile(r'^\s*(private|internal)\b(?!\s+protected)')
DOC_RE = re.compile(r'^\s*///')
SUMMARY_OPEN_RE = re.compile(r'^\s*///\s*<summary>')
REMARKS_OPEN_RE = re.compile(r'^\s*///\s*<remarks>')
EXAMPLE_RE = re.compile(r'^\s*///\s*<example>')

# `**` 也用于 URI 通配（`/api/health/**`）与 glob，那是字面量不是强调。
# 只在成对出现且中间无空白起止时判定为 Markdown 强调。
MD_BOLD_RE = re.compile(r'\*\*(?=\S)(.+?)(?<=\S)\*\*')

# 行内代码里的星号是字面量：掩码形态（`***`、`a***@example.com`）一行里出现两处时，
# 两个 `**` 会被上面的正则配成一对，判成强调——判定前先把 <c>…</c> 抠掉。
INLINE_CODE_RE = re.compile(r'<c>.*?</c>', re.S)


def iter_sources():
    for label, scan_root, check_nonpublic, skip_tests in SCAN_TARGETS:
        for dirpath, dirnames, filenames in os.walk(scan_root):
            excluded = {'obj', 'bin'}
            if skip_tests:
                excluded.add('tests')
            dirnames[:] = [d for d in dirnames if d not in excluded]
            for name in filenames:
                if not name.endswith('.cs'):
                    continue
                path = os.path.join(dirpath, name)
                rel = os.path.relpath(path, ROOT).replace(os.sep, '/')
                yield label, rel, path, check_nonpublic


def preceding_doc_block(lines, index):
    """返回紧挨 `index` 行之上的 `///` 行号区间（含），没有则 None。特性行可穿过。"""
    k = index - 1
    end = None
    while k >= 0:
        stripped = lines[k].strip()
        if DOC_RE.match(lines[k]):
            end = k if end is None else end
            k -= 1
            continue
        if stripped.startswith('[') or stripped == '':
            if end is not None:
                break
            k -= 1
            continue
        break
    if end is None:
        return None
    start = end
    while start - 1 >= 0 and DOC_RE.match(lines[start - 1]):
        start -= 1
    return start, end


def count_shape(path):
    """返回 (summary 数, remarks 数, example 数)。"""
    with open(path, encoding='utf-8') as fh:
        lines = fh.read().splitlines()
    s = sum(1 for l in lines if SUMMARY_OPEN_RE.match(l))
    r = sum(1 for l in lines if REMARKS_OPEN_RE.match(l))
    e = sum(1 for l in lines if EXAMPLE_RE.match(l))
    return s, r, e


def check(rel, path, problems, check_nonpublic):
    with open(path, encoding='utf-8') as fh:
        lines = fh.read().splitlines()

    # 规则 1 仅用于会生成随包 XML 的 Framework 源码。
    if check_nonpublic:
        for i, line in enumerate(lines):
            if not NONPUBLIC_RE.match(line):
                continue
            block = preceding_doc_block(lines, i)
            if block:
                problems.append(
                    f'{rel}:{i + 1} 非公开成员带 XML 注释（会进随包 .xml），改用行内 //：'
                    f'{line.strip()[:70]}')

    # 规则 2：XML 里的 Markdown 强调
    for i, line in enumerate(lines):
        if DOC_RE.match(line) and MD_BOLD_RE.search(INLINE_CODE_RE.sub('', line)):
            problems.append(
                f'{rel}:{i + 1} XML 注释里用了 Markdown 强调 **…**（不渲染），改用 <b>…</b>')


SELF_TEST_CASES = [
    # (规则, 片段, 是否应命中)
    ('nonpublic', '    private void Helper()', True),
    ('nonpublic', '    internal sealed class Guard', True),
    ('nonpublic', '    protected virtual Task RunAsync()', False),
    ('nonpublic', '    protected internal void Hook()', False),
    ('nonpublic', '    public void Api()', False),
    ('bold', '    /// 这是 <b>强调</b> 的正确写法', False),
    ('bold', '    /// 这是 **强调** 的错误写法', True),
    ('bold', '    /// 排除的URI模式（如：/api/health/**）', False),
    # 行内代码里的掩码星号：一行两处时旧正则会把它们配成一对强调
    ('bold', '    /// 中间换成 <c>***</c>，例如 <c>158***90</c>', False),
    # 抠掉行内代码之后，真正的强调仍要命中
    ('bold', '    /// <c>***</c> 之外还有 **真强调**', True),
]


def self_test():
    failures = []
    for rule, text, should in SELF_TEST_CASES:
        if rule == 'nonpublic':
            hit = bool(NONPUBLIC_RE.match(text))
        else:
            hit = bool(DOC_RE.match(text) and MD_BOLD_RE.search(INLINE_CODE_RE.sub('', text)))
        if hit != should:
            failures.append(f'  规则 {rule} 对 {text.strip()!r} 判定为 {hit}，期望 {should}')

    expected_scopes = {
        'Framework': True,
        'Template backend source': False,
    }
    actual_scopes = {
        label: check_nonpublic
        for label, _, check_nonpublic, _ in SCAN_TARGETS
    }
    if actual_scopes != expected_scopes:
        failures.append(f'  非公开成员规则作用域为 {actual_scopes!r}，期望 {expected_scopes!r}')
    if failures:
        print('❌ 规则自检失败：')
        print('\n'.join(failures))
        return 1
    print(f'✅ 规则自检通过（{len(SELF_TEST_CASES) + 1} 例）。')
    return 0


def main():
    if '--self-test' in sys.argv:
        return self_test()

    problems = []
    scanned = {label: 0 for label, _, _, _ in SCAN_TARGETS}
    shapes = {label: [0, 0, 0] for label in scanned}
    for label, rel, path, check_nonpublic in iter_sources():
        scanned[label] += 1
        check(rel, path, problems, check_nonpublic)
        shape = count_shape(path)
        shapes[label] = [a + b for a, b in zip(shapes[label], shape)]
    total = sum(scanned.values())
    scope = '，'.join(f'{label} {count} 个' for label, count in scanned.items())

    if problems:
        print(f'❌ XML 注释形态检查失败（共 {len(problems)} 项，已扫描 {total} 个文件：{scope}）：')
        for p in sorted(problems):
            print(f'  - {p}')
        print('\n判据见 docs/framework/development-guide.md §4.1「信息分层」。')
        return 1

    stats = []
    for label, (summary_count, remarks_count, example_count) in shapes.items():
        ratio = remarks_count / summary_count if summary_count else 0.0
        stats.append(
            f'{label} summary={summary_count}、remarks={remarks_count} ({ratio:.1%})、example={example_count}')
    print(f'✅ XML 注释形态检查通过（{total} 个文件：{scope}；'
          f'Framework 非公开成员无 XML，全部 XML 无 Markdown 强调）。')
    print('ℹ️ 注释统计（仅供趋势观察，不作闸门）：' + '；'.join(stats) + '。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
