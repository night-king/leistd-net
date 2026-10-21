#!/usr/bin/env python3
"""本项目业务错误码的形态、归属与引用必须一致。

错误码是前端分支与客户端重试的契约。写错前缀、两处重名、只剩异常映射在用的死码都不会让测试变红：
接口照常返回，只是码对不上、或者映射了一个永远不会抛出的码。这道闸门把它们变成失败。

判据（扫描 `backend/src`，测试不在内）：
  1. `*ErrorCodes.cs` 里的常量值形如 `<所有者>:<成员名>`：所有者取文件名去掉 ErrorCodes，成员名即常量名。
  2. 码值全局唯一；同一所有者前缀只由一个 `*ErrorCodes.cs` 声明（不同目录的同名文件也算两个）。
  3. 源码不写字面量码：`new BusinessException("…")` 的第一个参数不是字面量（类型名与括号之间、参数之间的空白和换行不影响判定），
     任何字符串字面量都不等于一个已声明的码（应引用常量）。
  4. 每个常量至少有一处业务引用：零引用是死码；只被异常映射（`ExceptionMappings/`、`*ExceptionMappings.cs`）
     引用、没有任何抛出处的，同样是死码——映射了一个永远不会出现的码。

例外：框架组件自带的错误码（如 `MultiTenancyErrorCodes`）不在本项目声明，只在映射里引用属于正常用法，不受判据 4 约束；
常量值引用组件常量（而不是字面量）时，形态与归属由组件负责，不受判据 1 约束。

与本地化无关：是否有对应词条由 `scripts/check-i18n.py`（开启本地化时随项目生成）检查。

用法：`python3 scripts/check-error-codes.py`；判据自检：`python3 scripts/check-error-codes.py --self-test`。
"""
import collections
from pathlib import Path
import re
import sys
import tempfile

# 脚本所在目录的上一级就是被检查的根：生成项目里是项目根，本仓里是 template/。
ROOT = Path(__file__).resolve().parents[1]

CONSTANT = re.compile(r'public\s+const\s+string\s+([A-Za-z]\w*)\s*=\s*("(?:[^"\\]|\\.)*"|[^;]+);')
CODE_SHAPE = re.compile(r'^[A-Z][A-Za-z0-9]*:[A-Z][A-Za-z0-9]*$')
# 类型名与括号之间、参数之间允许空白和换行；也认限定名与具名参数 `code:`。
LITERAL_BUSINESS_CODE = re.compile(
    r'\bnew\s+(?:[\w.]+\.)?BusinessException\s*\(\s*(?:code\s*:\s*)?(?:\$@|@\$|\$|@)?"')
STRING_LITERAL = re.compile(r'"((?:[^"\\\n]|\\.)*)"')


def source_files(directory):
    """项目源码文件：跳过构建产物。"""
    if not directory.is_dir():
        return []
    return sorted(p for p in directory.rglob('*.cs')
                  if p.is_file() and not {'bin', 'obj'} & set(p.relative_to(directory).parts))


def code_only(text):
    """去掉注释（含 XML 文档注释）：注释里的示例不是真实引用。"""
    text = re.sub(r'/\*[\s\S]*?\*/', lambda m: '\n' * m[0].count('\n'), text)
    return '\n'.join(re.sub(r'(^|[^:"])//.*$', r'\1', line) for line in text.split('\n'))


def is_mapping(path):
    return 'ExceptionMappings' in path.parts or path.name.endswith('ExceptionMappings.cs')


def check_project(root):
    src = root / 'backend/src'
    files = source_files(src)
    if not files:
        return [f'{src.relative_to(root).as_posix()}: no C# sources found']
    texts = {path: code_only(path.read_text(encoding='utf-8')) for path in files}
    name = lambda path: path.relative_to(root).as_posix()

    errors = []
    declared = []                                  # (文件, 类名, 成员名, 码值)
    owners = collections.defaultdict(list)
    for path in files:
        if not path.name.endswith('ErrorCodes.cs'):
            continue
        cls = path.stem
        owner = cls[:-len('ErrorCodes')]
        owners[owner].append(name(path))
        for member, value in CONSTANT.findall(texts[path]):
            if not value.startswith('"'):
                continue                           # 引用组件常量：形态归组件
            code = value[1:-1]
            declared.append((path, cls, member, code))
            if not CODE_SHAPE.match(code) or code != f'{owner}:{member}':
                errors.append(f'{name(path)}: error code {member} = {code} should be {owner}:{member}')

    for owner, paths in sorted(owners.items()):
        if len(paths) > 1:
            errors.append(f'error code prefix {owner} is declared by more than one file: {", ".join(paths)}')

    seen = {}
    for path, cls, member, code in declared:
        if code in seen:
            errors.append(f'{name(path)}: duplicate error code {code} (also {seen[code]})')
        seen.setdefault(code, f'{name(path)}')

    codes = {code for _, _, _, code in declared}
    for path in files:
        if path.name.endswith('ErrorCodes.cs'):
            continue
        text = texts[path]
        # 按整个文件匹配：构造调用可能跨行，按行扫描会漏掉换行后的字面量。
        raised = set()
        for match in LITERAL_BUSINESS_CODE.finditer(text):
            number = text.count('\n', 0, match.start()) + 1
            raised.add(text.count('\n', 0, match.end()) + 1)
            errors.append(f'{name(path)}:{number}: BusinessException must use the owning module error code constant')
        for number, line in enumerate(text.split('\n'), 1):
            if number in raised:
                continue
            for literal in STRING_LITERAL.findall(line):
                if literal in codes:
                    errors.append(f'{name(path)}:{number}: literal error code "{literal}" must reference its constant')

    for path, cls, member, code in declared:
        reference = re.compile(r'(?<![\w.])' + re.escape(cls) + r'\s*\.\s*' + re.escape(member) + r'\b')
        users = [other for other in files if other != path and reference.search(texts[other])]
        if not users:
            errors.append(f'{name(path)}: error code {code} is never referenced')
        elif all(is_mapping(other) for other in users):
            errors.append(f'{name(path)}: error code {code} is only referenced by exception mappings, never raised')
    return errors


# ---------------------------------------------------------------- 自检

FIXTURE = {
    'backend/src/Demo.Domain/Users/Errors/UserErrorCodes.cs': '''namespace Demo.Domain.Users.Errors;

/// <summary>示例：<c>public const string Example = "User:Example";</c></summary>
public static class UserErrorCodes
{
    public const string NotFound = "User:NotFound";
    public const string Locked = "User:Locked";
    // 引用组件常量：形态由组件负责
    public const string TenantMissing = MultiTenancyErrorCodes.TenantNotFound;
}
''',
    'backend/src/Demo.Application/Users/UserAppService.cs': '''public class UserAppService
{
    // throw new BusinessException("User:NotFound") 注释里的示例不算
    public void Find() => throw new BusinessException(UserErrorCodes.NotFound, "User was not found.");
    public void Lock() => throw new BusinessException(
        UserErrorCodes.Locked, "User is locked.");
    public string Route() => "https://example.com/users";
}
''',
    'backend/src/Demo.Api/Hosting/ExceptionMappings/UserExceptionMappings.cs': '''internal static class UserExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        options.MapCode(UserErrorCodes.NotFound, StatusCodes.Status404NotFound);
        // 组件自带的码只在映射里出现，属于正常用法
        options.MapCode(MultiTenancyErrorCodes.TenantNotFound, StatusCodes.Status404NotFound);
    }
}
''',
}


def write_fixture(root):
    for relative, text in FIXTURE.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')


def self_test():
    codes = 'backend/src/Demo.Domain/Users/Errors/UserErrorCodes.cs'
    service = 'backend/src/Demo.Application/Users/UserAppService.cs'
    mappings = 'backend/src/Demo.Api/Hosting/ExceptionMappings/UserExceptionMappings.cs'

    def edit(relative, old, new):
        def apply(root):
            path = root / relative
            text = path.read_text(encoding='utf-8')
            assert old in text, (relative, old)
            path.write_text(text.replace(old, new), encoding='utf-8')
        return apply

    def write(relative, text):
        def apply(root):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8')
        return apply

    add_code = lambda member: edit(codes, '    public const string Locked = "User:Locked";',
                                   f'    public const string Locked = "User:Locked";\n    public const string {member} = "User:{member}";')
    valid = [
        ('fixture as written', lambda root: None),
        ('mapping-only component code', edit(mappings, 'MultiTenancyErrorCodes.TenantNotFound, StatusCodes.Status404NotFound',
                                             'MultiTenancyErrorCodes.TenantInactive, StatusCodes.Status403Forbidden')),
    ]
    cases = [
        ('literal in BusinessException', write('backend/src/Demo.Application/Users/Literal.cs',
                                               'throw new BusinessException("User:Gone", "x");'),
         'Literal.cs:1: BusinessException must use the owning module error code constant'),
        ('literal with space before parenthesis', write('backend/src/Demo.Application/Users/Literal.cs',
                                                        'throw new BusinessException ("User:Undeclared", "Failure.");'),
         'Literal.cs:1: BusinessException must use the owning module error code constant'),
        ('literal on the next line', write('backend/src/Demo.Application/Users/Literal.cs',
                                           'void M()\n{\n    throw new BusinessException\n    (\n        "User:Undeclared",\n        "Failure.");\n}'),
         'Literal.cs:3: BusinessException must use the owning module error code constant'),
        ('qualified type with named literal code', write('backend/src/Demo.Application/Users/Literal.cs',
                                                         'throw new Leistd.Exceptions.BusinessException(code: $"User:{x}", "Failure.");'),
         'Literal.cs:1: BusinessException must use the owning module error code constant'),
        ('literal code elsewhere', write('backend/src/Demo.Api/Controllers/Literal.cs',
                                         'if (error.Code == "User:Locked") return;'),
         'Literal.cs:1: literal error code "User:Locked"'),
        ('zero-reference constant', add_code('Retired'), 'error code User:Retired is never referenced'),
        ('mapping-only constant', chain(add_code('Archived'), edit(mappings, '    }\n}',
                                        '        options.MapCode(UserErrorCodes.Archived, StatusCodes.Status409Conflict);\n    }\n}')),
         'error code User:Archived is only referenced by exception mappings'),
        ('commented-out raise does not count', chain(add_code('Frozen'), edit(service, '    public string Route()',
                                                     '    // throw new BusinessException(UserErrorCodes.Frozen, "x");\n    public string Route()')),
         'error code User:Frozen is never referenced'),
        ('owner mismatch', edit(codes, '"User:Locked"', '"Account:Locked"'), 'should be User:Locked'),
        ('member mismatch', edit(codes, '"User:Locked"', '"User:IsLocked"'), 'should be User:Locked'),
        ('shape', edit(codes, '"User:Locked"', '"user.locked"'), 'should be User:Locked'),
        ('cross-file prefix', write('backend/src/Demo.Application/Users/Errors/UserErrorCodes.cs',
                                    'public static class UserErrorCodes { public const string Disabled = "User:Disabled"; }'),
         'error code prefix User is declared by more than one file'),
        ('duplicate code', edit(codes, 'public const string Locked = "User:Locked";',
                                'public const string Locked = "User:Locked";\n    public const string LockedAgain = "User:Locked";'),
         'duplicate error code User:Locked'),
    ]

    failures = []
    for name, mutate, *expected in [(n, m) for n, m in valid] + cases:
        with tempfile.TemporaryDirectory(prefix='check-error-codes-') as tmp:
            root = Path(tmp)
            write_fixture(root)
            mutate(root)
            errors = check_project(root)
        if expected and not any(expected[0] in e for e in errors):
            failures.append(f'{name}: expected "{expected[0]}", got {errors}')
        if not expected and errors:
            failures.append(f'{name}: expected no problem, got {errors}')
    if failures:
        print('FAIL: check-error-codes self-test')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print(f'PASS: check-error-codes self-test ({len(valid)} valid fixtures, {len(cases)} independent rule mutations)')
    return 0


def chain(*steps):
    def apply(root):
        for step in steps:
            step(root)
    return apply


def main():
    # Windows 上管道输出默认按本地代码页编码，打印 ✅ 与中文会崩溃
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8')
    if '--self-test' in sys.argv[1:]:
        return self_test()
    problems = check_project(ROOT)
    if problems:
        print(f'FAIL: error code check found {len(problems)} problem(s):')
        for problem in problems:
            print(f'  - {problem}')
        return 1
    print('PASS: error codes are well-formed, uniquely owned, referenced by constant and raised by business code')
    return 0


if __name__ == '__main__':
    sys.exit(main())
