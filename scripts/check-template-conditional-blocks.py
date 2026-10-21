#!/usr/bin/env python3
"""模板条件块的结构闸门：#if / #else / #endif 必须配平，且一个块只能有一个 #else。

为什么需要：模板引擎遇到一个块里的多个 #else 不会报错，它取第一个分支、静默丢掉其余，
于是"多余分支"里的内容既不生成也不报警。真实事故有两处：

  - landing.html：#if 之前还留着一个无条件 <h3>，块内三个 #else。本地化项目的首页
    卡片标题因此渲染两遍（"Authentication Authentication"），八个矩阵场景全绿。
  - workspace-dashboard.ts：@Component 的 imports 写了四个分支。编译通过，
    因为引擎取了第一个。

单场景编译与单测都验不到这类缺陷：生成结果本身是合法代码，只是内容错了。

符号名是否合法由 check-template-symbols.ps1 负责；本闸门只看块结构。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TEMPLATE_ROOT = REPO_ROOT / "template"

SKIP_DIRS = {"node_modules", "obj", "bin", ".angular", "dist", ".git"}
SCANNED_SUFFIXES = {
    ".ts", ".html", ".cs", ".css", ".json", ".md", ".mjs", ".js",
    ".props", ".csproj", ".slnx", ".sln", ".ps1", ".yml", ".yaml",
    # template/scripts/ 随模板分发，同样会过生成引擎：在散文里写出 `//#if` 这个记号，
    # 引擎会当成真指令，从那一行起把文件剩下的部分整个吞掉，而生成物直到运行才报语法错。
    ".py",
}

# 三种注释载体：C# 预处理指令（行首 #if）、TS/JS 的 //#if、HTML 的 <!--#if -->
DIRECTIVE = re.compile(r'^\s*(?:<!--\s*|//\s*|/\*\s*)?#(if|else|elif|endif)\b')


def check_text(text: str, origin: str) -> list[str]:
    """返回该文本里的结构问题；空列表表示通过。"""
    problems: list[str] = []
    # 每层深度记一个 #else 计数；#endif 出栈
    else_seen: dict[int, int] = {}
    depth = 0

    for lineno, line in enumerate(text.splitlines(), 1):
        match = DIRECTIVE.match(line)
        if not match:
            continue
        kind = match.group(1)

        if kind == "if":
            depth += 1
            else_seen[depth] = 0
        elif kind in ("else", "elif"):
            if depth == 0:
                problems.append(f"{origin}:{lineno} #{kind} 不在任何 #if 块内")
                continue
            if kind == "else":
                else_seen[depth] += 1
                if else_seen[depth] > 1:
                    problems.append(
                        f"{origin}:{lineno} 同一条件块内的第 {else_seen[depth]} 个 #else"
                        "——模板引擎只取第一个分支，其余静默丢弃"
                    )
        elif kind == "endif":
            if depth == 0:
                problems.append(f"{origin}:{lineno} #endif 没有对应的 #if")
                continue
            else_seen.pop(depth, None)
            depth -= 1

    if depth != 0:
        problems.append(f"{origin}: {depth} 个 #if 没有对应的 #endif")

    return problems


def self_test() -> int:
    cases: list[tuple[str, str, int]] = [
        (
            "单个 #else 合法",
            "//#if (A)\nx\n//#else\ny\n//#endif\n",
            0,
        ),
        (
            "没有 #else 合法",
            "//#if (A)\nx\n//#endif\n",
            0,
        ),
        (
            "两个 #else 报一处",
            "//#if (A)\nx\n//#else\ny\n//#else\nz\n//#endif\n",
            1,
        ),
        (
            "三个 #else 报两处",
            "//#if (A)\nx\n//#else\ny\n//#else\nz\n//#else\nw\n//#endif\n",
            2,
        ),
        (
            "HTML 注释载体同样识别",
            "<!--#if (A)-->\nx\n<!--#else-->\ny\n<!--#else-->\nz\n<!--#endif-->\n",
            1,
        ),
        (
            "C# 预处理指令同样识别",
            "#if (A)\nx\n#else\ny\n#else\nz\n#endif\n",
            1,
        ),
        (
            "嵌套块各自独立计数",
            "//#if (A)\n//#if (B)\nx\n//#else\ny\n//#endif\n//#else\nz\n//#endif\n",
            0,
        ),
        (
            "嵌套内层的重复 #else 也要抓到",
            "//#if (A)\n//#if (B)\nx\n//#else\ny\n//#else\nz\n//#endif\n//#endif\n",
            1,
        ),
        (
            "缺 #endif 要报",
            "//#if (A)\nx\n//#else\ny\n",
            1,
        ),
        (
            "多余 #endif 要报",
            "//#if (A)\nx\n//#endif\n//#endif\n",
            1,
        ),
        (
            "游离 #else 要报",
            "//#else\nx\n",
            1,
        ),
        (
            "同层两个相邻块各自允许一个 #else",
            "//#if (A)\nx\n//#else\ny\n//#endif\n//#if (B)\nz\n//#else\nw\n//#endif\n",
            0,
        ),
    ]

    failures = 0
    for name, text, expected in cases:
        actual = len(check_text(text, "<self-test>"))
        if actual != expected:
            print(f"  ❌ {name}：期望 {expected} 处问题，实际 {actual} 处")
            failures += 1
        else:
            print(f"  ✅ {name}")

    if failures:
        print(f"❌ 自检失败：{failures}/{len(cases)} 个用例不符")
        return 1
    print(f"✅ 条件块规则自检通过（{len(cases)} 个用例）")
    return 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()

    problems: list[str] = []
    scanned = 0

    for path in sorted(TEMPLATE_ROOT.rglob("*")):
        if not path.is_file():
            continue
        if SKIP_DIRS & set(path.parts):
            continue
        if path.suffix not in SCANNED_SUFFIXES:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if "#if" not in text:
            continue

        scanned += 1
        problems.extend(check_text(text, str(path.relative_to(REPO_ROOT))))

    if problems:
        print("❌ 模板条件块结构检查失败：")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    print(f"✅ 模板条件块结构检查通过（{scanned} 个含条件块的文件：#if/#endif 配平，每块至多一个 #else）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
