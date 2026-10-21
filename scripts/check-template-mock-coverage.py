#!/usr/bin/env python3
"""模板前端 Mock 覆盖闸门（G-11）：后端路由、前端调用与 `_mock` 键三方求差。

Mock 是模板的一个交付面（`useMock` 下整套界面跑在它上面）。前端新调一个端点而 Mock 没跟上，
Mock 模式下的表现是 501 或"点了没反应"，而所有针对真实后端的测试全绿；反过来，后端改了路由而
Mock 还留着旧键，Mock 模式下一切正常、联调时才 404。两端各自的单测发现不了这两类漂移——
跨端校验只放在仓库闸门里，模板载荷里的前后端测试保持互相独立。

三个集合：

- **后端路由**：`backend/src` 下控制器的 `[Route]` 与 `[Http*]`，加上 `ComponentEndpoints.cs` 里
  `api.MapGroup("x").MapXxx(...)` 映射的组件端点——组件端点的子路由从 `framework/components` 中
  对应 `MapXxx` 方法体里的 `MapGet/MapPost/...` 读出。
- **前端调用**：`frontend/src` 非测试 TS 里经 `HttpClient` 发出的请求（方法 + 路径；`get/post/put/delete/patch`
  取方法名，`request('方法', 地址)` 取字面量方法，其余成员或非字面量方法一律按输入不完整失败），以及其余以
  `/api/` 开头的地址字面量（整页跳转、`<img>` 按 GET，赋给表单 `action` 的按 POST）。常量的初值
  （如 `baseUrl`）不单独算引用，在用到它的调用处核对。
- **Mock 键**：`frontend/_mock/api/*.ts` 里 `'方法 路径'` 形式的键。

刻意不 Mock 的端点与只在 Mock 中存在的端点由 `frontend/_mock/index.ts` 开头的注释清单声明
（反引号里写 `方法 路径`，分别跟在含"刻意不 Mock"与"只在 Mock 中存在"的说明行之后）；
闸门只读这一份清单，不另设允许名单。

违规：

1. 前端经 HttpClient 调用的端点没有 Mock（缺 Mock）；
2. 前端经 HttpClient 调用的端点在后端不存在，且未声明为只在 Mock 中存在；
3. Mock 键在后端不存在，且未声明为只在 Mock 中存在（多余 Mock）；
4. 前端其余 `/api/` 地址既没有 Mock，也没有声明为刻意不 Mock；
5. 清单本身过期：声明不 Mock 的端点后端不存在或已有 Mock；声明只在 Mock 中存在的端点没有 Mock 或后端已存在。

路径比较时参数段一律视为同一种参数段（`{id:guid}`、`:id`、`${id}`），字面段不区分大小写；
后端或 Mock 的参数段能匹配前端的字面段，反之不行。

Mock 里有键但前端没有调用的端点不算违规：只要后端存在，它就是后续业务可直接复用的替身。

边界：在模板源码上运行时，`//#if`、`#if` 各分支的内容取并集——某个端点的 Mock 写在了错误的条件
分支里，并集看不出来；按形态核对用 `--root <生成项目根目录>` 在生成产物上运行。

用法：
  python3 scripts/check-template-mock-coverage.py                 # 模板源码
  python3 scripts/check-template-mock-coverage.py --root <生成项目>  # 生成项目（框架源码仍取本仓库）
  python3 scripts/check-template-mock-coverage.py --self-test     # 闸门自检

退出码非 0 表示存在违规或输入不完整（缺目录、缺清单、解析不出调用的方法或地址、后端路由/前端调用/Mock 键
任一为空、某个 Mock 文件或 api/ 控制器或组件端点方法读不出条目），供 CI 阻断。
"""
from __future__ import annotations

import argparse
import re
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_ROOT = REPO_ROOT / "template"
DEFAULT_FRAMEWORK = REPO_ROOT / "framework"

HTTP_VERBS = ("GET", "POST", "PUT", "DELETE", "PATCH")
PARAM = "{}"
UNMOCKED_MARKER = "刻意不 Mock"
MOCK_ONLY_MARKER = "只在 Mock 中存在"


class InputError(Exception):
    """输入不完整或无法解析：闸门不能给出结论，按失败处理。"""


# ---------------------------------------------------------------------------
# 路径
# ---------------------------------------------------------------------------


def normalize_path(raw: str) -> tuple[str, ...]:
    """把各端写法统一成段元组：参数段为 `{}`，去掉查询串、首尾斜杠，字面段转小写。"""
    path = raw.split("?", 1)[0].strip()
    segments: list[str] = []
    for segment in path.strip("/").split("/"):
        if not segment:
            continue
        if segment.startswith(":") or (segment.startswith("{") and segment.endswith("}")):
            segments.append(PARAM)
        elif PARAM in segment:
            # 字面量后面接表达式（如 `challenge${query}`）：表达式拼的是查询串，不是路径的一部分
            stripped = segment.replace(PARAM, "")
            segments.append(stripped.lower() if stripped else PARAM)
        else:
            segments.append(segment.lower())
    return tuple(segments)


def covers(pattern: tuple[str, ...], concrete: tuple[str, ...]) -> bool:
    """pattern 能否匹配 concrete：参数段匹配任意段；字面段只匹配同名字面段。"""
    return len(pattern) == len(concrete) and all(
        p == PARAM or p == c for p, c in zip(pattern, concrete)
    )


def show(path: tuple[str, ...]) -> str:
    return "/" + "/".join(path)


@dataclass(frozen=True)
class Endpoint:
    method: str
    path: tuple[str, ...]
    origin: str

    def label(self) -> str:
        return f"{self.method} {show(self.path)}"


def any_covers(patterns: list[Endpoint], target: Endpoint) -> Endpoint | None:
    for pattern in patterns:
        if pattern.method == target.method and covers(pattern.path, target.path):
            return pattern
    return None


# ---------------------------------------------------------------------------
# TypeScript 词法：去注释、取字符串与模板字面量
# ---------------------------------------------------------------------------

REGEX_PRECEDERS = set("(,=:[!&|?{};+-*%<>~^")
REGEX_KEYWORDS = ("return", "typeof", "case", "in", "of", "delete", "void", "throw", "new")


@dataclass
class StringToken:
    start: int
    end: int
    parts: list[tuple[str, str]]  # ("text", 文本) 或 ("expr", 表达式源码)


def _skip_quoted(src: str, i: int) -> int:
    quote = src[i]
    i += 1
    while i < len(src):
        if src[i] == "\\":
            i += 2
            continue
        if src[i] == quote:
            return i + 1
        if src[i] == "\n":
            break
        i += 1
    raise InputError(f"未闭合的字符串字面量（偏移 {i}）")


def _skip_regex(src: str, i: int) -> int:
    i += 1
    in_class = False
    while i < len(src):
        ch = src[i]
        if ch == "\\":
            i += 2
            continue
        if ch == "[":
            in_class = True
        elif ch == "]":
            in_class = False
        elif ch == "/" and not in_class:
            i += 1
            while i < len(src) and src[i].isalpha():
                i += 1
            return i
        elif ch == "\n":
            break
        i += 1
    raise InputError(f"未闭合的正则字面量（偏移 {i}）")


def _regex_allowed(src: str, i: int) -> bool:
    j = i - 1
    while j >= 0 and src[j] in " \t\r\n":
        j -= 1
    if j < 0:
        return True
    if src[j] in REGEX_PRECEDERS:
        return True
    word = re.search(r"([A-Za-z_$][\w$]*)$", src[: j + 1])
    return bool(word and word.group(1) in REGEX_KEYWORDS)


def _scan_template(src: str, i: int, tokens: list[StringToken]) -> int:
    """i 指向反引号；返回闭合反引号之后的位置，并记下这个模板字面量。"""
    start = i
    i += 1
    parts: list[tuple[str, str]] = []
    text: list[str] = []
    while i < len(src):
        ch = src[i]
        if ch == "\\":
            text.append(src[i : i + 2])
            i += 2
            continue
        if ch == "`":
            if text:
                parts.append(("text", "".join(text)))
            tokens.append(StringToken(start, i + 1, parts))
            return i + 1
        if ch == "$" and src.startswith("${", i):
            if text:
                parts.append(("text", "".join(text)))
                text = []
            expr_start = i + 2
            i = _scan_code(src, expr_start, tokens, stop_at_brace=True)
            parts.append(("expr", src[expr_start : i - 1]))
            continue
        text.append(ch)
        i += 1
    raise InputError(f"未闭合的模板字面量（偏移 {start}）")


def _scan_code(src: str, i: int, tokens: list[StringToken], stop_at_brace: bool, masked: list[str] | None = None) -> int:
    depth = 0
    while i < len(src):
        ch = src[i]
        if src.startswith("//", i):
            end = src.find("\n", i)
            end = len(src) if end < 0 else end
            if masked is not None:
                masked[i:end] = [" "] * (end - i)
            i = end
            continue
        if src.startswith("/*", i):
            end = src.find("*/", i + 2)
            if end < 0:
                raise InputError(f"未闭合的块注释（偏移 {i}）")
            end += 2
            if masked is not None:
                masked[i:end] = [c if c == "\n" else " " for c in src[i:end]]
            i = end
            continue
        if ch in "'\"":
            end = _skip_quoted(src, i)
            tokens.append(StringToken(i, end, [("text", src[i + 1 : end - 1])]))
            i = end
            continue
        if ch == "`":
            i = _scan_template(src, i, tokens)
            continue
        if ch == "/" and _regex_allowed(src, i):
            i = _skip_regex(src, i)
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            if stop_at_brace and depth == 0:
                return i + 1
            depth -= 1
        i += 1
    if stop_at_brace:
        raise InputError("模板字面量里的表达式没有闭合")
    return i


def lex_typescript(src: str) -> tuple[str, list[StringToken]]:
    """返回去掉注释（换成空白、保留偏移）的源码，以及按出现顺序的全部字符串与模板字面量。"""
    masked = list(src)
    tokens: list[StringToken] = []
    _scan_code(src, 0, tokens, stop_at_brace=False, masked=masked)
    tokens.sort(key=lambda token: token.start)
    return "".join(masked), tokens


IDENTIFIER_CHAIN = re.compile(r"[A-Za-z_$][\w$]*(?:\s*\.\s*[A-Za-z_$][\w$]*)*")


@dataclass
class Constants:
    """地址常量：裸名与 `this.x` 只认本文件；`Class.x` 先本文件、再其他文件（只认全仓唯一的值）。"""

    local: dict[str, str]
    shared: dict[str, str]

    def resolve(self, expr: str) -> str | None:
        if not IDENTIFIER_CHAIN.fullmatch(expr):
            return None
        parts = re.split(r"\s*\.\s*", expr)
        if len(parts) == 1 or (len(parts) == 2 and parts[0] == "this"):
            return self.local.get(parts[-1])
        if len(parts) == 2 and parts[0][:1].isupper():  # 类的静态常量，如 AuthService.loginUrl
            return self.local.get(parts[1], self.shared.get(parts[1]))
        return None


def render(token: StringToken, constants: Constants | None) -> str:
    """把字面量拼成地址：能解析的常量代入，其余表达式记为参数段。"""
    pieces: list[str] = []
    for kind, value in token.parts:
        if kind == "text":
            pieces.append(value)
            continue
        resolved = constants.resolve(value.strip()) if constants else None
        pieces.append(resolved if resolved is not None else PARAM)
    return "".join(pieces)


# ---------------------------------------------------------------------------
# 前端
# ---------------------------------------------------------------------------

CONSTANT_DECL = re.compile(
    r"\b(?:readonly|const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=;\n]+)?=\s*(?=['\"`])"
)
HTTP_CLIENT_NAME = re.compile(
    r"\b([A-Za-z_$][\w$]*)\s*(?:=\s*inject\(\s*HttpClient\s*\)|:\s*HttpClient\b)"
)


def _constants(masked: str, tokens: list[StringToken]) -> dict[str, str]:
    by_start = {token.start: token for token in tokens}
    constants: dict[str, str] = {}
    for match in CONSTANT_DECL.finditer(masked):
        token = by_start.get(match.end())
        if token and all(kind == "text" for kind, _ in token.parts):
            constants.setdefault(match.group(1), render(token, None))
    return constants


def _skip_generic(masked: str, i: int) -> int:
    if i >= len(masked) or masked[i] != "<":
        return i
    depth = 0
    while i < len(masked):
        if masked[i] == "<":
            depth += 1
        elif masked[i] == ">" and masked[i - 1] != "=":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return i


def _skip_space(masked: str, i: int) -> int:
    while i < len(masked) and masked[i] in " \t\r\n":
        i += 1
    return i


@dataclass
class FrontendUsage:
    calls: list[Endpoint] = field(default_factory=list)
    references: list[Endpoint] = field(default_factory=list)


def collect_frontend(src_dir: Path, root: Path) -> FrontendUsage:
    usage = FrontendUsage()
    files = sorted(
        path for path in src_dir.rglob("*.ts")
        if not path.name.endswith(".spec.ts") and "node_modules" not in path.parts
    )
    parsed: dict[Path, tuple[str, list[StringToken], dict[str, str]]] = {}
    global_constants: dict[str, set[str]] = {}
    for path in files:
        text = path.read_text(encoding="utf-8")
        try:
            masked, tokens = lex_typescript(text)
        except InputError as error:
            raise InputError(f"{path.relative_to(root)}: {error}") from None
        constants = _constants(masked, tokens)
        parsed[path] = (masked, tokens, constants)
        for name, value in constants.items():
            global_constants.setdefault(name, set()).add(value)
    unique_globals = {name: next(iter(values)) for name, values in global_constants.items() if len(values) == 1}

    for path, (masked, tokens, local_constants) in parsed.items():
        rel = path.relative_to(root).as_posix()
        constants = Constants(local_constants, unique_globals)
        by_start = {token.start: token for token in tokens}
        consumed: set[int] = set()

        clients = set(HTTP_CLIENT_NAME.findall(masked))
        if clients:
            names = "|".join(re.escape(name) for name in sorted(clients))
            call = re.compile(rf"\b(?:this\s*\.\s*)?(?:{names})\s*\.\s*([A-Za-z_$][\w$]*)\s*")
            for match in call.finditer(masked):
                i = _skip_generic(masked, match.end())
                i = _skip_space(masked, i)
                if i >= len(masked) or masked[i] != "(":
                    continue
                i = _skip_space(masked, i + 1)
                line = masked.count("\n", 0, i) + 1
                member = match.group(1)
                if member == "request":
                    # request(方法, 地址, …)：方法必须是静态字面量，否则无法判定读写，也就核对不了 Mock 键
                    token = by_start.get(i)
                    verb = render(token, None).upper() if token and all(kind == "text" for kind, _ in token.parts) else None
                    if verb not in HTTP_VERBS:
                        raise InputError(f"{rel}:{line}: 解析不出 HttpClient.request 的 HTTP 方法，请改用字面量方法与地址")
                    consumed.add(token.start)
                    i = _skip_space(masked, token.end)
                    if i >= len(masked) or masked[i] != ",":
                        raise InputError(f"{rel}:{line}: 解析不出 HttpClient.request 调用的地址，请改用字面量或常量")
                    i = _skip_space(masked, i + 1)
                    line = masked.count("\n", 0, i) + 1
                elif member.upper() in HTTP_VERBS:
                    verb = member.upper()
                else:
                    raise InputError(f"{rel}:{line}: 不支持的 HttpClient 调用 {member}()，闸门无法核对它的方法与地址")
                token = by_start.get(i)
                if token is not None:
                    consumed.add(token.start)
                    url = render(token, constants)
                else:
                    chain = IDENTIFIER_CHAIN.match(masked, i)
                    url = constants.resolve(chain.group(0)) if chain else None
                    if url is None:
                        raise InputError(f"{rel}:{line}: 解析不出 HttpClient 调用的地址，请改用字面量或常量")
                if url.startswith("/api/"):
                    usage.calls.append(Endpoint(verb, normalize_path(url), f"{rel}:{line}"))

        mocked_checks = {
            match.end() for match in re.finditer(r"\bisMockedUrl\s*\(\s*", masked)
        }
        declared = {match.end() for match in CONSTANT_DECL.finditer(masked)}
        for token in tokens:
            if token.start in consumed or token.start in mocked_checks:
                continue
            url = render(token, constants)
            path = normalize_path(url)
            # 只有前缀（如拦截器里的 '/api/'）不是一个端点
            if not url.startswith("/api/") or len(path) < 3:
                continue
            # 常量的初值（如 baseUrl）不是一次引用：它在用处按调用核对
            if token.start in declared:
                continue
            line = masked.count("\n", 0, token.start) + 1
            # 不经 HttpClient 的地址按浏览器行为定方法：赋给表单 action 的是提交（POST），其余是导航或资源加载（GET）
            method = "POST" if re.search(r"\.action\s*=\s*$", masked[max(0, token.start - 40) : token.start]) else "GET"
            usage.references.append(Endpoint(method, path, f"{rel}:{line}"))
    return usage


# ---------------------------------------------------------------------------
# Mock
# ---------------------------------------------------------------------------

MOCK_KEY = re.compile(rf"""['"]({'|'.join(HTTP_VERBS)}) (/[^'"\s]+)['"]\s*:""")
DECLARED_ENTRY = re.compile(rf"`({'|'.join(HTTP_VERBS)}) (/[^`\s]+)`")


def collect_mock_keys(mock_api_dir: Path, root: Path) -> list[Endpoint]:
    keys: list[Endpoint] = []
    for path in sorted(mock_api_dir.glob("*.ts")):
        if path.name.endswith(".spec.ts"):
            continue
        masked, _ = lex_typescript(path.read_text(encoding="utf-8"))
        rel = path.relative_to(root).as_posix()
        found = list(MOCK_KEY.finditer(masked))
        # 每个 Mock 文件都应读出键：读出零个说明键的写法变了，静默跳过会让整个文件的端点都不参与核对
        if not found:
            raise InputError(f"{rel}: 读不出任何 '方法 路径' 形式的 Mock 键")
        for match in found:
            line = masked.count("\n", 0, match.start()) + 1
            keys.append(Endpoint(match.group(1), normalize_path(match.group(2)), f"{rel}:{line}"))
    return keys


def collect_declarations(index_file: Path, root: Path) -> tuple[list[Endpoint], list[Endpoint]]:
    """读 `_mock/index.ts` 开头注释里的两份清单：刻意不 Mock、只在 Mock 中存在。"""
    if not index_file.is_file():
        raise InputError(f"缺少 {index_file.relative_to(root)}，无法读取刻意不 Mock 的端点清单")
    rel = index_file.relative_to(root).as_posix()
    unmocked: list[Endpoint] = []
    mock_only: list[Endpoint] = []
    section: list[Endpoint] | None = None
    seen_marker = False
    for number, line in enumerate(index_file.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.strip()
        if not stripped.startswith("//"):
            break
        if UNMOCKED_MARKER in stripped:
            section, seen_marker = unmocked, True
        elif MOCK_ONLY_MARKER in stripped:
            section, seen_marker = mock_only, True
        for match in DECLARED_ENTRY.finditer(stripped):
            if section is None:
                raise InputError(f"{rel}:{number}: 清单项出现在说明行之前，无法判断属于哪一份清单")
            section.append(Endpoint(match.group(1), normalize_path(match.group(2)), f"{rel}:{number}"))
    if not seen_marker:
        raise InputError(f"{rel}: 开头注释里没有含「{UNMOCKED_MARKER}」的清单说明行")
    return unmocked, mock_only


# ---------------------------------------------------------------------------
# 后端
# ---------------------------------------------------------------------------

CLASS_DECL = re.compile(r"\bclass\s+\w+")
CLASS_ROUTE = re.compile(r'\[Route\(\s*"([^"]*)"\s*\)\]')
ACTION_ROUTE = re.compile(rf'\[Http({"|".join(v.title() for v in HTTP_VERBS)})(?:\(\s*"([^"]*)"\s*\))?\]')
GROUP_VAR = re.compile(r'\bvar\s+(\w+)\s*=\s*(\w+)\s*\.\s*MapGroup\(\s*"([^"]*)"\s*\)')
GROUP_COMPONENT = re.compile(r'\b(\w+)\s*\.\s*MapGroup\(\s*"([^"]*)"\s*\)\s*\.\s*(Map\w+)\s*(?:<[^>(]*>)?\s*\(')
MINIMAL_ROUTE = re.compile(
    rf'\b(\w+)\s*\.\s*Map({"|".join(v.title() for v in HTTP_VERBS)})\(\s*(string\.Empty|"[^"]*"|\$"[^"]*"|\w+)'
)
COMPONENT_METHOD = re.compile(r"public\s+static\s+RouteGroupBuilder\s+(Map\w+)\s*(?:<[^>(]*>)?\s*\(")


def strip_csharp_comments(src: str) -> str:
    def blank(match: re.Match[str]) -> str:
        return re.sub(r"[^\n]", " ", match.group(0))

    pattern = re.compile(r'//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"', re.S)
    return pattern.sub(lambda m: blank(m) if m.group(0).startswith("/") else m.group(0), src)


def join_route(*parts: str) -> str:
    return "/".join(part.strip("/") for part in parts if part and part.strip("/"))


def _route_argument(argument: str, src: str) -> str:
    """MapGet 的路由实参：字面量、插值串或同文件里赋过值的局部变量。"""
    if argument == "string.Empty":
        return ""
    if argument.startswith('"'):
        return argument[1:-1]
    if argument.startswith('$"'):
        return _interpolated(argument[2:-1])
    assigned = re.search(rf'\b{re.escape(argument)}\s*=\s*(\$?"[^"]*")', src)
    if not assigned:
        raise InputError(f"解析不出路由变量 {argument} 的值")
    return _route_argument(assigned.group(1), src)


def _interpolated(body: str) -> str:
    body = body.replace("{{", "\x01").replace("}}", "\x02")
    body = re.sub(r"\{[^{}]*\}", PARAM, body)
    return body.replace("\x01", "{").replace("\x02", "}")


def collect_component_methods(framework_dir: Path) -> dict[str, list[tuple[str, str]]]:
    """框架组件里每个 `MapXxx(...)` 方法映射的 (方法, 相对路由)。同名重载取并集。"""
    methods: dict[str, list[tuple[str, str]]] = {}
    components = framework_dir / "components"
    if not components.is_dir():
        raise InputError(f"缺少框架源码目录 {components}")
    for path in sorted(components.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        src = strip_csharp_comments(path.read_text(encoding="utf-8"))
        starts = [(match.start(), match.group(1)) for match in COMPONENT_METHOD.finditer(src)]
        for index, (start, name) in enumerate(starts):
            end = starts[index + 1][0] if index + 1 < len(starts) else len(src)
            body = src[start:end]
            routes = methods.setdefault(name, [])
            for match in MINIMAL_ROUTE.finditer(body):
                routes.append((match.group(2).upper(), _route_argument(match.group(3), src)))
    return methods


def collect_backend(backend_src: Path, framework_dir: Path, root: Path) -> list[Endpoint]:
    if not backend_src.is_dir():
        raise InputError(f"缺少后端源码目录 {backend_src.relative_to(root)}")
    routes: list[Endpoint] = []
    component_methods: dict[str, list[tuple[str, str]]] | None = None
    for path in sorted(backend_src.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        rel = path.relative_to(root).as_posix()
        src = strip_csharp_comments(path.read_text(encoding="utf-8"))

        classes = [match.start() for match in CLASS_DECL.finditer(src)]
        class_routes: dict[int, str] = {}
        for match in CLASS_ROUTE.finditer(src):
            following = next((start for start in classes if start > match.start()), None)
            if following is not None:
                class_routes.setdefault(following, match.group(1))
        actions = list(ACTION_ROUTE.finditer(src))
        # 类级路由指向 api/ 的控制器至少应读出一个 action：读出零个说明特性写法变了
        if not actions and any(route.lstrip("/~").lower().startswith("api/") for route in class_routes.values()):
            raise InputError(f"{rel}: 控制器声明了 api/ 路由，却读不出任何 [HttpGet]/[HttpPost]/... action")
        for match in actions:
            owner = max((start for start in classes if start < match.start()), default=None)
            template = match.group(2) or ""
            prefix = "" if template.startswith(("/", "~/")) else class_routes.get(owner, "")
            route = join_route(prefix, template.lstrip("~"))
            line = src.count("\n", 0, match.start()) + 1
            routes.append(Endpoint(match.group(1).upper(), normalize_path(route), f"{rel}:{line}"))

        groups = {"app": ""}
        for match in GROUP_VAR.finditer(src):
            groups[match.group(1)] = join_route(groups.get(match.group(2), ""), match.group(3))
        for match in GROUP_COMPONENT.finditer(src):
            if component_methods is None:
                component_methods = collect_component_methods(framework_dir)
            name = match.group(3)
            if name not in component_methods:
                raise InputError(f"{rel}: 在 {framework_dir} 中找不到组件端点方法 {name}")
            if not component_methods[name]:
                raise InputError(f"{rel}: 组件端点方法 {name} 里读不出任何 MapGet/MapPost/... 路由")
            prefix = join_route(groups.get(match.group(1), ""), match.group(2))
            line = src.count("\n", 0, match.start()) + 1
            for method, relative in component_methods[name]:
                routes.append(Endpoint(method, normalize_path(join_route(prefix, relative)), f"{rel}:{line} → {name}"))
        for match in MINIMAL_ROUTE.finditer(src):
            if match.group(1) in groups:
                prefix = groups[match.group(1)]
                line = src.count("\n", 0, match.start()) + 1
                route = join_route(prefix, _route_argument(match.group(3), src))
                routes.append(Endpoint(match.group(2).upper(), normalize_path(route), f"{rel}:{line}"))
    return [route for route in routes if route.path[:1] == ("api",)]


# ---------------------------------------------------------------------------
# 求差
# ---------------------------------------------------------------------------


def check(root: Path, framework_dir: Path) -> tuple[list[str], dict[str, int]]:
    frontend = root / "frontend"
    for required in (frontend / "src", frontend / "_mock" / "api"):
        if not required.is_dir():
            raise InputError(f"缺少目录 {required}")
    backend = collect_backend(root / "backend" / "src", framework_dir, root)
    usage = collect_frontend(frontend / "src", root)
    mocks = collect_mock_keys(frontend / "_mock" / "api", root)
    unmocked, mock_only = collect_declarations(frontend / "_mock" / "index.ts", root)
    # 三个集合任一为空都说明解析失效（或指错了根目录），这时"零违规"不是结论
    for label, items in (("后端路由", backend), ("前端 HttpClient 调用", usage.calls), ("Mock 键", mocks)):
        if not items:
            raise InputError(f"{label}为空：解析失效或 --root 指错了目录（{root}）")

    violations: list[str] = []
    for call in usage.calls:
        if not any_covers(mocks, call):
            violations.append(f"缺 Mock：{call.label()}（{call.origin}）")
        if not any_covers(backend, call) and not any_covers(mock_only, call):
            violations.append(f"后端没有该端点：{call.label()}（{call.origin}）")
    for key in mocks:
        if not any_covers(backend, key) and not any_covers(mock_only, key):
            violations.append(f"多余 Mock，后端没有该端点：{key.label()}（{key.origin}）")
    for reference in usage.references:
        if not any_covers(mocks, reference) and not any_covers(unmocked, reference):
            violations.append(
                f"前端引用的地址既没有 Mock，也未在 _mock/index.ts 声明为刻意不 Mock：{reference.label()}（{reference.origin}）"
            )
    for entry in unmocked:
        if not any_covers(backend, entry):
            violations.append(f"清单过期：声明不 Mock 的端点后端不存在：{entry.label()}（{entry.origin}）")
        if any_covers(mocks, entry):
            violations.append(f"清单过期：声明不 Mock 的端点已有 Mock：{entry.label()}（{entry.origin}）")
    for entry in mock_only:
        if not any_covers(mocks, entry):
            violations.append(f"清单过期：声明只在 Mock 中存在的端点没有 Mock：{entry.label()}（{entry.origin}）")
        if any_covers(backend, entry):
            violations.append(f"清单过期：声明只在 Mock 中存在的端点后端已存在：{entry.label()}（{entry.origin}）")

    stats = {
        "后端路由": len({(e.method, e.path) for e in backend}),
        "HttpClient 调用": len({(e.method, e.path) for e in usage.calls}),
        "其余地址引用": len({e.path for e in usage.references}),
        "Mock 键": len({(e.method, e.path) for e in mocks}),
        "刻意不 Mock": len(unmocked),
        "只在 Mock": len(mock_only),
    }
    return sorted(set(violations)), stats


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

FIXTURE = {
    "backend/src/Demo.Api/Controllers/UserController.cs": """
[Route("api/v1/users")]
public sealed class UserController : BaseController
{
    // [HttpGet("commented-out")] 注释里的特性不算
    [HttpGet]
    public Task List() => null;

    [HttpGet("{id:guid}")]
    public Task Get(Guid id) => null;

    [HttpGet("{id:guid}/avatar")]
    public Task Avatar(Guid id) => null;
}
""",
    "backend/src/Demo.Api/Controllers/AuthController.cs": """
[Route("api/v1/auth")]
public sealed class AuthController : BaseController
{
    [HttpGet("login")]
    public Task Login() => null;

    [HttpPost("logout")]
    public Task Logout() => null;
}
""",
    "backend/src/Demo.Api/Hosting/ComponentEndpoints.cs": """
public static class ComponentEndpoints
{
    public static WebApplication MapComponentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGroup("records").MapRecords(options => options.ReadPolicy = "x");
        return app;
    }
}
""",
    "framework/components/records/Endpoints/RecordEndpoints.cs": """
public static class RecordEndpoints
{
    public static RouteGroupBuilder MapRecords(this IEndpointRouteBuilder endpoints, Action<RecordOptions> configure)
    {
        var group = endpoints.MapGroup(string.Empty);
        group.MapGet(string.Empty, () => 1);
        var route = $"grants/{Segments[name]}/{{key}}";
        group.MapGet(route, () => 1);
        group.MapGet("export", () => 1);
        return group;
    }
}
""",
    "frontend/src/app/services/user-service.ts": """
import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class UserService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/users';

  list() {
    return this.http.get<Paged<User>>(this.baseUrl, { params });
  }

  get(id: string) {
    // this.http.get('/api/v1/users/commented') 注释里的调用不算
    return this.http.get<User>(`${this.baseUrl}/${id}`);
  }
}
""",
    "frontend/src/app/services/record-service.ts": """
import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class RecordService {
  private readonly http = inject(HttpClient);

  list() {
    const pattern = /['"`]/g;
    return this.http.get('/api/v1/records');
  }

  grants(key: string) {
    return this.http.get(`/api/v1/records/grants/roles/${encodeURIComponent(key)}`);
  }

  exportAll() {
    return this.http.get(`/api/v1/records/export`, { responseType: 'blob' });
  }
}
""",
    "frontend/src/app/services/auth-service.ts": """
import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';

export class AuthService {
  private readonly http = inject(HttpClient);

  login(returnUrl: string) {
    if (this.isMockedUrl('/api/v1/auth/login')) {
      this.http.post('/api/v1/auth/login', {}).subscribe();
      return;
    }
    window.location.href = `/api/v1/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  logout() {
    form.action = '/api/v1/auth/logout';
  }
}
""",
    "frontend/src/app/app.interceptors.ts": """
export const isApi = (url: string) => url.startsWith('/api/');
""",
    "frontend/_mock/index.ts": """// 刻意不 Mock 的端点：浏览器整页跳转或经 <img> 加载。每项写成 `方法 路径`：
// - `GET /api/v1/users/{id}/avatar`：头像。
//#if (RemoteTokenAuth)
// - `GET /api/v1/auth/login`：302 到身份服务。
// 只在 Mock 中存在的端点：
// - `POST /api/v1/auth/login`：模拟登录。
//#endif
export * from './api/user';
""",
    "frontend/_mock/api/user.ts": """
export const USER_API = {
  'GET /api/v1/users': () => [],
  'GET /api/v1/users/:id': () => ({}),
  'POST /api/v1/auth/login': () => ({}),
  'POST /api/v1/auth/logout': () => ({}),
};
""",
    "frontend/_mock/api/record.ts": """
export const RECORD_API = {
  'GET /api/v1/records': () => [],
  'GET /api/v1/records/grants/roles/:key': () => ({}),
  'GET /api/v1/records/export': () => '',
};
""",
}


def _write_fixture(base: Path, overrides: dict[str, str | None]) -> tuple[Path, Path]:
    files = {**FIXTURE, **overrides}
    for rel, content in files.items():
        if content is None:
            continue
        target = base / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
    return base, base / "framework"


def _edit(rel: str, old: str, new: str) -> dict[str, str]:
    assert old in FIXTURE[rel], f"自检夹具里找不到要替换的片段：{old}"
    return {rel: FIXTURE[rel].replace(old, new)}


def self_test() -> int:
    cases: list[tuple[str, dict[str, str | None], str | None]] = [
        ("合法：三方一致、参数段写法各异、清单内的跳转与只在 Mock 中的端点", {}, None),
        (
            "缺 Mock：前端调用了组件端点而 Mock 没有键",
            _edit("frontend/_mock/api/record.ts", "  'GET /api/v1/records/export': () => '',\n", ""),
            "缺 Mock：GET /api/v1/records/export",
        ),
        (
            "缺 Mock：前端新增调用",
            _edit(
                "frontend/src/app/services/record-service.ts",
                "    return this.http.get('/api/v1/records');",
                "    this.http.get('/api/v1/records/export');\n    return this.http.delete(`/api/v1/records/${id}`);",
            ),
            "缺 Mock：DELETE /api/v1/records/{}",
        ),
        (
            "多余 Mock：后端没有该端点",
            _edit(
                "frontend/_mock/api/user.ts",
                "  'POST /api/v1/auth/logout': () => ({}),\n",
                "  'POST /api/v1/auth/logout': () => ({}),\n  'PUT /api/v1/users/:id/stale': () => ({}),\n",
            ),
            "多余 Mock，后端没有该端点：PUT /api/v1/users/{}/stale",
        ),
        (
            "浏览器跳转型端点：未在清单声明即失败",
            _edit(
                "frontend/_mock/index.ts",
                "// - `GET /api/v1/auth/login`：302 到身份服务。\n",
                "",
            ),
            "未在 _mock/index.ts 声明为刻意不 Mock：GET /api/v1/auth/login",
        ),
        (
            "只在 Mock 中的端点：未声明即判多余",
            _edit("frontend/_mock/index.ts", "// - `POST /api/v1/auth/login`：模拟登录。\n", ""),
            "多余 Mock，后端没有该端点：POST /api/v1/auth/login",
        ),
        (
            "清单过期：声明不 Mock 的端点已有 Mock",
            _edit(
                "frontend/_mock/api/user.ts",
                "  'GET /api/v1/users/:id': () => ({}),\n",
                "  'GET /api/v1/users/:id': () => ({}),\n  'GET /api/v1/users/:id/avatar': () => '',\n",
            ),
            "清单过期：声明不 Mock 的端点已有 Mock：GET /api/v1/users/{}/avatar",
        ),
        (
            "前端调用的端点后端不存在",
            {
                **_edit(
                    "frontend/src/app/services/user-service.ts",
                    "    return this.http.get<User>(`${this.baseUrl}/${id}`);",
                    "    return this.http.post<User>(`${this.baseUrl}/${id}/lock`, {});",
                ),
                **_edit(
                    "frontend/_mock/api/user.ts",
                    "  'GET /api/v1/users/:id': () => ({}),\n",
                    "  'GET /api/v1/users/:id': () => ({}),\n  'POST /api/v1/users/:id/lock': () => ({}),\n",
                ),
            },
            "后端没有该端点：POST /api/v1/users/{}/lock",
        ),
        (
            "解析不出调用地址时失败而不是放过",
            _edit(
                "frontend/src/app/services/record-service.ts",
                "    return this.http.get('/api/v1/records');",
                "    return this.http.get(buildUrl());",
            ),
            "解析不出 HttpClient 调用的地址",
        ),
        (
            "写请求经 request() 发出：按字面量方法核对，不误记为 GET 地址引用",
            _edit(
                "frontend/src/app/services/record-service.ts",
                "    return this.http.get('/api/v1/records');",
                "    this.http.get('/api/v1/records');\n    return this.http.request('DELETE', '/api/v1/users');",
            ),
            "缺 Mock：DELETE /api/v1/users",
        ),
        (
            "request() 的方法不是字面量时失败而不是放过",
            _edit(
                "frontend/src/app/services/record-service.ts",
                "    return this.http.get('/api/v1/records');",
                "    this.http.get('/api/v1/records');\n    return this.http.request(method, '/api/v1/users');",
            ),
            "解析不出 HttpClient.request 的 HTTP 方法",
        ),
        (
            "不认识的 HttpClient 调用时失败而不是放过",
            _edit(
                "frontend/src/app/services/record-service.ts",
                "    return this.http.get('/api/v1/records');",
                "    this.http.get('/api/v1/records');\n    return this.http.head('/api/v1/users');",
            ),
            "不支持的 HttpClient 调用 head()",
        ),
        (
            "后端路由为空时失败",
            {
                "backend/src/Demo.Api/Controllers/UserController.cs": None,
                "backend/src/Demo.Api/Controllers/AuthController.cs": None,
                "backend/src/Demo.Api/Hosting/ComponentEndpoints.cs": None,
                "backend/src/Demo.Api/Placeholder.cs": "public sealed class Placeholder { }",
            },
            "后端路由为空",
        ),
        (
            "前端调用为空时失败",
            {
                "frontend/src/app/services/user-service.ts": None,
                "frontend/src/app/services/record-service.ts": None,
                "frontend/src/app/services/auth-service.ts": None,
            },
            "前端 HttpClient 调用为空",
        ),
        (
            "Mock 文件读不出键时失败",
            {"frontend/_mock/api/record.ts": "export const RECORD_API = { [`GET ${base}`]: () => [] };\n"},
            "读不出任何 '方法 路径' 形式的 Mock 键",
        ),
        (
            "控制器读不出 action 时失败",
            {
                "backend/src/Demo.Api/Controllers/AuthController.cs": FIXTURE["backend/src/Demo.Api/Controllers/AuthController.cs"]
                .replace('[HttpGet("login")]', '[HttpGet(template: "login")]')
                .replace('[HttpPost("logout")]', '[HttpPost(template: "logout")]'),
            },
            "读不出任何 [HttpGet]/[HttpPost]/... action",
        ),
        (
            "缺少清单文件时失败",
            {"frontend/_mock/index.ts": None},
            "无法读取刻意不 Mock 的端点清单",
        ),
    ]
    failures = 0
    for name, overrides, expected in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root, framework = _write_fixture(Path(tmp), overrides)
            try:
                violations, _ = check(root, framework)
                messages = violations
            except InputError as error:
                messages = [f"输入错误：{error}"]
            if expected is None:
                ok = not messages
            else:
                ok = any(expected in message for message in messages)
            print(f"{'✅' if ok else '❌'} {name}")
            if not ok:
                failures += 1
                print(f"   期望：{expected or '无违规'}")
                for message in messages:
                    print(f"   实际：{message}")
    print(f"自检：{len(cases) - failures}/{len(cases)} 通过")
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT, help="模板目录或生成项目根目录（含 backend/ 与 frontend/）")
    parser.add_argument("--framework", type=Path, default=DEFAULT_FRAMEWORK, help="框架源码目录，用于解析组件端点")
    parser.add_argument("--self-test", action="store_true", help="在临时目录上跑正反用例")
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    root = args.root.resolve()
    try:
        violations, stats = check(root, args.framework.resolve())
    except InputError as error:
        print(f"❌ {error}")
        return 1
    summary = "，".join(f"{name} {count}" for name, count in stats.items())
    if violations:
        print(f"❌ Mock 覆盖：{len(violations)} 处违规（{root}）")
        for violation in violations:
            print(f"   {violation}")
        print(f"   统计：{summary}")
        return 1
    print(f"✅ Mock 覆盖：前端调用、Mock 键与后端路由一致（{summary}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
