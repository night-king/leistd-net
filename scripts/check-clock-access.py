#!/usr/bin/env python3
"""当前时间只能从可替换的时间源取。

后端规范 §3.2.1 禁止就地读取当前时间：那样做隐藏依赖，而且**症状是静默的**——代码照常
工作，只是那段逻辑再也没法用假时钟测。过期、节流、有效期这类判断一旦这么写，
「它到底有没有生效」就只能靠真的等一段时间来验证，于是通常就不验证了。

**放行 `TimeProvider.System` 作为默认时间源。** `private readonly TimeProvider _tp =
timeProvider ?? TimeProvider.System;` 是 .NET 官方的可测时钟形态：接缝在构造签名上，
测试用 `FakeTimeProvider` 覆盖它。本脚本只禁「就地读出当前时刻」的写法。

**豁免绑定到具体表达式与命中次数，而不是整个文件。** 早先的版本命中豁免路径就
`continue`，于是那个文件其余部分再也不被扫描——同文件后来新增的真违规看不见，
而「豁免已过期」也永远不会触发，因为只要文件存在就算被用到了。那是闸门自身的 false-green。
现在：命中的表达式必须与登记的片段完全一致、次数也要对得上；文件继续扫完，
只抑制登记的那几处；数目或内容变了都报错。

自测：`python3 scripts/check-clock-access.py --self-test`
"""
import io, ntpath, os, re, sys, tempfile

ROOT = os.path.join(os.path.dirname(__file__), '..')

# 禁区：所有会被注入时钟的生产代码
ZONES = [
    'framework/components',
    'framework/ddd-struct',
    'template/backend/src',
]

PATTERNS = [
    (re.compile(r'\bDateTime\.(?:Now|UtcNow)\b'), 'DateTime.Now/UtcNow'),
    (re.compile(r'\bDateTimeOffset\.(?:Now|UtcNow)\b'), 'DateTimeOffset.Now/UtcNow'),
    # 只禁「就地取值」，不禁把它当默认时间源传进去
    (re.compile(r'\bTimeProvider\.System\s*\.\s*GetUtcNow\(\)'), 'TimeProvider.System.GetUtcNow()'),
]

# 豁免：路径 → (被豁免的代码片段, 期望出现次数, 理由)。
# 加新条目前先问「这是不是说明规则该改」。
WAIVERS = {
    'framework/components/event-bus/Leistd.EventBus.Core/Events/BaseEvent.cs': (
        'TimeProvider.System.GetUtcNow().UtcDateTime',
        1,
        '事件由领域代码 new 出来、拿不到容器；可注入的时间源在另一个构造重载上',
    ),
}

SKIP_DIR = ('obj', 'bin', 'node_modules')


def rel_key(path, root, pathmod=os.path):
    """相对路径统一成 `/` 分隔，与 WAIVERS 的键同形。

    Windows 上 relpath 给出 `\\` 分隔，不统一的话豁免永远对不上键：登记的那处被报成违规，
    同时豁免被判为"文件不存在"。`pathmod` 只供自测注入 `ntpath`，在任何平台上复现这一点。
    """
    return pathmod.relpath(path, root).replace(pathmod.sep, '/')


def scan_file(path, rel, waiver):
    """返回 (违规列表, 豁免片段命中次数)。

    按**精确匹配区间**计数，而不是"这一行里有没有那段文本"：同一行出现两次禁用写法时
    两次都要计入；一行里既有登记的豁免表达式、又有另一种违规时，只抑制前者。
    """
    findings, waived_hits = [], 0
    waived_snippet = waiver[0] if waiver else None
    text = io.open(path, encoding='utf-8', errors='replace').read()
    for i, line in enumerate(text.split('\n'), 1):
        code = line.split('//', 1)[0]

        # 先算出这一行里豁免片段占据的所有区间
        waived_spans = []
        if waived_snippet:
            start = code.find(waived_snippet)
            while start != -1:
                waived_spans.append((start, start + len(waived_snippet)))
                start = code.find(waived_snippet, start + 1)

        for pattern, label in PATTERNS:
            for match in pattern.finditer(code):
                inside_waiver = any(
                    lo <= match.start() and match.end() <= hi for lo, hi in waived_spans)
                if inside_waiver:
                    waived_hits += 1
                    continue
                findings.append((rel, i, label, line.strip()))
    return findings, waived_hits


def run(zones, waivers, root):
    findings, scanned = [], 0
    waiver_hits = {rel: 0 for rel in waivers}
    seen_files = set()

    for zone in zones:
        base = os.path.join(root, zone)
        if not os.path.isdir(base):
            # 目录改名后静默扫 0 个文件、然后"通过"，是这类闸门最容易出的失效
            return 1, [f'⚠️  禁区路径不存在，可能已被重命名：{zone}']
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d not in SKIP_DIR]
            for name in fn:
                if not name.endswith('.cs'):
                    continue
                path = os.path.join(dp, name)
                rel = rel_key(path, root)
                scanned += 1
                seen_files.add(rel)
                found, hits = scan_file(path, rel, waivers.get(rel))
                findings.extend(found)
                if rel in waivers:
                    waiver_hits[rel] += hits

    messages = []
    stale = []
    for rel, (snippet, expected, _reason) in waivers.items():
        if rel not in seen_files:
            stale.append(f'❌ 豁免条目指向的文件不存在：{rel}')
        elif waiver_hits[rel] != expected:
            stale.append(
                f'❌ 豁免条目已不匹配：{rel}\n'
                f'   期望 {expected} 处 `{snippet}`，实际 {waiver_hits[rel]} 处')
    if stale:
        messages.extend(stale)
        messages.append('\n过期或数目不符的豁免会掩盖真违规——原违规已消失就删掉它，'
                        '数目变了就说明同一文件新增了违规。')
        return 1, messages

    if findings:
        for rel, line, label, text in findings:
            messages.append(f'❌ {rel}:{line} 直接读取当前时间（{label}）')
            messages.append(f'   {text}')
        messages.append(
            f'\n共 {len(findings)} 处。改为注入 IClock（模板）或 TimeProvider'
            f'（框架，构造参数可选、默认 TimeProvider.System）。')
        return 1, messages

    return 0, [f'✅ 时间源检查通过（{scanned} 个文件，{len(zones)} 个禁区，'
               f'{len(waivers)} 条豁免且全部命中）。']


def self_test():
    """四种形态：合法豁免、豁免失效、同文件新增第二处违规、普通违规。"""
    cases = [
        ('合法豁免：登记的那处被抑制，其余无违规',
         {'a.cs': 'var x = TimeProvider.System.GetUtcNow().UtcDateTime;\n'},
         {'zone/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 0),
        ('豁免失效：原违规已被改掉，豁免应报错',
         {'a.cs': 'var x = clock.Now;\n'},
         {'zone/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 1),
        ('同文件新增第二处违规：不能因为有豁免就整篇放行',
         {'a.cs': 'var x = TimeProvider.System.GetUtcNow().UtcDateTime;\nvar y = DateTime.UtcNow;\n'},
         {'zone/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 1),
        ('普通违规：无豁免时直接报错',
         {'a.cs': 'var y = DateTimeOffset.UtcNow;\n'}, {}, 1),
        ('注释里的写法不算违规',
         {'a.cs': '// 不要用 DateTime.UtcNow\n'}, {}, 0),
        ('同一行出现两次被禁写法：两次都要计入，不能只算一次豁免',
         {'a.cs': 'var d = TimeProvider.System.GetUtcNow().UtcDateTime - '
                  'TimeProvider.System.GetUtcNow().UtcDateTime;\n'},
         {'zone/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 1),
        ('豁免表达式与另一种违规同在一行：另一种必须仍被报出',
         {'a.cs': 'var d = TimeProvider.System.GetUtcNow().UtcDateTime - DateTime.UtcNow;\n'},
         {'zone/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 1),
        ('多级目录下的豁免：键按 `/` 分隔匹配',
         {'sub/a.cs': 'var x = TimeProvider.System.GetUtcNow().UtcDateTime;\n'},
         {'zone/sub/a.cs': ('TimeProvider.System.GetUtcNow().UtcDateTime', 1, 'r')}, 0),
    ]
    failures = 0
    # Windows 分隔符：用 ntpath 模拟，不依赖当前平台
    win_rel = rel_key('C:\\repo\\zone\\sub\\a.cs', 'C:\\repo', ntpath)
    ok = win_rel == 'zone/sub/a.cs'
    print(f"  {'✅' if ok else '❌'} 反斜杠路径统一成 `/`（期望 zone/sub/a.cs，实际 {win_rel}）")
    if not ok:
        failures += 1
    for name, files, waivers, expected in cases:
        with tempfile.TemporaryDirectory() as tmp:
            zone = os.path.join(tmp, 'zone')
            os.makedirs(zone)
            for fname, content in files.items():
                target = os.path.join(zone, *fname.split('/'))
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with io.open(target, 'w', encoding='utf-8') as f:
                    f.write(content)
            code, msgs = run(['zone'], waivers, tmp)
            ok = code == expected
            print(f"  {'✅' if ok else '❌'} {name}（期望退出 {expected}，实际 {code}）")
            if not ok:
                failures += 1
                print('     ' + '\n     '.join(msgs))
    if failures:
        print(f'\n❌ 自测失败 {failures} 例。')
        return 1
    print(f'\n✅ 自测通过（{len(cases) + 1} 例）。')
    return 0


def main():
    if '--self-test' in sys.argv:
        return self_test()
    code, messages = run(ZONES, WAIVERS, ROOT)
    print('\n'.join(messages))
    return code


if __name__ == '__main__':
    sys.exit(main())
