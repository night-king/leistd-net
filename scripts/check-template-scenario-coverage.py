#!/usr/bin/env python3
"""模板场景覆盖闸门：full 档登记场景覆盖全部可达条件行与有效能力的可达两两组合。

为什么需要：full 档是放行前唯一的全局覆盖证明（PR 只承担 plan-quality-checks.py 的增量保证）。
新增一个 `#if (IncludeNotifications && !IncludeLocalization)` 分支时，如果没有任何登记场景
生成它，矩阵本身就有洞，而 full 照样全绿。

同时检查有效能力的全部可达两两组合。更高阶交互使用登记的关键场景验证，
全部有效形态的轻量生成由 test-template-generation.py 验证。

判据按模板引擎的语义求值：文件级 modifiers（含第二个来源的 condition）、嵌套的
#if/#elif/#elseif/#else，以及 template.json 的 computed 符号。对每个条件行算出
"哪些登记场景会生成它"，要求：

  1. 至少一个 full 档场景生成它（档位参数化，自检用一个子集档锁定"子集漏测"判据）；
  2. 只要全部原始参数组合里有一种会生成它，就至少有一个登记场景生成它（否则矩阵本身有洞）。

同一套求值器也供 PR 选择器使用（generated / branch_leaves），不另写第二套。
场景与档位的唯一定义在 template-matrix-scenarios.ps1，这里经 pwsh 读取，不重抄清单。
块结构是否配平由 check-template-conditional-blocks.py 负责；本闸门假定结构合法，
branch_leaves 遇到不配平的结构直接报错（选择器据此退回 full）。
"""

from __future__ import annotations

from functools import lru_cache
import fnmatch
import itertools
import json
import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TEMPLATE_ROOT = REPO_ROOT / "template"
CONFIG_PATH = TEMPLATE_ROOT / ".template.config" / "template.json"
SCENARIOS_SCRIPT = REPO_ROOT / "scripts" / "template-matrix-scenarios.ps1"

# 与 check-template-conditional-blocks.py 同一套注释载体，另认 #elseif（模板引擎的同义写法）
DIRECTIVE = re.compile(r'^\s*(?:<!--\s*|//\s*|/\*\s*)?#(if|elseif|elif|else|endif)\b(.*)$')
EXPR_TAIL = re.compile(r'\s*(?:-->|\*/)\s*$')
TOKEN = re.compile(r'\s*(?:(\(|\)|&&|\|\||==|!=|!)|"([^"]*)"|([A-Za-z_]\w*))')


@lru_cache(maxsize=None)
def compile_condition(expr: str):
    parts: list[str] = []
    pos = 0
    expr = EXPR_TAIL.sub("", expr).strip()
    while pos < len(expr):
        match = TOKEN.match(expr, pos)
        if not match:
            raise ValueError(f"无法解析条件表达式：{expr!r}")
        op, literal, name = match.groups()
        if op:
            parts.append({"&&": " and ", "||": " or ", "!": " not "}.get(op, op))
        elif literal is not None:
            parts.append(repr(literal))
        elif name in ("true", "false"):
            parts.append(name.capitalize())
        else:
            parts.append(f"values.get({name!r}, False)")
        pos = match.end()
    return compile("".join(parts).strip(), '<template-condition>', 'eval')


def evaluate(expr: str, values: dict[str, object]) -> bool:
    """按模板引擎语义求值；仅复用进程内编译，未知符号仍为 false。"""
    return bool(eval(compile_condition(expr), {"__builtins__": {}}, {"values": values}))


def symbol_values(config: dict, arguments: dict[str, object]) -> dict[str, object]:
    symbols = config["symbols"]
    values: dict[str, object] = {}
    for name, symbol in symbols.items():
        if symbol.get("type") != "parameter":
            continue
        default = symbol.get("defaultValue")
        if symbol.get("datatype") == "bool":
            default = str(default).lower() == "true"
        values[name] = arguments.get(name, default)
    # computed 可以引用其他 computed，按声明顺序求值直到稳定
    for _ in range(len(symbols)):
        for name, symbol in symbols.items():
            if symbol.get("type") == "computed":
                values[name] = evaluate(symbol["value"], values)
    return values


def all_combinations(config: dict) -> list[dict[str, object]]:
    choices: list[tuple[str, list[object]]] = []
    for name, symbol in config["symbols"].items():
        if symbol.get("type") != "parameter":
            continue
        if symbol.get("datatype") == "bool":
            choices.append((name, [False, True]))
        elif symbol.get("datatype") == "choice":
            choices.append((name, [c["choice"] for c in symbol["choices"]]))
    return [
        symbol_values(config, dict(zip([n for n, _ in choices], combo)))
        for combo in itertools.product(*[v for _, v in choices])
    ]


EFFECTIVE_DIMENSIONS = (
    "ServiceRole", "SpaFrontend", "IncludeMultiTenancy", "IncludeRealTime", "Email",
    "IncludeOperationRecords", "IncludeNotifications", "ExternalLogin", "IncludeLocalization",
)


def effective_shape(values: dict[str, object]) -> tuple:
    return tuple(values[name] for name in EFFECTIVE_DIMENSIONS)


def pairwise_problems(config: dict, scenarios: dict, dimensions=EFFECTIVE_DIMENSIONS, tier: str = "full") -> list[str]:
    combinations = all_combinations(config)
    selected = [symbol_values(config, parse_cli_arguments(config, info["Arguments"]))
                for info in scenarios.values() if tier in info["Slices"]]
    problems = []
    for left, right in itertools.combinations(dimensions, 2):
        reachable = {(v[left], v[right]) for v in combinations}
        covered = {(v[left], v[right]) for v in selected}
        for pair in sorted(reachable - covered, key=str):
            problems.append(f"{tier} 档缺少有效能力两两组合：{left}={pair[0]}, {right}={pair[1]}")
    return problems


def parse_cli_arguments(config: dict, args: list[str]) -> dict[str, object]:
    """把 dotnet new 参数（--service-role Resource、--include-notifications）映射回符号。"""
    by_long_name: dict[str, str] = {}
    for name, symbol in config["symbols"].items():
        if symbol.get("type") == "parameter":
            kebab = re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()
            by_long_name[f"--{kebab}"] = name
    result: dict[str, object] = {}
    index = 0
    while index < len(args):
        flag = args[index]
        if flag not in by_long_name:
            raise ValueError(f"场景参数 {flag} 不对应 template.json 中的任何参数")
        name = by_long_name[flag]
        symbol = config["symbols"][name]
        if symbol.get("datatype") == "bool":
            if index + 1 < len(args) and args[index + 1] in ("true", "false"):
                result[name] = args[index + 1] == "true"
                index += 2
            else:
                result[name] = True
                index += 1
        else:
            result[name] = args[index + 1]
            index += 2
    return result


def glob_match(path: str, pattern: str) -> bool:
    """模板引擎的 glob：`**/` 可以匹配零层目录。"""
    return fnmatch.fnmatch(path, pattern) or (pattern.startswith("**/") and fnmatch.fnmatch(path, pattern[3:]))


def iter_sources(config: dict, files: list[str]):
    """按 template.json 的 sources 产出 (相对来源根的路径, 仓库内路径, 来源条件, modifiers)。"""
    for source in config["sources"]:
        root = (source.get("source") or "./").removeprefix("./").rstrip("/")
        prefix = f"template/{root}/" if root else "template/"
        excludes = source.get("exclude", [])
        for path in files:
            if not path.startswith(prefix):
                continue
            relative = path[len(prefix):]
            if any(glob_match(relative, pattern) for pattern in excludes):
                continue
            yield relative, path, source.get("condition"), source.get("modifiers", [])


def line_contexts(text: str):
    """逐行产出 (行号, 条件帧)；帧为 (当前分支表达式, 此前同块分支表达式列表)。"""
    stack: list[list] = []
    for lineno, line in enumerate(text.splitlines(), 1):
        match = DIRECTIVE.match(line)
        if match:
            kind, expr = match.groups()
            if kind == "if":
                stack.append([expr, []])
            elif kind in ("elif", "elseif") and stack:
                stack[-1][1].append(stack[-1][0])
                stack[-1][0] = expr
            elif kind == "else" and stack:
                stack[-1][1].append(stack[-1][0])
                stack[-1][0] = "true"
            elif kind == "endif" and stack:
                stack.pop()
            continue
        yield lineno, tuple((current, tuple(previous)) for current, previous in stack)


def file_conditions(relative: str, modifiers: list) -> list[str]:
    """文件级 modifiers 中命中该文件的排除条件：任一为真，该文件即被裁掉。"""
    return [
        modifier["condition"]
        for modifier in modifiers
        if any(glob_match(relative, pattern) for pattern in modifier.get("exclude", []))
    ]


def generated(values: dict[str, object], frames, source_condition, excludes) -> bool:
    """按模板引擎语义判断一组取值是否生成某一上下文：来源条件、文件级排除，再逐层分支条件。"""
    if source_condition and not evaluate(source_condition, values):
        return False
    if any(evaluate(condition, values) for condition in excludes):
        return False
    return all(
        evaluate(current, values) and not any(evaluate(p, values) for p in previous)
        for current, previous in frames
    )


def branch_leaves(text: str, changed_lines) -> list[tuple]:
    """被改动的顶层条件块里每一臂（含嵌套块、每个 elif 臂、没有 #else 时的隐式空臂）的条件帧。

    顶层块的行区间（#if 到 #endif）含任一改动行即视为被改动；帧与 line_contexts 同形，
    可直接交给 generated() 求值。结构不配平时报错，调用方据此退回完整验证。
    """
    changed = set(changed_lines)
    leaves: list[tuple] = []
    stack: list[dict] = []
    top_start = None

    def close_arm(block):
        frames = tuple(block["parents"]) + ((block["current"], tuple(block["previous"])),)
        block["arms"].append(frames)

    for lineno, line in enumerate(text.splitlines(), 1):
        match = DIRECTIVE.match(line)
        if not match:
            continue
        kind, expr = match.groups()
        if kind == "if":
            parents = []
            for outer in stack:
                parents.append((outer["current"], tuple(outer["previous"])))
            if not stack:
                top_start = lineno
            stack.append({"parents": parents, "current": EXPR_TAIL.sub("", expr).strip(), "previous": [],
                          "arms": [], "explicit_else": False})
        elif kind in ("elif", "elseif", "else"):
            if not stack or stack[-1]["explicit_else"]:
                raise ValueError(f"第 {lineno} 行的 #{kind} 没有对应的 #if")
            block = stack[-1]
            close_arm(block)
            block["previous"].append(block["current"])
            block["current"] = "true" if kind == "else" else EXPR_TAIL.sub("", expr).strip()
            block["explicit_else"] = kind == "else"
        else:
            if not stack:
                raise ValueError(f"第 {lineno} 行的 #endif 没有对应的 #if")
            block = stack.pop()
            close_arm(block)
            if not block["explicit_else"]:
                block["previous"].append(block["current"])
                block["current"] = "true"
                close_arm(block)
            if stack:
                stack[-1].setdefault("children", []).extend(block["arms"] + block.get("children", []))
            elif changed & set(range(top_start, lineno + 1)):
                leaves.extend(block["arms"] + block.get("children", []))
    if stack:
        raise ValueError("条件块没有闭合")
    return leaves


def coverage_problems(
    config: dict,
    files: dict[str, str],
    scenarios: dict[str, dict],
    tier: str = "full",
) -> tuple[list[str], int]:
    """返回 (问题列表, 检查过的条件行数)。files 为 仓库内路径 → 文本。"""
    scenario_values = {
        name: symbol_values(config, parse_cli_arguments(config, info["Arguments"]))
        for name, info in scenarios.items()
    }
    tier_scenarios = {name for name, info in scenarios.items() if tier in info["Slices"]}
    combinations = all_combinations(config)

    problems: list[str] = []
    checked = 0
    for relative, path, source_condition, modifiers in iter_sources(config, sorted(files)):
        excludes = file_conditions(relative, modifiers)
        for lineno, frames in line_contexts(files[path]):
            if not frames and not excludes and not source_condition:
                continue
            checked += 1
            producers = {name for name, values in scenario_values.items() if generated(values, frames, source_condition, excludes)}
            if not producers:
                if any(generated(values, frames, source_condition, excludes) for values in combinations):
                    problems.append(f"{path}:{lineno} 有参数组合会生成这一行，但没有任何登记场景生成它")
            elif not producers & tier_scenarios:
                problems.append(
                    f"{path}:{lineno} 只由 {tier} 档以外的场景生成（{', '.join(sorted(producers))}）；"
                    f"调整 {tier} 档场景或改写条件"
                )
    return problems, checked


def load_scenarios() -> dict[str, dict]:
    command = (
        f". '{SCENARIOS_SCRIPT}'; "
        "$AllScenarios | ForEach-Object { [ordered]@{ Name = $_; Arguments = @($scenarioMap[$_].Arguments); "
        "Slices = $scenarioMap[$_].Slices; Frontend = $scenarioMap[$_].Frontend; Containers = $ContainerScenarios; Titles = $MatrixSlices } } | ConvertTo-Json -Depth 5 -AsArray"
    )
    completed = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", command],
        capture_output=True, text=True, encoding="utf-8",
    )
    if completed.returncode != 0:
        raise SystemExit(f"❌ 读取场景定义失败：\n{completed.stderr.strip() or completed.stdout.strip()}")
    return {item["Name"]: item for item in json.loads(completed.stdout)}


def self_test() -> int:
    config = {
        "symbols": {
            "Role": {"type": "parameter", "datatype": "choice", "defaultValue": "A",
                     "choices": [{"choice": "A"}, {"choice": "B"}]},
            "X": {"type": "parameter", "datatype": "bool", "defaultValue": "false"},
            "Y": {"type": "parameter", "datatype": "bool", "defaultValue": "false"},
            "IsA": {"type": "computed", "value": '(Role == "A")'},
        },
        "sources": [
            {"exclude": ["**/.template.config/**"], "modifiers": [{"condition": "(!X)", "exclude": ["only-x/**"]}]},
            {"source": "./.template.config/y", "target": "./", "condition": "Y"},
        ],
    }
    # 判据按档位参数化：用一个子集档 sub（全关、全开、b-role）锁定"只由子集外场景生成"的判据；
    # 生产调用取 full。x-only 只在 full。
    scenarios = {
        "base": {"Arguments": [], "Slices": {"full": "a", "sub": "a"}},
        "all": {"Arguments": ["--x", "--y"], "Slices": {"full": "a", "sub": "a"}},
        "x-only": {"Arguments": ["--x"], "Slices": {"full": "b"}},
        "b-role": {"Arguments": ["--role", "B"], "Slices": {"full": "b", "sub": "b"}},
    }
    cases: list[tuple[str, dict[str, str], int]] = [
        ("单符号两侧都由子集场景生成", {"template/a.ts": "//#if (X)\nx\n//#else\nnot x\n//#endif\n"}, 0),
        ("X 开 Y 关只由 x-only 生成 → 红", {"template/a.ts": "//#if (X && !Y)\nx\n//#endif\n"}, 1),
        ("嵌套等价于 X 开 Y 关 → 红", {"template/a.cs": "#if (X)\n#if (!Y)\nx\n#endif\n#endif\n"}, 1),
        ("#else 分支取反后由 x-only 唯一生成 → 红", {"template/a.html": "<!--#if (!X || Y) -->\na\n<!--#else -->\nb\n<!--#endif -->\n"}, 1),
        ("#elseif 之后的分支排除前面的条件", {"template/a.cs": "#if (X && Y)\na\n#elseif (X)\nb\n#endif\n"}, 1),
        ("文件级 modifier：only-x 下的 !Y 行只在 x-only → 红", {"template/only-x/f.ts": "//#if (!Y)\nz\n//#endif\n"}, 1),
        ("文件级 modifier：only-x 下的无条件行由 all 生成", {"template/only-x/f.ts": "plain\n"}, 0),
        ("第二来源的 condition 生效：Y 下的 !X 行无人生成但组合可达 → 红",
         {"template/.template.config/y/g.ts": "//#if (!X)\nq\n//#endif\n"}, 1),
        ("computed 符号求值", {"template/a.ts": "//#if (!IsA && X)\nb\n//#endif\n"}, 1),
        ("任何组合都不生成的行不报", {"template/a.ts": "//#if (X && !X)\nnever\n//#endif\n"}, 0),
        ("Role 取 B 的行由 b-role 生成", {"template/a.ts": "//#if (!IsA)\nb\n//#endif\n"}, 0),
    ]
    failures = 0
    for name, files, expected in cases:
        problems, _ = coverage_problems(config, files, scenarios, tier="sub")
        ok = len(problems) == expected
        failures += 0 if ok else 1
        print(f"  {'✅' if ok else '❌'} {name}" + ("" if ok else f"：期望 {expected} 个问题，实际 {problems}"))
    # 行覆盖全绿仍可漏掉 X=false/Y=true；组合规则须独立发现它。
    pair_scenarios = dict(scenarios, x_only={"Arguments": ["--x"], "Slices": {"sub": "b"}})
    missing = pairwise_problems(config, pair_scenarios, ('X', 'Y'), tier="sub")
    complete = dict(pair_scenarios, y_only={"Arguments": ["--y"], "Slices": {"sub": "b"}})
    # full 档的生产判据：x-only 只在 full 时，同一条 X 开 Y 关的行在 full 上是通过的
    full_ok, _ = coverage_problems(config, {"template/a.ts": "//#if (X && !Y)\nx\n//#endif\n"}, scenarios)
    # 选择器复用的块结构：嵌套叶子、elif 各臂、隐式空 else，只报被改动的顶层块
    nested = "a\n#if (X)\nb\n#if (Y)\nc\n#elif (IsA)\nd\n#endif\n#endif\ne\n#if (Y)\nf\n#endif\n"
    leaves = branch_leaves(nested, [5])
    leaf_tail = sorted(frames[-1][0] for frames in leaves)
    untouched = branch_leaves(nested, [1, 13])
    try:
        branch_leaves("#if (X)\nx\n", [1])
        unbalanced = False
    except ValueError:
        unbalanced = True
    for name, ok in (
        ("两两组合发现行覆盖未发现的缺口", len(missing) == 1 and 'X=False, Y=True' in missing[0]),
        ("补齐两两组合后通过", not pairwise_problems(config, complete, ('X', 'Y'), tier="sub")),
        ("computed 不要求不可达取值", not pairwise_problems(config, complete, ('Role', 'IsA'), tier="sub")),
        ("full 档：只由 x-only 生成的行不再报", not full_ok),
        ("改动块的叶子：外层两臂、内层 if/elif 与隐式空臂", len(leaves) == 5 and leaf_tail.count("true") == 2
         and any(len(f) == 2 and f[-1] == ("(IsA)", ("(Y)",)) for f in leaves)),
        ("未改动的块不报，块外改动行不算改块", len(branch_leaves(nested, [11])) == 2 and not branch_leaves(nested, [1]) and len(untouched) == 2),
        ("不配平的块结构报错", unbalanced),
    ):
        failures += int(not ok)
        print(f"  {'✅' if ok else '❌'} {name}")
    if failures:
        print(f"❌ 场景覆盖规则自检失败：{failures}/{len(cases) + 7} 个用例不符")
        return 1
    print(f"✅ 场景覆盖规则自检通过（{len(cases) + 7} 个用例）")
    return 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()

    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    listed = subprocess.run(
        ["git", "-C", str(REPO_ROOT), "-c", "core.quotepath=off", "ls-files", "--cached", "--others", "--exclude-standard", "--", "template"],
        check=True, capture_output=True, text=True, encoding="utf-8",
    ).stdout.splitlines()
    files: dict[str, str] = {}
    for path in listed:
        if "/node_modules/" in path:
            continue
        try:
            files[path] = (REPO_ROOT / path).read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue

    scenarios = load_scenarios()
    problems, checked = coverage_problems(config, files, scenarios)
    problems.extend(pairwise_problems(config, scenarios))
    for name, info in scenarios.items():
        values = symbol_values(config, parse_cli_arguments(config, info["Arguments"]))
        if info["Frontend"] != values["SpaFrontend"]:
            problems.append(f"{name} 的 Frontend 阶段适用性与有效 SpaFrontend 不一致")
    if problems:
        print("❌ 模板场景覆盖检查失败：")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    full = [name for name, info in scenarios.items() if "full" in info["Slices"]]
    print(f"✅ 模板场景覆盖检查通过（{checked} 个条件行；full 档 {len(full)} 个登记场景覆盖全部可达条件行及有效能力两两组合）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
