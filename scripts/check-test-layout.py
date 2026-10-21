#!/usr/bin/env python3
"""framework/tests/ 的布局闸门。

测试目录镜像源码目录之后，"哪个家族没有测试"变成一个可机械判定的问题——
这是覆盖率报告发现不了的那一类缺口：程序集从未被任何测试加载时，它根本不出现在
报告里，任何百分比门槛都对它无效。

六条规则：

1. **路径形状精确。** 测试项目只能是下面三种之一，多一层少一层都不行：
   `tests/components/<家族>/<项目>/<项目>.csproj`、`tests/ddd-struct/<项目>/<项目>.csproj`、
   `tests/shared/<项目>/<项目>.csproj`。只判断"在某个根之下"会让散在
   `tests/components/` 根上或藏在更深层级的项目跳过后面所有家族校验。
2. **每个有可覆盖代码的组件家族都要有测试目录，且目录里真有 `*.Tests.csproj`。**
   例外写进 WAIVERS 并附理由；只有接口、特性与常量的家族自动豁免。
3. **家族名与项目名双向对应。** `tests/components/lock/` 下只能放 `Leistd.Lock.Tests`；
   反过来 `tests/components/<家族>/` 也必须有同名的 `components/<家族>/`——
   只做单向检查时，一个拼错的家族目录会静默长成第二棵没人镜像的树。
4. **每个测试项目都必须在 `Leistd.Framework.slnx` 里。** 不在解决方案里的测试项目
   不会被 `dotnet test` 跑到——它存在、能编译、看起来一切正常，但从未运行过。

5. **项目内子目录名有限。** 测试项目（`tests/components/**`、`tests/ddd-struct/**`）里的目录只能是
   包后缀（`Leistd.Lock.Tests/Redis/` 对应 `Leistd.Lock.Redis`；多段后缀逐段嵌套，
   `Leistd.Notifications.Tests/AspNetCore/SignalR/` 对应 `Leistd.Notifications.AspNetCore.SignalR`），
   或 `Contracts`、`TestDoubles`、`TestResources`、`EndToEnd` 之一（可以在包后缀目录之下，其内部不再约束）。
   目录按包切分时，读者能从路径直接找到被测包；随手起的 `Helpers/`、`Misc/` 让这层对应失效。
6. **替身放在 `TestDoubles/`。** 文件名以 `Fake` 开头或含 `Doubles` 的文件必须在某个 `TestDoubles/` 之下。

另外，项目根目录直接放的文件超过 10 个时**只报告、不失败**：提示考虑按包后缀分目录，是否拆由评审决定。

**本闸门只保证到"家族"这一级**：家族有测试项目、项目名对得上、已登记进解决方案。
它**不能**证明家族内每个发布包都被引用或加载——那需要读覆盖率或解析引用图，
属于按需体检（见 `framework/build/coverage.runsettings`），不做机械门禁。

`--self-test` 在临时目录上跑一组正反用例，验证规则本身仍然有效——
规则失效时，紧随其后那次在真实目录上的"通过"没有意义。

退出码非 0 表示存在违规，供 CI 阻断。
"""
from __future__ import annotations

import re
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# 有可覆盖代码、但刻意不建独立测试目录的家族。值是理由，会原样出现在失败信息里。
WAIVERS = {
    "aop": "仅 BaseAsyncInterceptor，1 行可覆盖代码，由 dependency-injection 的拦截器编织用例连带覆盖",
}

BRANCHES = ("components", "ddd-struct", "shared")

# 不按包后缀、而按用途划分的子目录：契约用例、替身、测试资源、端到端
PURPOSE_DIRS = {"Contracts", "TestDoubles", "TestResources", "EndToEnd"}
DOUBLE_FILE = re.compile(r"^(Fake\w*|\w*Doubles\w*)\.cs$")
ROOT_FILE_REPORT_THRESHOLD = 10


def has_executable_code(family_dir: Path) -> bool:
    """家族里是否存在方法体——只有接口/特性/常量的家族没有可覆盖行，自动豁免。"""
    for cs in family_dir.rglob("*.cs"):
        if any(part in {"obj", "bin"} for part in cs.parts):
            continue
        text = cs.read_text(encoding="utf-8")
        # 方法体、表达式主体成员、构造函数——任一出现即认为有可执行代码
        if re.search(r"=>\s*[^;{]+;", text) or re.search(r"\)\s*\n?\s*\{", text):
            return True
    return False


def family_prefixes(family_dir: Path) -> set[str]:
    """该家族允许的测试项目名前缀。

    取家族内全部包名的**最长公共点分段前缀**，加上各包完整名。
    不能简单地"砍掉最后一段"：`Leistd.Authorization.Core` 砍掉 `.Core` 得到的
    `Leistd.Authorization` 是对的，`Leistd.AspNetCore.SignalR` 砍掉 `.SignalR`
    得到的 `Leistd.AspNetCore` 却不对应任何包。也不能取前两段：那会让
    `authorization-data-scope` 家族接受 `Leistd.Authorization.Tests`。

    只有一个包的家族，公共前缀就是它自己；此时额外允许剥掉结尾的 `.Core`——
    `.Core` 在本框架里是"同一领域的抽象核心"这个打包标记（见 development-guide §1），
    其余后缀都是并列实现，必须保留。
    """
    names = [c.stem for c in family_dir.glob("*/*.csproj")]
    if not names:
        return set()

    allowed = set(names)

    segments = [n.split(".") for n in names]
    common: list[str] = []
    for parts in zip(*segments):
        if len(set(parts)) != 1:
            break
        common.append(parts[0])
    if common:
        allowed.add(".".join(common))

    if len(names) == 1 and names[0].endswith(".Core"):
        stripped = names[0][: -len(".Core")]
        if stripped != "Leistd":          # Leistd.Core 剥完只剩 Leistd，不是家族前缀
            allowed.add(stripped)

    return allowed


def package_suffix_paths(project_stem: str, package_names: list[str]) -> set[tuple[str, ...]]:
    """包后缀对应的目录路径及其各级前缀：`Leistd.Notifications` + `.AspNetCore.SignalR` → (AspNetCore,)、(AspNetCore, SignalR)。"""
    paths: set[tuple[str, ...]] = set()
    for name in package_names:
        if not name.startswith(project_stem + "."):
            continue
        segments = tuple(name[len(project_stem) + 1:].split("."))
        paths.update(segments[:i] for i in range(1, len(segments) + 1))
    return paths


def subdirectory_problems(csproj: Path, package_names: list[str], framework: Path) -> list[str]:
    """规则 5、6：项目内子目录名与替身位置。"""
    project_dir = csproj.parent
    stem = csproj.stem.removesuffix(".Tests")
    allowed = package_suffix_paths(stem, package_names)
    problems: list[str] = []
    for path in sorted(project_dir.rglob("*")):
        rel = path.relative_to(project_dir).parts
        if {"bin", "obj"} & set(rel):
            continue
        shown = path.relative_to(framework.parent).as_posix()
        if path.is_dir():
            purpose = next((i for i, part in enumerate(rel) if part in PURPOSE_DIRS), None)
            if purpose is not None:
                ok = purpose == 0 or rel[:purpose] in allowed
            else:
                ok = rel in allowed
            # 只报最外层的违规目录，它下面的子目录不重复报
            parent_ok = len(rel) == 1 or _dir_allowed(rel[:-1], allowed)
            if not ok and parent_ok:
                choices = sorted("/".join(p) for p in allowed if len(p) == len(rel) and p[:-1] == rel[:-1])
                problems.append(
                    f"{shown}/: 子目录名不符——只能是包后缀（可选：{', '.join(choices) or '无'}）"
                    f"或 {', '.join(sorted(PURPOSE_DIRS))} 之一"
                )
        elif DOUBLE_FILE.match(path.name) and "TestDoubles" not in rel[:-1]:
            problems.append(f"{shown}: 替身文件必须放在 TestDoubles/ 下")
    return problems


def _dir_allowed(rel: tuple[str, ...], allowed: set[tuple[str, ...]]) -> bool:
    purpose = next((i for i, part in enumerate(rel) if part in PURPOSE_DIRS), None)
    if purpose is not None:
        return purpose == 0 or rel[:purpose] in allowed
    return rel in allowed


def root_file_reports(framework: Path) -> list[str]:
    """项目根目录文件数超过阈值的测试项目：只报告，不失败。"""
    reports = []
    for csproj in sorted((framework / "tests").rglob("*.Tests.csproj")):
        if any(part in {"obj", "bin"} for part in csproj.parts):
            continue
        count = sum(1 for f in csproj.parent.iterdir() if f.is_file())
        if count > ROOT_FILE_REPORT_THRESHOLD:
            reports.append(f"{csproj.parent.relative_to(framework.parent).as_posix()}/ 根目录有 {count} 个文件")
    return reports


def check(framework: Path) -> list[str]:
    """对一棵 framework/ 目录树执行全部规则，返回违规列表。"""
    components = framework / "components"
    tests = framework / "tests"
    problems: list[str] = []

    slnx_path = framework / "Leistd.Framework.slnx"
    slnx = slnx_path.read_text(encoding="utf-8") if slnx_path.exists() else ""

    # 单趟遍历全部测试 csproj：形状、登记、家族归属一次判完。
    # 分两趟（先按"是否在某个根之下"过一遍、再用 glob 取家族项目）会留下一个缝——
    # 形状不合法的项目通不过 glob，于是把后面所有校验都跳过了。
    components_projects: list[tuple[str, Path]] = []      # (family, csproj)
    for csproj in sorted(tests.rglob("*.csproj")):
        if any(part in {"obj", "bin"} for part in csproj.parts):
            continue
        rel = csproj.relative_to(tests)
        parts = rel.parts
        shown = csproj.relative_to(framework.parent).as_posix()

        if not parts or parts[0] not in BRANCHES:
            problems.append(
                f"{shown}: 测试项目必须落在 tests/components/<家族>/、tests/ddd-struct/ "
                f"或 tests/shared/ 之下"
            )
            continue

        branch = parts[0]
        expected_depth = 4 if branch == "components" else 3
        shape = ("components/<家族>/<项目>/<项目>.csproj"
                 if branch == "components" else f"{branch}/<项目>/<项目>.csproj")
        if len(parts) != expected_depth or csproj.stem != csproj.parent.name:
            problems.append(f"{shown}: 路径形状不符——必须是 tests/{shape}")
            continue

        if f'"tests/{rel.as_posix()}"' not in slnx:
            problems.append(
                f"{shown}: 未登记到 Leistd.Framework.slnx——"
                f"dotnet test 跑不到它，而它能编译、看起来一切正常"
            )

        if branch == "components":
            components_projects.append((parts[1], csproj))

    # 规则 5、6：子目录名与替身位置（按家族或 ddd-struct 的包名求后缀）
    for family, csproj in components_projects:
        family_dir = components / family
        if family_dir.is_dir():
            names = [c.stem for c in family_dir.glob("*/*.csproj")]
            problems.extend(subdirectory_problems(csproj, names, framework))
    ddd_tests = tests / "ddd-struct"
    if ddd_tests.is_dir():
        ddd_names = [c.stem for c in (framework / "ddd-struct").glob("*/*.csproj")]
        for csproj in sorted(ddd_tests.glob("*/*.Tests.csproj")):
            problems.extend(subdirectory_problems(csproj, ddd_names, framework))

    # 规则 3 正向：家族名与项目名对上
    for family, csproj in components_projects:
        shown = csproj.relative_to(framework.parent).as_posix()
        family_dir = components / family
        if not family_dir.is_dir():
            continue                      # 反向镜像检查会单独报，不重复
        if not csproj.stem.endswith(".Tests"):
            problems.append(f"{shown}: 测试项目名必须以 .Tests 结尾")
            continue
        stem = csproj.stem[: -len(".Tests")]
        prefixes = family_prefixes(family_dir)
        if stem not in prefixes:
            problems.append(
                f"{shown}: 项目名与家族对不上——'{stem}' 不是 {family} 下任何一个包的前缀"
                f"（可选：{', '.join(sorted(prefixes))}）。测试项目名不得发明包名段"
            )

    # 规则 2：家族齐全且非空
    families = sorted(p for p in components.iterdir() if p.is_dir()) if components.is_dir() else []
    for family_dir in families:
        family = family_dir.name
        test_dir = tests / "components" / family

        if not test_dir.is_dir():
            if family in WAIVERS or not has_executable_code(family_dir):
                continue
            problems.append(
                f"framework/components/{family}/ 有可覆盖代码但没有 "
                f"framework/tests/components/{family}/——补测试项目，"
                f"或把豁免理由写进 scripts/check-test-layout.py 的 WAIVERS"
            )
            continue

        if family in WAIVERS:
            problems.append(
                f"framework/tests/components/{family}/ 已存在，但该家族仍在 WAIVERS 里"
                f"（理由：{WAIVERS[family]}）——删掉那条豁免"
            )

        if not any(f == family and c.stem.endswith(".Tests") for f, c in components_projects):
            problems.append(
                f"framework/tests/components/{family}/ 里没有形状合法的 *.Tests.csproj——"
                f"目录存在不等于有测试"
            )

    # 规则 3 反向：测试家族必须有同名组件家族
    test_components = tests / "components"
    if test_components.is_dir():
        for test_family in sorted(p for p in test_components.iterdir() if p.is_dir()):
            if not (components / test_family.name).is_dir():
                problems.append(
                    f"framework/tests/components/{test_family.name}/ 没有对应的 "
                    f"framework/components/{test_family.name}/——镜像是双向的，"
                    f"拼错的家族目录会静默长成第二棵没人镜像的树"
                )

    if not (tests / "ddd-struct").is_dir():
        problems.append("缺少 framework/tests/ddd-struct/——DDD 基座的测试与组件测试是并列的一支")

    return problems


# --- 自检 ---------------------------------------------------------------
# 每条反例都对应一次真实审查里发现的漏口，固化在这里防止规则回退。
#
# 反例必须**点名它针对的规则**（expect 是该规则诊断里的判别性片段），不能只断言
# "有违规"：这些场景往往同时踩中多条规则（例如一个形状非法的项目，必然也让它所在
# 家族"没有合法的 .Tests 项目"），只看 problems 非空时，把目标规则整条删掉自检照样绿。
#
# (说明, 组件包相对路径, 测试 csproj 相对 tests/ 的路径, 是否登记 slnx, 期望命中的诊断片段/None 表示应通过)
CASES: list[tuple] = [
    ("正例：家族名与项目名一致",
     ["lock/Leistd.Lock.Core", "lock/Leistd.Lock.Redis"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, None),
    ("正例：单包 .Core 家族接受剥离形态",
     ["authorization-data-scope/Leistd.Authorization.DataScope.Core"],
     "components/authorization-data-scope/Leistd.Authorization.DataScope.Tests/"
     "Leistd.Authorization.DataScope.Tests.csproj", True, None),
    ("反例：借用上级领域名",
     ["authorization-data-scope/Leistd.Authorization.DataScope.Core"],
     "components/authorization-data-scope/Leistd.Authorization.Tests/Leistd.Authorization.Tests.csproj",
     True, "项目名与家族对不上"),
    ("反例：发明不存在的包名段",
     ["aspnetcore-signalr/Leistd.AspNetCore.SignalR"],
     "components/aspnetcore-signalr/Leistd.AspNetCore.Tests/Leistd.AspNetCore.Tests.csproj",
     True, "项目名与家族对不上"),
    ("反例：测试家族没有对应组件家族",
     ["lock/Leistd.Lock.Core"],
     "components/foo/Leistd.Foo.Tests/Leistd.Foo.Tests.csproj", True, "没有对应的"),
    ("反例：csproj 直接散在 components/ 根上",
     ["lock/Leistd.Lock.Core"],
     "components/Leistd.Lock.Tests.csproj", True, "路径形状不符"),
    ("反例：真实家族下多一层嵌套",
     ["lock/Leistd.Lock.Core"],
     "components/lock/deep/nested/Leistd.Authorization.Tests.csproj", True, "路径形状不符"),
    ("反例：项目目录名与项目名不一致",
     ["lock/Leistd.Lock.Core"],
     "components/lock/TestsFolder/Leistd.Lock.Tests.csproj", True, "路径形状不符"),
    ("反例：未登记到 .slnx",
     ["lock/Leistd.Lock.Core"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", False, "未登记到"),
    ("反例：项目名不以 .Tests 结尾",
     ["lock/Leistd.Lock.Core"],
     "components/lock/Leistd.Lock.Fixtures/Leistd.Lock.Fixtures.csproj", True, "必须以 .Tests 结尾"),
    ("反例：有可覆盖代码的家族完全没有测试目录",
     ["lock/Leistd.Lock.Core"],
     "shared/Leistd.TestBase/Leistd.TestBase.csproj", True, "有可覆盖代码但没有"),
    ("反例：已建测试目录却仍留着豁免",
     ["aop/Leistd.DynamicProxy"],
     "components/aop/Leistd.DynamicProxy.Tests/Leistd.DynamicProxy.Tests.csproj", True, "仍在 WAIVERS 里"),
    # 规则 5、6：第 6 项是测试项目内的附加文件（相对项目目录）
    ("正例：包后缀目录、用途目录与包后缀下的替身目录",
     ["lock/Leistd.Lock.Core", "lock/Leistd.Lock.Memory", "lock/Leistd.Lock.Redis"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, None,
     ["Memory/A.cs", "Redis/B.cs", "Contracts/C.cs", "TestDoubles/FakeLock.cs", "Redis/TestDoubles/FakeRedis.cs",
      "TestResources/Good/en.json", "EndToEnd/D.cs"]),
    ("正例：多段包后缀逐段嵌套",
     ["notifications/Leistd.Notifications.Core", "notifications/Leistd.Notifications.AspNetCore",
      "notifications/Leistd.Notifications.AspNetCore.SignalR"],
     "components/notifications/Leistd.Notifications.Tests/Leistd.Notifications.Tests.csproj", True, None,
     ["AspNetCore/A.cs", "AspNetCore/SignalR/B.cs"]),
    ("例外：根目录文件多只报告不失败",
     ["lock/Leistd.Lock.Core"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, None,
     [f"Case{i}Tests.cs" for i in range(12)]),
    ("反例：不是包后缀也不是用途目录",
     ["lock/Leistd.Lock.Core", "lock/Leistd.Lock.Redis"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, "子目录名不符", ["Helpers/A.cs"]),
    ("反例：包后缀目录下嵌套非后缀目录",
     ["lock/Leistd.Lock.Core", "lock/Leistd.Lock.Redis"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, "子目录名不符", ["Redis/Deep/A.cs"]),
    ("反例：单包家族没有包后缀可用",
     ["aspnetcore-signalr/Leistd.AspNetCore.SignalR"],
     "components/aspnetcore-signalr/Leistd.AspNetCore.SignalR.Tests/Leistd.AspNetCore.SignalR.Tests.csproj",
     True, "子目录名不符", ["SignalR/A.cs"]),
    ("反例：Fake 替身散在根目录",
     ["lock/Leistd.Lock.Core"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, "替身文件必须放在 TestDoubles/", ["FakeClock.cs"]),
    ("反例：*Doubles* 文件放在包后缀目录",
     ["lock/Leistd.Lock.Core", "lock/Leistd.Lock.Memory"],
     "components/lock/Leistd.Lock.Tests/Leistd.Lock.Tests.csproj", True, "替身文件必须放在 TestDoubles/",
     ["Memory/LockDoubles.cs"]),
    ("反例：ddd-struct 测试项目同样受约束",
     ["lock/Leistd.Lock.Core"],
     "ddd-struct/Leistd.Ddd.Domain.Tests/Leistd.Ddd.Domain.Tests.csproj", True, "子目录名不符", ["Entities/A.cs"]),
]


def self_test() -> int:
    failures: list[str] = []

    reported = False
    for label, packages, test_rel, registered, expect, *rest in CASES:
        extras = rest[0] if rest else []
        with tempfile.TemporaryDirectory() as tmp:
            fw = Path(tmp) / "framework"
            for pkg in packages:
                d = fw / "components" / pkg
                d.mkdir(parents=True)
                # 让 has_executable_code 认定该家族有可覆盖代码
                (d / "Thing.cs").write_text(
                    "public class Thing { public int F() { return 1; } }", encoding="utf-8")
                (d / f"{Path(pkg).name}.csproj").write_text("<Project />", encoding="utf-8")

            target = fw / "tests" / test_rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("<Project />", encoding="utf-8")
            for extra in extras:
                file = target.parent / extra
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_text("", encoding="utf-8")
            (fw / "tests" / "ddd-struct").mkdir(parents=True, exist_ok=True)
            (fw / "ddd-struct" / "Leistd.Ddd.Domain").mkdir(parents=True, exist_ok=True)
            (fw / "ddd-struct" / "Leistd.Ddd.Domain" / "Leistd.Ddd.Domain.csproj").write_text("<Project />", encoding="utf-8")
            if label.startswith("例外：根目录文件多"):
                reported = bool(root_file_reports(fw))

            entry = f'    <Project Path="tests/{test_rel}" />\n' if registered else ""
            (fw / "Leistd.Framework.slnx").write_text(
                f"<Solution>\n{entry}</Solution>\n", encoding="utf-8")

            problems = check(fw)
            if expect is None:
                if problems:
                    failures.append(f"{label}：期望通过，实际拒绝（{problems[0]}）")
            elif not any(expect in p for p in problems):
                got = "；".join(problems) if problems else "无任何诊断"
                failures.append(f"{label}：期望命中「{expect}」，实际得到 {got}")

    if not reported:
        failures.append("例外：根目录文件超过阈值时应出现在报告里，实际没有")

    if failures:
        print("测试布局闸门自检失败：")
        for f in failures:
            print(f"  {f}")
        return 1

    print(f"✅ 测试布局闸门自检通过（{len(CASES)} 个用例，每条反例均点名其针对的规则）。")
    return 0


def main() -> int:
    if "--self-test" in sys.argv:
        return self_test()

    framework = REPO_ROOT / "framework"
    problems = check(framework)
    if problems:
        print("测试布局检查失败：")
        for problem in problems:
            print(f"  {problem}")
        return 1

    for report in root_file_reports(framework):
        print(f"ℹ️  {report}（超过 {ROOT_FILE_REPORT_THRESHOLD} 个，仅报告：考虑按包后缀分目录）")

    components = framework / "components"
    tests = framework / "tests"
    families = len([p for p in components.iterdir() if p.is_dir()])
    mirrored = len([p for p in (tests / "components").iterdir() if p.is_dir()])
    projects = len([p for p in tests.rglob("*.csproj")
                    if not any(part in {"obj", "bin"} for part in p.parts)])
    print(
        f"✅ 测试布局检查通过（{families} 个组件家族，{mirrored} 个已镜像，{len(WAIVERS)} 个豁免；"
        f"{projects} 个项目路径形状合法、项目名与家族一致、且已登记到 .slnx）。"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
