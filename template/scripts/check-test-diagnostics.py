#!/usr/bin/env python3
"""测试代码里的开发者诊断消息不含中日韩字符。

断言失败消息和测试里抛出的异常消息会出现在测试报告、CI 日志和 IDE 里，规范要求用英文
（`docs/standards/testing.md` §2.2）；被断言的本地化期望值、测试数据、用例标题与注释不在此列。

判据：只看直接写出的消息参数里的字符串字面量（含插值字符串的字面部分、原始/逐字字符串与
`\\u` 转义），插值表达式里的字符串是数据。消息参数按调用签名定位，不机械取第几个实参：
  - TypeScript：`expect(x, msg)`、`expect.soft(x, msg)`、`expect.poll(fn, { message })`、
    `expect.unreachable(msg)`；`assert(x, msg)` 及 `node:assert` 各函数的 message 参数；
    `throw [new] XxxError(msg)`（`AggregateError` 取第二个参数）。
  - C#：xUnit 的 `Assert.True/False(condition, userMessage)`、`Assert.Fail(message)`；
    `throw new XxxException(...)` 只看 message 参数——`ArgumentNullException` 等的 paramName、
    `BusinessException` 的错误码、具名的 `paramName:` / `innerException:` 都是数据。
实参边界按括号配对切分；紧跟标识符、能配对到 `>(` 的尖括号按泛型类型实参处理，其余 `<` 视为比较。
不做类型推断与间接数据流分析：先赋给变量再传入的消息、自定义断言辅助函数里的消息不在判据内，
由评审负责。只检查是否含中日韩字符，不证明其余文字就是英文。

扫描范围：
  - 默认（项目模式）：脚本上一级目录为项目根，扫 `backend/tests` 下的 C# 与 TS/JS 测试源码，
    以及 `frontend` 下的 `*.spec.ts` / `*.testing.ts`；`backend/tests` 必须存在，没有前端的
    项目不扫前端；`e2e/`、`frontend/e2e/` 仅存在时扫描。任何被扫描的根一个文件都没有即失败，
    防止目录改名后静默通过。
  - `--repository`：在本模板的源仓库中，另扫框架测试 `framework/tests`，模板前端也成为必需。

用法：`python3 scripts/check-test-diagnostics.py`；判据自检：`python3 scripts/check-test-diagnostics.py --self-test`。
"""
from __future__ import annotations

from pathlib import Path
import re
import sys
import tempfile

# 脚本所在目录的上一级就是被检查的项目根：生成项目里是项目根，源仓库里是 template/。
ROOT = Path(__file__).resolve().parents[1]

# 中日韩统一表意文字（含扩展 A 与兼容区）、假名、谚文、中日韩标点与全角形式。
CJK = re.compile('[\u3000-\u303f\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af\uf900-\ufaff\uff00-\uffef]')
SKIP_DIRS = {'node_modules', 'bin', 'obj', 'dist', '.angular', 'coverage', '.cache', '.git'}
TS_SUFFIXES = ('.ts', '.mts', '.cts', '.js', '.mjs', '.cjs')


def blank(chars: list[str], start: int, end: int) -> None:
    for k in range(start, min(end, len(chars))):
        if chars[k] != '\n':
            chars[k] = ' '


# ---------------------------------------------------------------------------
# 词法：返回 (masked, literals)。masked 与原文等长，注释与字符串内容换成空格（保留换行），
# 在它上面找调用点与实参边界不会被注释或数据骗到；literals 为 (起点, 终点, 字面文本, 是否解析转义)，
# 插值表达式里的字符串另记一条，范围落在外层字面量之内。

class TsLexer:
    REGEX_PRECEDERS = set('(,=:[!&|?{};+-*%<>~^')
    REGEX_KEYWORDS = {'return', 'typeof', 'case', 'do', 'else', 'in', 'of', 'new', 'delete',
                      'void', 'throw', 'yield', 'await'}

    def __init__(self, src: str):
        self.src, self.n, self.chars, self.literals = src, len(src), list(src), []

    def run(self):
        self.code(0, False)
        return ''.join(self.chars), self.literals

    def regex_allowed(self, i: int) -> bool:
        j = i - 1
        while j >= 0 and self.chars[j].isspace():
            j -= 1
        if j < 0 or self.chars[j] in self.REGEX_PRECEDERS:
            return True
        k = j
        while k >= 0 and (self.chars[k].isalnum() or self.chars[k] in '_$'):
            k -= 1
        return self.src[k + 1:j + 1] in self.REGEX_KEYWORDS

    def code(self, i: int, stop_at_brace: bool) -> int:
        src, n, depth = self.src, self.n, 0
        while i < n:
            c = src[i]
            if src.startswith('//', i):
                end = src.find('\n', i)
                end = n if end < 0 else end
                blank(self.chars, i, end)
                i = end
            elif src.startswith('/*', i):
                end = src.find('*/', i + 2)
                end = n if end < 0 else end + 2
                blank(self.chars, i, end)
                i = end
            elif c in '\'"':
                i = self.quoted(i)
            elif c == '`':
                i = self.template(i)
            elif c == '/' and self.regex_allowed(i):
                i = self.regex(i)
            else:
                if stop_at_brace:
                    if c == '{':
                        depth += 1
                    elif c == '}':
                        if not depth:
                            return i + 1
                        depth -= 1
                i += 1
        return i

    def quoted(self, i: int) -> int:
        src, quote, j = self.src, self.src[i], i + 1
        while j < self.n and src[j] != quote and src[j] != '\n':
            j += 2 if src[j] == '\\' else 1
        end = min(j + 1, self.n)
        self.literals.append((i, end, src[i + 1:min(j, self.n)], True))
        blank(self.chars, i + 1, min(j, self.n))
        return end

    def template(self, i: int) -> int:
        src, parts, j = self.src, [], i + 1
        while j < self.n and src[j] != '`':
            if src[j] == '\\':
                parts.append(src[j:j + 2])
                j += 2
            elif src.startswith('${', j):
                j = self.code(j + 2, True)
            else:
                parts.append(src[j])
                j += 1
        end = min(j + 1, self.n)
        self.literals.append((i, end, ''.join(parts), True))
        blank(self.chars, i + 1, min(j, self.n))
        return end

    def regex(self, i: int) -> int:
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
        blank(self.chars, i + 1, min(j, self.n))
        return j + 1


def lex_cs(src: str):
    """C#：注释、字符字面量、普通/逐字/插值/原始字符串；插值表达式按代码递归扫描。"""
    chars, literals, n = list(src), [], len(src)

    def code(i: int, closing: int = 0) -> int:
        depth = 0
        while i < n:
            if src.startswith('//', i):
                end = src.find('\n', i)
                end = n if end < 0 else end
                blank(chars, i, end)
                i = end
            elif src.startswith('/*', i):
                end = src.find('*/', i + 2)
                end = n if end < 0 else end + 2
                blank(chars, i, end)
                i = end
            elif src[i] == '"':
                i = string(i)
            elif src[i] == "'":
                end = i + 1
                while end < n and src[end] not in "'\n":
                    end += 2 if src[end] == '\\' else 1
                blank(chars, i, end + 1)
                i = end + 1
            elif closing and not depth and src.startswith('}' * closing, i):
                return i + closing
            else:
                if closing:
                    if src[i] == '{':
                        depth += 1
                    elif src[i] == '}':
                        depth -= 1
                i += 1
        return i

    def string(quote: int) -> int:
        start = quote
        while start and src[start - 1] in '$@':
            start -= 1
        prefix = src[start:quote]
        dollars, verbatim = prefix.count('$'), '@' in prefix
        q = 1
        while quote + q < n and src[quote + q] == '"':
            q += 1
        raw = q >= 3
        if not raw:
            q = 1
        i, parts = quote + q, []
        while i < n:
            if not raw and verbatim and src.startswith('""', i):
                parts.append('"')
                i += 2
            elif src.startswith('"' * q, i) or (not raw and not verbatim and src[i] == '\n'):
                break
            elif not raw and not verbatim and src[i] == '\\':
                parts.append(src[i:i + 2])
                i += 2
            elif dollars and src.startswith('{' * (dollars if raw else 1), i):
                if not raw and src.startswith('{{', i):
                    parts.append('{')
                    i += 2
                else:
                    i = code(i + (dollars if raw else 1), dollars if raw else 1)
            else:
                parts.append(src[i])
                i += 1
        end = min(i + q, n)
        literals.append((start, end, ''.join(parts), not raw and not verbatim))
        blank(chars, start, end)
        chars[start] = '"'
        return end

    code(0)
    return ''.join(chars), literals


# 转义自左向右逐个识别：`\\\\` 先被当作一个反斜杠吃掉，`'\\\\u4e2d'` 运行时是 ASCII 文本，不能解码成汉字。
TS_ESCAPE = re.compile(r'\\(?:u\{([0-9a-fA-F]+)\}|u([0-9a-fA-F]{4})|x([0-9a-fA-F]{2})|(.))', re.S)
CS_ESCAPE = re.compile(r'\\(?:u([0-9a-fA-F]{4})|U([0-9a-fA-F]{8})|x([0-9a-fA-F]{1,4})|(.))', re.S)


def decode(text: str, escapes: bool, csharp: bool) -> str:
    if not escapes:
        return text

    def one(m):
        *codes, other = m.groups()
        code = next((g for g in codes if g), None)
        if code is None:
            return other
        value = int(code, 16)
        return chr(value) if value <= 0x10FFFF else ''

    return (CS_ESCAPE if csharp else TS_ESCAPE).sub(one, text)


# ---------------------------------------------------------------------------
# 调用点与消息参数。

TS_CALL = re.compile(r'(?<![\w.$])(expect(?:\s*\.\s*(?:soft|poll|unreachable))?|assert(?:\s*\.\s*\w+)?)\s*\(')
TS_THROW = re.compile(r'\bthrow\s+(?:new\s+)?(?:[\w$]+\s*\.\s*)*(\w*Error)\s*\(')
CS_CALL = re.compile(r'(?<![\w.:])(?:(?:global\s*::\s*)?Xunit\s*\.\s*)?Assert\s*\.\s*(True|False|Fail)\s*\(')
CS_THROW = re.compile(r'\bthrow\s+new\s+(?:(?:global\s*::\s*)?\w+\s*\.\s*)*(\w+Exception)\s*\(')
NAMED = re.compile(r'\s*@?([A-Za-z_]\w*)\s*:(?!:)')

# node:assert：message 是第几个参数（ok/assert 本身为 1）
NODE_ASSERT_INDEX = {
    'ok': 1, 'equal': 2, 'notEqual': 2, 'strictEqual': 2, 'notStrictEqual': 2, 'deepEqual': 2,
    'notDeepEqual': 2, 'deepStrictEqual': 2, 'notDeepStrictEqual': 2, 'partialDeepStrictEqual': 2,
    'match': 2, 'doesNotMatch': 2,
}
NODE_ASSERT_ERROR_MATCHERS = {'throws', 'rejects', 'doesNotThrow', 'doesNotReject'}


GENERIC_CHARS = re.compile(r'[\w\s.,?\[\]:<>]')


def generic_end(masked: str, i: int):
    """masked[i] == '<'：紧跟标识符、能配对到 `>` 且其后是 `(` 时视为泛型调用的类型实参，返回 `>` 之后的位置。

    只认这一种写法（`Check<Key, Value>(...)`、`new Dictionary<A, B>()`），其余 `<` 按比较运算处理。
    """
    if not i or not (masked[i - 1].isalnum() or masked[i - 1] == '_'):
        return None
    depth = 0
    for j in range(i, len(masked)):
        c = masked[j]
        if not GENERIC_CHARS.match(c):
            return None
        if c == '<':
            depth += 1
        elif c == '>':
            depth -= 1
            if not depth:
                rest = masked[j + 1:].lstrip()
                return j + 1 if rest.startswith('(') else None
    return None


def arguments(masked: str, opening: int, generics: bool) -> list[tuple[int, int]]:
    """masked[opening] == '('；返回各实参的 [起, 止) 区间。字符串与注释已抹掉，括号计数可靠。

    generics 只对 C# 与 TypeScript 打开：两者都把 `a<b, c>(d)` 解析为带类型实参的调用
    （C# 规范「语法歧义」一节的 `F(G<A, B>(7))`；TypeScript 解析器同样如此），
    纯 JavaScript 没有类型实参，同一写法是两个比较表达式。"""
    start, stack, result = opening + 1, 0, []
    i = start - 1
    while i + 1 < len(masked):
        i += 1
        c = masked[i]
        if c == '<' and generics:
            end = generic_end(masked, i)
            if end is not None:
                i = end - 1
            continue
        if c == ')' and not stack:
            if masked[start:i].strip() or result:
                result.append((start, i))
            return result
        if c == ',' and not stack:
            result.append((start, i))
            start = i + 1
        elif c in '([{':
            stack += 1
        elif c in ')]}':
            stack -= 1
    return []


def ts_message_spans(masked: str, literals, generics: bool) -> list[tuple[int, int]]:
    spans = []
    starts = {a: b for a, b, *_ in literals}

    def single_string(span):
        """实参恰好是一个字符串字面量（node:assert 的 throws 族把字符串第二参数当作 message）。"""
        a, b = span
        text = masked[a:b]
        first = a + len(text) - len(text.lstrip())
        return first in starts and starts[first] >= a + len(text.rstrip())

    for m in TS_CALL.finditer(masked):
        name = re.sub(r'\s+', '', m.group(1))
        args = arguments(masked, m.end() - 1, generics)
        if name == 'expect.poll':
            if len(args) > 1:
                a, b = args[1]
                if masked[a:b].strip().startswith('{'):
                    spans.extend(object_property(masked, literals, a, b, 'message'))
                else:
                    spans.append((a, b))
            continue
        if name == 'expect.unreachable':
            index = 0
        elif name in ('expect', 'expect.soft', 'assert'):
            index = 1
        else:
            method = name.split('.', 1)[1]
            if method == 'fail':
                index = 0 if len(args) == 1 else 2
            elif method in NODE_ASSERT_ERROR_MATCHERS:
                index = 1 if len(args) == 2 and single_string(args[1]) else 2
            else:
                index = NODE_ASSERT_INDEX.get(method)
        if index is not None and len(args) > index:
            spans.append(args[index])
    for m in TS_THROW.finditer(masked):
        args = arguments(masked, m.end() - 1, generics)
        index = 1 if m.group(1) == 'AggregateError' else 0
        if len(args) > index:
            spans.append(args[index])
    return spans


def object_property(masked: str, literals, a: int, b: int, name: str) -> list[tuple[int, int]]:
    """对象字面量 { ... } 顶层的某个属性值区间；键可以是标识符或带引号的字符串。"""
    quoted_keys = {start: (end, text) for start, end, text, _ in literals}
    opening = masked.index('{', a)
    spans, depth, i = [], 0, opening + 1
    item_start = i
    while i < b:
        c = masked[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            if not depth:
                break
            depth -= 1
        elif c == ',' and not depth:
            spans.append((item_start, i))
            item_start = i + 1
        i += 1
    spans.append((item_start, i))
    result = []
    for start, end in spans:
        key_at = start + len(masked[start:end]) - len(masked[start:end].lstrip())
        if key_at in quoted_keys:
            key_end, key = quoted_keys[key_at]
            colon = re.match(r'\s*:', masked[key_end:end])
            if key == name and colon:
                result.append((key_end + colon.end(), end))
            continue
        prop = re.match(r'\s*(\w+)\s*:', masked[start:end])
        if prop and prop.group(1) == name:
            result.append((start + prop.end(), end))
    return result


def cs_message_index(kind: str, count: int):
    """message 形参的位置。C# 的具名实参若后面还跟位置实参，必须就在自己的形参位置上，
    所以按原实参顺序映射即可，不能剔除具名实参后重新编号。"""
    if kind in ('True', 'False'):
        return 1
    if kind == 'Fail':
        return 0
    if kind in ('ArgumentNullException', 'ObjectDisposedException'):
        return 1 if count >= 2 else None
    if kind == 'ArgumentOutOfRangeException':
        return {2: 1, 3: 2}.get(count)
    if kind == 'BusinessException':
        return 1
    return 0


def cs_message_spans(masked: str) -> list[tuple[int, int]]:
    spans = []
    for pattern, names in ((CS_CALL, {'userMessage', 'message'}), (CS_THROW, {'message'})):
        for m in pattern.finditer(masked):
            args = arguments(masked, m.end() - 1, True)
            index = cs_message_index(m.group(1), len(args))
            for position, (a, b) in enumerate(args):
                named = NAMED.match(masked[a:b])
                if named:
                    if named.group(1) in names:
                        spans.append((a + named.end(), b))
                elif position == index:
                    spans.append((a, b))
    return spans


def diagnostics(src: str, language: str) -> list[int]:
    """返回含中日韩字符的诊断消息所在行号（字面量起始行）。"""
    csharp = language == 'cs'
    if csharp:
        masked, literals = lex_cs(src)
        spans = cs_message_spans(masked)
    else:
        masked, literals = TsLexer(src).run()
        spans = ts_message_spans(masked, literals, generics=language == 'ts')
    top, previous_end = [], -1
    for literal in sorted(literals):
        if literal[0] >= previous_end:
            top.append(literal)
            previous_end = literal[1]
    lines = set()
    for a, b, text, escapes in top:
        if CJK.search(decode(text, escapes, csharp)) and any(x <= a and b <= y for x, y in spans):
            lines.add(src.count('\n', 0, a) + 1)
    return sorted(lines)


# ---------------------------------------------------------------------------
# 扫描范围。

def project_zones(root: Path, frontend_required: bool):
    """(目录, 后缀, 是否必需)。可选根只在存在时扫描，存在就必须扫到文件。"""
    return [
        (root / 'backend/tests', ('.cs',) + TS_SUFFIXES, True),
        (root / 'frontend', ('.spec.ts', '.testing.ts'), frontend_required),
        (root / 'frontend/e2e', TS_SUFFIXES, False),
        (root / 'e2e', TS_SUFFIXES, False),
    ]


def scan(zones, report_root: Path):
    failures, seen = [], set()
    for directory, suffixes, required in zones:
        label = directory.relative_to(report_root).as_posix()
        if not directory.is_dir():
            if required:
                failures.append(f'{label}: required test source root is missing')
            continue
        count = 0
        for path in sorted(directory.rglob('*')):
            if not path.is_file() or not path.name.endswith(suffixes):
                continue
            if SKIP_DIRS & set(path.relative_to(directory).parts[:-1]):
                continue
            count += 1
            if path in seen:
                continue
            seen.add(path)
            relative = path.relative_to(report_root).as_posix()
            text = path.read_text(encoding='utf-8')
            failures.extend(f'{relative}:{line}: test diagnostic message contains CJK characters; write it in English'
                            for line in diagnostics(text, language_of(path)))
        if not count:
            failures.append(f'{label}: no test sources discovered; the scan scope is probably stale')
    return len(seen), failures


def language_of(path: Path) -> str:
    if path.suffix == '.cs':
        return 'cs'
    return 'js' if path.suffix in ('.js', '.mjs', '.cjs') else 'ts'


def repository_zones(repository: Path):
    return project_zones(repository / 'template', True) + [(repository / 'framework/tests', ('.cs',), True)]


# ---------------------------------------------------------------------------

LANGUAGE_CASES = [
    # (源码, 语言 cs/ts/js, 期望行号)
    ("expect('中文数据', 'English message').toBe('中文期望');", 'ts', []),
    ("// expect(x, '中文')\nexpect(x, '中文诊断');", 'ts', [2]),
    ("expect.soft(x, `中文 ${lookup('中文数据')}`);", 'ts', [1]),
    ("expect(x, `English ${lookup('中文数据')}`);", 'ts', []),
    ("expect.poll(() => x, { timeout: 10, message: '中文' });", 'ts', [1]),
    ("expect.poll(() => '中文数据', { message: 'English', other: '中文数据' });", 'ts', []),
    ("expect.poll(() => x, { 'message': '中文' });", 'ts', [1]),
    ("expect.poll(() => x, { \"message\": `中文` });", 'ts', [1]),
    ("expect.poll(() => x, { 'label': '中文数据', message: 'English' });", 'ts', []),
    ("expect(x, 'Expected literal \\\\u4e2d');", 'ts', []),
    ("expect(x, 'Escaped \\\\\\u4e2d');", 'ts', [1]),
    ("expect(x, '\\x41\\u{4e2d}');", 'ts', [1]),
    ("expect.unreachable('中文');", 'ts', [1]),
    ("throw new Error('中文'); const stub = new Error('中文测试数据');", 'ts', [1]),
    ("if (!row) throw new TypeError(`中文 ${row}`);", 'ts', [1]),
    ("throw new AggregateError([new Error('中文数据')], 'English');", 'ts', []),
    (r"expect(x, '\u4e2d\u6587');", 'ts', [1]),
    ("expect(\n  x,\n  '跨行中文',\n).toBe(1);", 'ts', [3]),
    ("assert.ok(false, '中文诊断');", 'ts', [1]),
    ("assert(value, '中文');", 'ts', [1]),
    ("assert.strictEqual(actual, '中文期望', 'English');", 'ts', []),
    ("assert.deepStrictEqual(actual, expected, '中文');", 'ts', [1]),
    ("assert.fail('中文');", 'ts', [1]),
    ("assert.throws(() => run(), /中文/, '中文');", 'ts', [1]),
    ("assert.throws(() => run(), '中文');", 'ts', [1]),
    ("const re = /'/; expect(x, '中文');", 'ts', [1]),
    ("const data = { message: '中文数据' }; expect(data).toEqual(data);", 'ts', []),
    ("it('中文标题', () => { expect(name).toBe('株式会社'); });", 'ts', []),
    ('Assert.True(x, "中文"); Assert.Equal("中文数据", x);', 'cs', [1]),
    ('Assert.False(userMessage: "中文", condition: x);', 'cs', [1]),
    ('Assert.True(userMessage: "English", condition: lookup("中文数据"));', 'cs', []),
    ('Assert.Fail("中文");', 'cs', [1]),
    ('Assert.True(condition: false, "中文");', 'cs', [1]),
    ('Assert.True(condition: Check("中文数据"), "English");', 'cs', []),
    ('throw new ArgumentNullException(paramName: nameof(x), "中文");', 'cs', [1]),
    ('throw new ArgumentNullException(paramName: "中文数据", "English");', 'cs', []),
    ('Assert.True(Check<Key, Value>(), "中文");', 'cs', [1]),
    ('Assert.True(Lookup<Dictionary<string, int>>("中文数据"), "English");', 'cs', []),
    ('Assert.True(a < b, "中文");', 'cs', [1]),
    ('Assert.True(a < b, c > (d), "中文数据");', 'cs', []),
    ('Assert.True(a<b, c>d, "中文数据");', 'cs', []),
    ('Assert.True(x, "Expected literal \\\\u4e2d");', 'cs', []),
    ('Assert.True(x, "\\x4e2d");', 'cs', [1]),
    ('Xunit.Assert.True(false, "中文"); Other.Assert.True(x, "中文数据");', 'cs', [1]),
    ('var id = tenant ?? throw new InvalidOperationException("种子必须在租户上下文内执行");', 'cs', [1]),
    ('throw new ArgumentNullException(paramName: "中文数据", message: "English message");', 'cs', []),
    ('throw new ArgumentNullException("中文数据");', 'cs', []),
    ('throw new ArgumentNullException(nameof(x), "中文");', 'cs', [1]),
    ('throw new ArgumentOutOfRangeException(nameof(x), "中文数据", "English");', 'cs', []),
    ('throw new BusinessException("中文数据", "English");', 'cs', []),
    ('throw new InvalidOperationException(message: "English", innerException: Wrap("中文数据"));', 'cs', []),
    ('throw new global::System.InvalidOperationException(\n    "中文");', 'cs', [2]),
    ('Assert.True(x, $"English {string.Join("、", values)}");', 'cs', []),
    ('Assert.True(x, $@"中文 {string.Join("、", values)}");', 'cs', [1]),
    ('Assert.True(x, @"English ""中文""");', 'cs', [1]),
    ('Assert.True(x, """中文""");', 'cs', [1]),
    ('Assert.True(x, $$"""English {{lookup("中文数据")}}""");', 'cs', []),
    ('Assert.True(x, $$"""中文 {{lookup("中文数据")}}""");', 'cs', [1]),
    (r'Assert.True(x, "\u4e2d");', 'cs', [1]),
    ('var s = "Assert.True(x, \\"中文\\")";', 'cs', []),
    ('/* Assert.Fail("中文"); */ const char c = \'中\';', 'cs', []),
    ('#if (IncludeX)\nAssert.True(x, "中文");\n#endif', 'cs', [2]),
    # `a<b, c>(d)`：C#（规范「语法歧义」）与 TypeScript 都解析为带类型实参的调用，实参只有两个：
    # node:assert.equal 的第 2 个实参是期望值，xUnit Assert.True 的第 2 个实参是 userMessage。
    # 纯 JavaScript 没有类型实参，同一写法是三个实参，中文串落在 equal 的 message 位置。
    ("assert.equal(a<b, c>(d), '中文');", 'ts', []),
    ("assert.equal(a<b, c>(d), '中文');", 'js', [1]),
    ("assert.equal(a < b, c > (d), '中文');", 'js', [1]),
    ('Assert.True(a<b, c>(d), "中文");', 'cs', [1]),
]


def self_test() -> int:
    failures = []
    for index, (src, language, expected) in enumerate(LANGUAGE_CASES):
        actual = diagnostics(src, language)
        if actual != expected:
            failures.append(f'language case {index}: expected lines {expected}, got {actual}')

    def project(files):
        base = Path(tempfile.mkdtemp(dir=workspace))
        for relative, content in files.items():
            path = base / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding='utf-8')
        return base

    clean_test = 'public class T { void M() { Assert.True(x, "English"); } }'
    with tempfile.TemporaryDirectory() as workspace:
        scope_cases = [
            ('backend only (no frontend) passes', {'backend/tests/A/T.cs': clean_test}, []),
            ('missing backend/tests fails', {'frontend/src/a.spec.ts': "expect(x, 'ok');"}, ['required test source root is missing']),
            ('empty frontend fails', {'backend/tests/A/T.cs': clean_test, 'frontend/src/main.ts': ''}, ['frontend: no test sources']),
            ('build output is skipped', {'backend/tests/A/T.cs': clean_test,
                                         'backend/tests/A/obj/G.cs': 'Assert.Fail("中文");'}, []),
            ('violation reports path and line', {'backend/tests/A/T.cs': clean_test,
                                                 'frontend/src/a.spec.ts': "\nexpect(x, '中文');"},
             ['frontend/src/a.spec.ts:2: test diagnostic']),
            ('present e2e root must not be empty', {'backend/tests/A/T.cs': clean_test, 'e2e/README.md': ''}, ['e2e: no test sources']),
            ('e2e script is scanned', {'backend/tests/A/T.cs': clean_test, 'e2e/flow.mjs': "assert.equal(a<b, c>(d), '中文');"},
             ['e2e/flow.mjs:1: test diagnostic']),
        ]
        for name, files, expected in scope_cases:
            base = project(files)
            _, problems = scan(project_zones(base, False), base)
            if len(problems) != len(expected) or not all(word in problem for word, problem in zip(expected, problems)):
                failures.append(f'scope case "{name}": expected {expected}, got {problems}')
        repository = project({'template/backend/tests/A/T.cs': clean_test, 'template/frontend/src/a.spec.ts': "expect(x, 'ok');"})
        _, problems = scan(repository_zones(repository), repository)
        if problems != ['framework/tests: required test source root is missing']:
            failures.append(f'scope case "repository requires framework/tests": got {problems}')

    if failures:
        print('FAIL: check-test-diagnostics self-test')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print(f'PASS: check-test-diagnostics self-test ({len(LANGUAGE_CASES)} language cases, {len(scope_cases) + 1} scope cases)')
    return 0


def main() -> int:
    # Windows 上管道输出默认按本地代码页编码；输出本身只用 ASCII，路径里若有非 ASCII 也不会崩
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8', errors='backslashreplace')
    argv = sys.argv[1:]
    if '--self-test' in argv:
        return self_test()
    if '--repository' in argv:
        repository = ROOT.parent
        if not (ROOT / '.template.config').is_dir():
            print(f'FAIL: --repository expects this script under <repository>/template/scripts, found {ROOT}')
            return 1
        count, problems = scan(repository_zones(repository), repository)
    else:
        count, problems = scan(project_zones(ROOT, False), ROOT)
    if problems:
        print(f'FAIL: test diagnostics check found {len(problems)} problem(s) in {count} file(s):')
        for problem in problems:
            print(f'  - {problem}')
        return 1
    print(f'PASS: test diagnostics contain no CJK characters ({count} files; direct message arguments and thrown messages only)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
