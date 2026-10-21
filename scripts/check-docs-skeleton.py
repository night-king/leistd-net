#!/usr/bin/env python3
"""组件文档骨架检查（`docs/framework/development-guide.md` §4.3）。

组件文档是消费者与 AI 的入口。**标题固定才能被精确定位**——AI 要在几十篇文档里找
「这个组件怎么注册」，靠的是 `## 安装` / `## 注册` / `## 使用` 这类恒定标题，不是全文扫描。
骨架因此是规范而非建议，本闸门把其中机械可判的四条写成规则：

1. **必选段齐全且相对顺序固定**：`何时使用` → `安装` → `使用` → `接口参考` → `注意事项`。
2. **`注意事项` 与 `相关` 恒在最后两段**。读者读到「注意事项」时应该已经读完全部用法；
   在它后面再挂一段家族话题，等于让最重要的告警不是最后一眼看到的东西。
3. **没有空壳段**。「当前无配置项」这类只为凑齐骨架而存在的段落只消耗目录、不提供信息，
   应当整段不出现；同理，正文与子标题都为空的段落一律算缺陷。
4. **导语恰好一段且 ≤120 可见字**。导语回答「它解决什么问题」，不承担教程与营销；
   计长度时链接只算文字、行内代码按一字计，因此 `<see>`/反引号密集的段落不会被误判。

可选段的**存在**不做要求（有 Options 才写 `配置项`，有非显然运行时语义才写 `实现行为`），
但一旦出现，名称必须精确：`配置项` 允许带配置节路径后缀（`配置项（`Leistd:Lock:Redis`）`）。
家族特有段名称自由——它们本就是各家族独有的话题，穷举反而会变成一张没人维护的白名单。

带 `--self-test`：规则是文本匹配，写错一个边界就会静默不再命中，从那以后闸门永远是绿的。
"""
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..'))
DOC_GLOBS = (
    os.path.join(ROOT, 'framework', 'docs', 'components'),
    os.path.join(ROOT, 'framework', 'docs', 'ddd-struct'),
)
# 索引文件不是组件文档，不套骨架。
EXCLUDED_BASENAMES = {'README.md'}

REQUIRED = ['何时使用', '安装', '使用', '接口参考', '注意事项']
# 可选段的精确名称。`配置项` 允许 `（配置节路径）` 后缀。
OPTIONAL_EXACT = {'注册', '配置', '实现行为', '相关'}
CONFIG_SECTION_RE = re.compile(r'^配置项(（.+）)?$')

INTRO_MAX_VISIBLE = 120
# 只为凑齐骨架而存在的段落。
SHELL_BODY_RE = re.compile(r'当前无|暂无|^无配置项|尚无')


def visible_length(text):
    """链接只算显示文字，行内代码按一字计，`**` 与中文引号不计。"""
    text = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', text)
    text = re.sub(r'`[^`]*`', 'X', text)
    return len(text.replace('**', '').replace('「', '').replace('」', ''))


def iter_docs():
    for root in DOC_GLOBS:
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            if not name.endswith('.md') or name in EXCLUDED_BASENAMES:
                continue
            path = os.path.join(root, name)
            yield os.path.relpath(path, ROOT).replace(os.sep, '/'), path


def parse(lines):
    """返回 (导语行, [(标题, 正文行)])。"""
    heads = [i for i, l in enumerate(lines) if l.startswith('## ')]
    intro_end = heads[0] if heads else len(lines)
    h1 = next((i for i, l in enumerate(lines) if l.startswith('# ')), -1)
    intro = [l for l in lines[h1 + 1:intro_end] if l.strip()]
    sections = []
    for k, i in enumerate(heads):
        end = heads[k + 1] if k + 1 < len(heads) else len(lines)
        sections.append((lines[i][3:].strip(), lines[i + 1:end]))
    return intro, sections


def check(rel, lines, problems):
    intro, sections = parse(lines)
    names = [n for n, _ in sections]

    # 规则 4：导语
    if len(intro) != 1:
        problems.append(f'{rel} 导语应恰好一段（当前 {len(intro)} 段）：只回答「它解决什么问题」')
    intro_len = sum(visible_length(l) for l in intro)
    if intro_len > INTRO_MAX_VISIBLE:
        problems.append(
            f'{rel} 导语 {intro_len} 可见字，超出上限 {INTRO_MAX_VISIBLE}；'
            f'背景与场景归 `## 何时使用`')

    # 规则 1：必选段齐全 + 相对顺序
    positions = []
    for req in REQUIRED:
        hit = [k for k, n in enumerate(names) if n == req or n.startswith(req + '（')]
        if not hit:
            problems.append(f'{rel} 缺必选段 `## {req}`')
        else:
            positions.append(hit[0])
    if positions != sorted(positions):
        problems.append(
            f'{rel} 必选段顺序错误，应为 ' + ' → '.join(REQUIRED) + f'；当前为 {names}')

    # 规则 2：注意事项 / 相关 恒在最后
    if names:
        tail = names[-2:] if names[-1] == '相关' else names[-1:]
        if tail and tail[0] != '注意事项':
            problems.append(
                f'{rel} `## 注意事项` 不在末尾（`## 相关` 之外不得有后续段），当前末段为 {names[-2:]}')

    # 规则 3：空壳段 + 名称精确
    for name, body in sections:
        text = [l for l in body if l.strip()]
        if not text:
            problems.append(f'{rel} `## {name}` 正文与子标题都为空，应整段删除')
        elif len(text) == 1 and SHELL_BODY_RE.search(text[0]):
            problems.append(
                f'{rel} `## {name}` 是空壳段（{text[0].strip()[:36]}…），应整段删除而不是写「当前无」')
        if name in REQUIRED or name in OPTIONAL_EXACT or CONFIG_SECTION_RE.match(name):
            continue
        if name.startswith('配置项'):
            problems.append(
                f'{rel} `## {name}` 命名不规范：无绑定节路径写 `## 配置项`，'
                f'有则写 `## 配置项（`配置节路径`）`')


SELF_TEST_DOCS = [
    # (说明, 文档行, 期望问题数)
    ('齐全且合规', [
        '# 分布式锁', '', '统一的 `ILock` 抽象屏蔽底层实现。', '',
        '## 何时使用', '', '| 场景 | 用法 |', '',
        '## 安装', '', '```bash', 'dotnet add package X', '```', '',
        '## 使用', '', '示例。', '',
        '## 接口参考', '', '| 成员 | 作用 |', '',
        '## 实现行为', '', '说明。', '',
        '## 注意事项', '', '- 一条。', '',
        '## 相关', '', '- [X](./x.md)',
    ], 0),
    ('缺必选段 + 注意事项不在末尾', [
        '# 标题', '', '一句话导语。', '',
        '## 何时使用', '', '正文。', '',
        '## 安装', '', '正文。', '',
        '## 使用', '', '正文。', '',
        '## 注意事项', '', '- 一条。', '',
        '## 超时语义', '', '正文。',
    ], 2),
    ('空壳配置项段', [
        '# 标题', '', '一句话导语。', '',
        '## 何时使用', '', '正文。', '',
        '## 安装', '', '正文。', '',
        '## 使用', '', '正文。', '',
        '## 接口参考', '', '正文。', '',
        '## 配置项', '', '当前无配置项。', '',
        '## 注意事项', '', '- 一条。',
    ], 1),
    ('导语两段且过长', [
        '# 标题', '',
        '这是一段很长的导语，' * 14, '',
        '这是第二段。', '',
        '## 何时使用', '', '正文。', '',
        '## 安装', '', '正文。', '',
        '## 使用', '', '正文。', '',
        '## 接口参考', '', '正文。', '',
        '## 注意事项', '', '- 一条。',
    ], 2),
    ('配置项命名不规范', [
        '# 标题', '', '一句话导语。', '',
        '## 何时使用', '', '正文。', '',
        '## 安装', '', '正文。', '',
        '## 使用', '', '正文。', '',
        '## 接口参考', '', '正文。', '',
        '## 配置项 / Options', '', '| 键 | 默认 |', '',
        '## 注意事项', '', '- 一条。',
    ], 1),
]


def self_test():
    failures = []
    for label, lines, expected in SELF_TEST_DOCS:
        problems = []
        check('self-test.md', lines, problems)
        if len(problems) != expected:
            failures.append(
                f'  「{label}」命中 {len(problems)} 项，期望 {expected}：{problems}')

    if visible_length('[文字](./a.md) 与 `IClock`') != len('文字 与 X'):
        failures.append('  可见长度计算未正确折叠链接与行内代码')
    if not CONFIG_SECTION_RE.match('配置项（`Leistd:Lock:Redis`）'):
        failures.append('  带配置节路径的 `配置项` 应被接受')
    if CONFIG_SECTION_RE.match('配置项 / Options'):
        failures.append('  `配置项 / Options` 应被拒绝')

    if failures:
        print('❌ 规则自检失败：')
        print('\n'.join(failures))
        return 1
    print(f'✅ 规则自检通过（{len(SELF_TEST_DOCS) + 3} 例）。')
    return 0


def main():
    if '--self-test' in sys.argv:
        return self_test()

    problems = []
    count = 0
    for rel, path in iter_docs():
        count += 1
        with open(path, encoding='utf-8') as fh:
            check(rel, fh.read().splitlines(), problems)

    if problems:
        print(f'❌ 组件文档骨架检查失败（共 {len(problems)} 项，已扫描 {count} 篇）：')
        for p in problems:
            print(f'  - {p}')
        print('\n骨架见 docs/framework/development-guide.md §4.3「组件文档骨架」。')
        return 1

    print(f'✅ 组件文档骨架检查通过（{count} 篇：必选段齐全有序，'
          f'注意事项/相关在末尾，无空壳段，导语均为一段且 ≤{INTRO_MAX_VISIBLE} 可见字）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
