#!/usr/bin/env python3
"""测试名只用英文；前端用例标题小写开头。

前后端规范规定：前端 `describe` / `it` / `test` 的标题、后端 `[Fact]` / `[Theory]` 的方法名与
`DisplayName` 都用英文句子；中文只出现在注释与测试数据里。测试名会出现在测试报告、
CI 日志和 IDE 的测试树里，混着两种语言时既难检索，也难与失败信息对照。

**只查名字，不查数据与注释。** 扫描前先把注释和字符串（含模板字符串）的内容抹成空白，
再在剩下的代码里找调用点与方法声明；标题本身按原文取出来判定。于是：

- 注释里写的 `it('中文')`、字符串里的 `"describe(中文)"` 都不是调用点；
- 用例体里的测试数据不受影响；断言消息与测试里抛出的诊断不归本脚本，由 `template/scripts/check-test-diagnostics.py` 检查；
- `it.each([...])('标题')`、`it.skipIf(cond)('标题')` 这类链式写法也能找到标题；
- 模板字符串标题只看字面部分，`${...}` 插值里的内容是数据。

前端 `it` / `test` 的标题另须**小写开头**（testing.md §3：标题是一句接在主语后面的行为描述，
如 `keeps the dialog open when saving fails`）。首词是专有名词（`PROPER_NOUNS`）或全大写缩写
（`URL`、`OIDC`）时放行；标题不以字母开头（`%s`、`/api/...`、数字）不受约束。`describe` 的标题按惯例
写被测单元名（`AuthService`、`RoleTable`），不在此列。

判定的字符范围：中日韩统一表意文字（含扩展 A 与兼容区）、假名、谚文，以及
中日韩标点（U+3000–U+303F）和全角形式（U+FF00–U+FFEF）——全角括号、逗号单独出现也算。

自测：`python3 scripts/check-test-names.py --self-test`
"""
from __future__ import annotations

import os
import re
import sys
import tempfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..'))

# (根目录, 文件后缀)。目录改名后静默扫 0 个文件然后"通过"，是这类闸门最容易出的失效，
# 所以每个根都必须存在且至少扫到一个文件。
ZONES = [
    ('template/frontend', '.spec.ts'),
    ('framework/tests', '.cs'),
    ('template/backend/tests', '.cs'),
]

SKIP_DIRS = {'node_modules', 'bin', 'obj', 'dist', '.angular', 'coverage'}

CJK = re.compile(
    '[　-〿぀-ヿ㐀-䶿一-鿿'
    '가-힯豈-﫿＀-￯]')

# Vitest 的链式修饰：带参数的那几个（each / for / runIf / skipIf）后面先跟一组实参，标题在下一组调用里
TS_CALL = re.compile(
    r'(?<![\w.$])(describe|it|test)'
    r'((?:\s*\.\s*(?:each|for|skip|only|todo|concurrent|sequential|fails|runIf|skipIf|shuffle))*)\s*\(')
TS_CHAIN_WITH_ARGS = re.compile(r'\.\s*(?:each|for|runIf|skipIf)\b')

# 用例标题首词可以大写的专有名词：产品、协议与库名按原样书写。缩写（全大写）另由 ACRONYM 放行。
PROPER_NOUNS = {
    'Angular', 'Chromium', 'GitHub', 'JavaScript', 'Mapster', 'OAuth', 'OpenID', 'OpenIddict',
    'Playwright', 'PostgreSQL', 'Redis', 'SignalR', 'Spartan', 'Transloco', 'TypeScript', 'Vitest',
}
ACRONYM = re.compile(r'[A-Z][A-Z0-9]+s?')
TS_CASE_CHECKED = {'it', 'test'}
FIRST_WORD = re.compile(r'[A-Za-z][A-Za-z0-9]*')

CS_TEST_ATTR = re.compile(r'\[\s*(?:Xunit\s*\.\s*)?(?:Fact|Theory)\b')
CS_DISPLAY_NAME = re.compile(r'\bDisplayName\s*=\s*')


# ---------------------------------------------------------------------------
# 词法扫描：返回 (masked, literals)。
#   masked   与原文等长，注释、字符串、模板字符串、正则字面量的内容换成空格（保留换行，行号不变），
#            于是在它上面找调用点与声明，不会被注释或数据里的同名文字骗到；
#   literals 每个字符串字面量起始位置（引号处）→ 解码后的字面文本；模板字符串只留字面部分，
#            插值是数据。标题直接从这里取，不再从原文二次解析——二次解析与扫描器对边界的
#            判断一旦不一致（正则里的引号、嵌套模板），就会漏判或误报。

def _blank(chars: list[str], start: int, end: int) -> None:
    for k in range(start, end):
        if chars[k] != '\n':
            chars[k] = ' '


# 这些记号之后出现的 `/` 是正则字面量的开头，而不是除号
_REGEX_PRECEDERS = set('(,=:[!&|?{};+-*%<>~^')
_REGEX_KEYWORDS = {'return', 'typeof', 'case', 'do', 'else', 'in', 'of', 'new', 'delete',
                   'void', 'throw', 'yield', 'await'}


class _TsLexer:
    def __init__(self, src: str):
        self.src, self.n = src, len(src)
        self.chars = list(src)
        self.literals: dict[int, str] = {}

    def run(self) -> tuple[str, dict[int, str]]:
        self._code(0, stop_at_brace=False)
        return ''.join(self.chars), self.literals

    def _prev_allows_regex(self, i: int) -> bool:
        j = i - 1
        while j >= 0 and self.chars[j].isspace():
            j -= 1
        if j < 0:
            return True
        if self.chars[j] in _REGEX_PRECEDERS:
            return True
        k = j
        while k >= 0 and (self.chars[k].isalnum() or self.chars[k] in '_$'):
            k -= 1
        return self.src[k + 1:j + 1] in _REGEX_KEYWORDS

    def _code(self, i: int, stop_at_brace: bool) -> int:
        """扫描代码直到结尾；stop_at_brace 时扫到与之配对的 `}` 为止，返回其后的位置。"""
        src, n, depth = self.src, self.n, 0
        while i < n:
            c = src[i]
            if src.startswith('//', i):
                j = src.find('\n', i)
                j = n if j == -1 else j
                _blank(self.chars, i, j)
                i = j
            elif src.startswith('/*', i):
                j = src.find('*/', i + 2)
                j = n if j == -1 else j + 2
                _blank(self.chars, i, j)
                i = j
            elif c in '\'"':
                i = self._quoted(i)
            elif c == '`':
                i = self._template(i)
            elif c == '/' and self._prev_allows_regex(i):
                i = self._regex(i)
            else:
                if stop_at_brace:
                    if c == '{':
                        depth += 1
                    elif c == '}':
                        if depth == 0:
                            return i + 1
                        depth -= 1
                i += 1
        return i

    def _quoted(self, i: int) -> int:
        src, quote, out, j = self.src, self.src[i], [], i + 1
        while j < self.n and src[j] != quote and src[j] != '\n':
            if src[j] == '\\':
                out.append(src[j + 1:j + 2])
                j += 2
                continue
            out.append(src[j])
            j += 1
        self.literals[i] = ''.join(out)
        _blank(self.chars, i + 1, min(j, self.n))
        return j + 1

    def _template(self, i: int) -> int:
        src, out, j = self.src, [], i + 1
        while j < self.n and src[j] != '`':
            if src[j] == '\\':
                out.append(src[j + 1:j + 2])
                j += 2
            elif src.startswith('${', j):
                # 插值里是代码，可以再嵌字符串与模板；扫到配对的 `}`，整段按数据抹掉
                end = self._code(j + 2, stop_at_brace=True)
                _blank(self.chars, j, end)
                j = end
            else:
                out.append(src[j])
                j += 1
        self.literals[i] = ''.join(out)
        _blank(self.chars, i + 1, min(j, self.n))
        return j + 1

    def _regex(self, i: int) -> int:
        src, j, in_class = self.src, i + 1, False
        while j < self.n and src[j] != '\n':
            ch = src[j]
            if ch == '\\':
                j += 2
                continue
            if ch == '[':
                in_class = True
            elif ch == ']':
                in_class = False
            elif ch == '/' and not in_class:
                break
            j += 1
        _blank(self.chars, i + 1, min(j, self.n))
        return j + 1


def mask_ts(src: str) -> tuple[str, dict[int, str]]:
    return _TsLexer(src).run()


def mask_cs(src: str) -> tuple[str, dict[int, str]]:
    """C#：预处理指令整行、注释、普通/逐字/插值/原始字符串、字符字面量。"""
    chars, literals = list(src), {}
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        line_start = src.rfind('\n', 0, i) + 1
        if c == '#' and not src[line_start:i].strip():
            # 预处理指令（模板条件 #if / #endif 夹在特性与方法之间）整行抹掉
            j = src.find('\n', i)
            j = n if j == -1 else j
            _blank(chars, i, j)
            i = j
        elif src.startswith('//', i):
            j = src.find('\n', i)
            j = n if j == -1 else j
            _blank(chars, i, j)
            i = j
        elif src.startswith('/*', i):
            j = src.find('*/', i + 2)
            j = n if j == -1 else j + 2
            _blank(chars, i, j)
            i = j
        elif src.startswith('"""', i):
            # 原始字符串：以至少三个引号开头，以同样数目的引号结尾，内容原样
            q = 3
            while i + q < n and src[i + q] == '"':
                q += 1
            j = src.find('"' * q, i + q)
            j = n if j == -1 else j
            literals[i] = src[i + q:j]
            _blank(chars, i + q, j)
            i = j + q
        elif c == '"':
            prefix = src[max(0, i - 2):i]
            verbatim = prefix.endswith('@') or prefix in ('@$', '$@')
            out, j = [], i + 1
            while j < n:
                if verbatim and src.startswith('""', j):
                    out.append('"')
                    j += 2
                    continue
                if not verbatim and src[j] == '\\':
                    out.append(src[j + 1:j + 2])
                    j += 2
                    continue
                if src[j] == '"':
                    break
                out.append(src[j])
                j += 1
            literals[i] = ''.join(out)
            _blank(chars, i + 1, min(j, n))
            i = j + 1
        elif c == "'":
            # 字符字面量：'x'、'\''、'\u4e2d'
            j = i + 1
            while j < n and src[j] != "'" and src[j] != '\n':
                j += 2 if src[j] == '\\' else 1
            _blank(chars, i + 1, min(j, n))
            i = j + 1
        else:
            i += 1
    return ''.join(chars), literals


# ---------------------------------------------------------------------------

def _skip_ws(text: str, i: int) -> int:
    while i < len(text) and text[i].isspace():
        i += 1
    return i


def _skip_group(masked: str, i: int, open_ch: str, close_ch: str) -> int:
    """masked[i] == open_ch；返回配对的 close_ch 之后的位置。字符串已被抹掉，括号计数可靠。"""
    depth = 0
    while i < len(masked):
        if masked[i] == open_ch:
            depth += 1
        elif masked[i] == close_ch:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return i


def _line_of(src: str, pos: int) -> int:
    return src.count('\n', 0, pos) + 1


def _ts_titles(src: str) -> list[tuple[str, int, str]]:
    """前端用例调用点：(describe/it/test, 行号, 标题)。标题只取字符串字面量的文本。"""
    masked, literals = mask_ts(src)
    found = []
    for m in TS_CALL.finditer(masked):
        pos = m.end() - 1                       # 指向 '('
        if TS_CHAIN_WITH_ARGS.search(m.group(2)):
            pos = _skip_ws(masked, _skip_group(masked, pos, '(', ')'))
            if pos >= len(masked) or masked[pos] != '(':
                continue
        start = _skip_ws(masked, pos + 1)
        title = literals.get(start)
        if title is not None:
            found.append((m.group(1), _line_of(src, start), title))
    return found


def scan_ts(src: str) -> list[tuple[int, str]]:
    return [(line, title) for _, line, title in _ts_titles(src) if CJK.search(title)]


def title_starts_uppercase(title: str) -> bool:
    """标题以大写字母开头，且首词既不是专有名词也不是缩写。"""
    if not title[:1].isupper() or not title[:1].isascii():
        return False
    word = FIRST_WORD.match(title)
    if word is None:
        return False
    return word.group(0) not in PROPER_NOUNS and not ACRONYM.fullmatch(word.group(0))


def scan_ts_case(src: str) -> list[tuple[int, str]]:
    return [(line, title) for kind, line, title in _ts_titles(src)
            if kind in TS_CASE_CHECKED and title_starts_uppercase(title)]


def scan_cs(src: str) -> list[tuple[int, str]]:
    masked, literals = mask_cs(src)
    found = []
    for m in CS_TEST_ATTR.finditer(masked):
        attr_end = _skip_group(masked, m.start(), '[', ']')
        # DisplayName 只认测试特性自己的实参；测试数据里同名的属性赋值（租户显示名等）是数据
        for d in CS_DISPLAY_NAME.finditer(masked, m.start(), attr_end):
            start = d.end()
            while start < len(src) and src[start] in '@$':
                start += 1
            title = literals.get(start)
            if title is not None and CJK.search(title):
                found.append((_line_of(src, start), f'DisplayName "{title}"'))
        # 跳过紧随其后的其余特性（[InlineData(...)] 等），再取声明里 '(' 之前的最后一个标识符
        pos = _skip_ws(masked, attr_end)
        while pos < len(masked) and masked[pos] == '[':
            pos = _skip_ws(masked, _skip_group(masked, pos, '[', ']'))
        paren = masked.find('(', pos)
        if paren == -1:
            continue
        names = re.findall(r'[^\W\d]\w*', masked[pos:paren])
        if names and CJK.search(names[-1]):
            found.append((_line_of(src, pos + masked[pos:paren].rfind(names[-1])), names[-1]))
    return sorted(found)


# ---------------------------------------------------------------------------

def run(zones, root):
    findings, messages, scanned_total = [], [], 0
    for zone, suffix in zones:
        base = os.path.join(root, zone)
        if not os.path.isdir(base):
            return 1, [f'⚠️  扫描路径不存在，可能已被重命名：{zone}']
        scanned = 0
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d not in SKIP_DIRS]
            for name in sorted(fn):
                if not name.endswith(suffix):
                    continue
                path = os.path.join(dp, name)
                rel = os.path.relpath(path, root).replace(os.sep, '/')
                src = open(path, encoding='utf-8', errors='replace').read()
                scanned += 1
                scan = scan_ts if suffix == '.spec.ts' else scan_cs
                findings.extend((rel, line, f'测试名含中日韩字符：{title}') for line, title in scan(src))
                if suffix == '.spec.ts':
                    findings.extend((rel, line, f'用例标题要小写开头：{title}') for line, title in scan_ts_case(src))
        if scanned == 0:
            return 1, [f'⚠️  {zone} 下没有扫到任何 *{suffix} 文件，判据可能已失效']
        scanned_total += scanned

    if findings:
        for rel, line, title in findings:
            messages.append(f'❌ {rel}:{line} {title}')
        messages.append(
            f'\n共 {len(findings)} 处。测试名改为小写开头的英文句子；"为什么"写进上方注释，中文数据留在用例体里；'
            f'首词确是专有名词时加进 PROPER_NOUNS。')
        return 1, messages
    return 0, [f'✅ 测试名检查通过（{scanned_total} 个文件，{len(zones)} 个扫描路径）。']


SELF_TEST_CASES = [
    # (说明, 文件名, 内容, 期望命中数)
    ('中文 it 标题', 'a.spec.ts', "it('返回 401', () => {});\n", 1),
    ('中文 describe 标题', 'a.spec.ts', "describe('登录', () => {});\n", 1),
    ('英文标题放行', 'a.spec.ts', "it('returns 401', () => {});\n", 0),
    ('只有全角标点也算', 'a.spec.ts', "it('returns 401（anonymous）', () => {});\n", 1),
    ('标题跨行', 'a.spec.ts', "it(\n  '返回 401',\n  () => {},\n);\n", 1),
    ('注释里的伪调用不算', 'a.spec.ts', "// it('返回 401')\n/* describe('登录') */\n", 0),
    ('字符串里的伪调用不算', 'a.spec.ts', "const s = \"it('返回')\";\n", 0),
    ('用例体里的中文数据与断言文本不算', 'a.spec.ts',
     "it('shows the name', () => { expect(name).toBe('株式会社'); });\n", 0),
    ('成员调用 foo.it() 不是用例', 'a.spec.ts', "helper.it('中文');\n", 0),
    ('it.each 的数据不算，标题算', 'a.spec.ts',
     "it.each(['中文'])('renders %s', () => {});\nit.each([1])('渲染 %s', () => {});\n", 1),
    ('it.skip / describe.only 等链式修饰', 'a.spec.ts',
     "it.skip('跳过', () => {});\ndescribe.only('只跑', () => {});\n", 2),
    ('模板字符串：插值是数据', 'a.spec.ts',
     "it(`skips ${'中文'} header`, () => {});\nit(`跳过 ${url}`, () => {});\n", 1),
    ('标题里的转义引号', 'a.spec.ts', "it('doesn\\'t 跳过', () => {});\n", 1),
    ('正则字面量里的引号不打乱后续扫描', 'a.spec.ts',
     "const quote = /'/;\nit(\"中文\", () => {});\n", 1),
    ('正则字面量里的伪调用不算', 'a.spec.ts', "const pattern = /it(\"中文\")/;\n", 0),
    ('正则字符类里的斜杠', 'a.spec.ts', "const p = /[/'\"]/g;\nit('返回', () => {});\n", 1),
    ('除号不当成正则', 'a.spec.ts', "const r = a / b / c;\nit('返回', () => {});\n", 1),
    ('嵌套模板字符串：插值里的伪调用是数据', 'a.spec.ts',
     "it(`renders ${`it(\"中文\")`}`, () => {});\n", 0),
    ('嵌套模板之后的中文标题仍能找到', 'a.spec.ts',
     "const s = `a ${`b ${c}`} d`;\nit('返回', () => {});\n", 1),
    ('后端中文方法名', 'A.cs', "[Fact]\npublic void 返回401() { }\n", 1),
    ('后端英文方法名放行', 'A.cs',
     "[Theory]\n[InlineData(\"中文\")]\npublic async Task Rejects_header(string v) { }\n", 0),
    ('后端多个特性之后的方法名', 'A.cs',
     "[Theory]\n[InlineData(\"a\")]\n[Trait(\"k\", \"v\")]\npublic void 拒绝(string v) { }\n", 1),
    ('后端 DisplayName', 'A.cs', "[Fact(DisplayName = \"拒绝\")]\npublic void Rejects() { }\n", 1),
    ('后端注释与字符串里的中文不算', 'A.cs',
     "// [Fact] public void 中文() {}\n[Fact]\npublic void Tenant_name() { var n = \"Acme 株式会社\"; }\n", 0),
    ('后端测试数据里的 DisplayName 属性不算', 'A.cs',
     "[Fact]\npublic void Renames() { var t = new Tenant { DisplayName = \"株式会社\" }; }\n", 0),
    ('后端特性与方法之间夹着模板条件', 'A.cs',
     "[Theory]\n#if (LocalIdentity)\n[InlineData(\"a\")]\n#endif\npublic void 拒绝(string v) { }\n", 1),
    ('后端 DisplayName 用原始字符串', 'A.cs',
     '[Fact(DisplayName = """中文""")]\npublic void Rejects() { }\n', 1),
    ('后端 DisplayName 用含转义引号的逐字字符串', 'A.cs',
     '[Fact(DisplayName = @"a ""quoted"" 中文")]\npublic void Rejects() { }\n', 1),
    ('后端逐字字符串', 'A.cs',
     "[Fact]\npublic void Reads() { var s = @\"he said \"\"中文\"\"\"; }\n", 0),
]


# (说明, 内容, 期望命中数)：只看 it / test 标题的大小写
CASE_SELF_TEST_CASES = [
    ('大写开头的 it 标题', "it('Returns 401', () => {});\n", 1),
    ('大写开头的 test 标题', "test('Shows the dialog', () => {});\n", 1),
    ('链式 it.each 的标题', "it.each([1])('Renders %s', () => {});\n", 1),
    ('小写开头放行', "it('returns 401', () => {});\n", 0),
    ('专有名词开头放行', "it('SignalR reconnects after a drop', () => {});\n", 0),
    ('缩写开头放行', "it('URL keeps the query', () => {});\nit('OIDC callbacks finish', () => {});\n", 0),
    ('非字母开头放行', "it('%s is rejected', () => {});\nit('/api/v1 prefix', () => {});\n", 0),
    ('describe 写被测单元名放行', "describe('AuthService', () => {});\n", 0),
    ('注释与字符串里的大写标题不算', "// it('Returns')\nconst s = \"it('Returns')\";\n", 0),
]


def self_test():
    failures = 0
    for name, content, expected in CASE_SELF_TEST_CASES:
        got = len(scan_ts_case(content))
        ok = got == expected
        print(f"  {'✅' if ok else '❌'} 小写开头：{name}（期望 {expected} 处，实际 {got} 处）")
        failures += 0 if ok else 1
    for name, fname, content, expected in SELF_TEST_CASES:
        scan = scan_ts if fname.endswith('.spec.ts') else scan_cs
        got = len(scan(content))
        ok = got == expected
        print(f"  {'✅' if ok else '❌'} {name}（期望 {expected} 处，实际 {got} 处）")
        failures += 0 if ok else 1

    # 路径失效：扫描根不存在、或存在但一个文件都没扫到，都必须失败
    with tempfile.TemporaryDirectory() as tmp:
        missing, _ = run([('nope', '.spec.ts')], tmp)
        os.makedirs(os.path.join(tmp, 'empty'))
        empty, _ = run([('empty', '.spec.ts')], tmp)
        for label, code in (('扫描路径不存在时失败', missing), ('扫描路径下没有文件时失败', empty)):
            ok = code == 1
            print(f"  {'✅' if ok else '❌'} {label}（期望退出 1，实际 {code}）")
            failures += 0 if ok else 1

    total = len(SELF_TEST_CASES) + len(CASE_SELF_TEST_CASES) + 2
    if failures:
        print(f'\n❌ 自测失败 {failures} 例。')
        return 1
    print(f'\n✅ 自测通过（{total} 例）。')
    return 0


def main():
    if '--self-test' in sys.argv:
        return self_test()
    code, messages = run(ZONES, ROOT)
    print('\n'.join(messages))
    return code


if __name__ == '__main__':
    sys.exit(main())
