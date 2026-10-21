#!/usr/bin/env python3
"""只在本仓库成立的多语言判据；项目自身的判据在随模板分发的 template/scripts/check-i18n.py。

  1. 框架组件随包译文（自动枚举 framework/components/**/Resources）：culture、键集合、`{Name}` 占位符一致。
     这些键是公共契约——宿主按键覆盖组件译文，错误码按键找句子，抛异常那侧按占位符名传参。
  2. 框架错误码形如 `<所有者>:<名称>`，在框架与模板之间全局唯一；声明错误码的包必须在**同一个包**的
     `Resources/en.json` 与 `zh-CN.json` 里都有该码的句子——宿主资源里的同名键不算，那是覆盖，不是随包译文。
     错误码文件不限于 `*ErrorCodes.cs`：`Errors/` 目录下的文件与任何 `*Codes.cs`（如 `OperationFailureCodes`）都算；
     完全没有资源目录的包同样要报，而不是因为"没有资源可比"被跳过。
  3. 宿主资源不复制组件译文：同名键会覆盖组件自带的句子，组件改文案时旧句子被静默钉死。
  4. 不启用多语言时组件内联的英文表（ENGLISH / ENGLISH_VALIDATION）与英文词条逐键相同，覆盖模板用到的键，
     校验提示表覆盖全部 `validation.*`。两个分支只在模板源码里同时存在，生成项目里无从比对。

解析与比对逻辑全部复用分发脚本，这里不另写一份。
自检：`python3 scripts/check-i18n-repo.py --self-test`。
"""
import importlib.util
import json
from pathlib import Path
import re
import shutil
import sys
import tempfile

REPO = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location('check_i18n', REPO / 'template/scripts/check-i18n.py')
project = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(project)

# 宿主确要改写组件文案的键，逐条写明原因。
HOST_OVERRIDE_ALLOWED = {
    # 补充模板自带的迁移入口（ConnectionStrings:MigrationTarget、DbMigrator --apply）：组件不知道宿主用什么迁移工具
    'Tenant:DedicatedDatabaseMissing',
    'Tenant:DedicatedDatabaseNotMigrated',
    'Tenant:DedicatedDatabaseUnreachable',
}
FRAMEWORK_CODE = re.compile(r'[A-Z][A-Za-z0-9]*:[A-Z][A-Za-z0-9]*')
ENTRY = re.compile(r'''(?:['"])?([\w.]+)(?:['"])?:\s*('(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*")\s*,''')


def framework_error_codes(components):
    """框架错误码常量 (文件, 成员名, 码)：`Errors/` 目录下的文件与任何 `*Codes.cs`。"""
    return [(path, m[1], m[2])
            for path in project.source_files(components, {'.cs'})
            if path.name.endswith('Codes.cs') or 'Errors' in path.relative_to(components).parts[:-1]
            for m in project.CONSTANT.finditer(project.without_doc_comments(path.read_text(encoding='utf-8')))]


def owning_package(path, components):
    """离文件最近的、含 csproj 的上级目录。"""
    for parent in path.parents:
        if parent == components:
            return None
        if any(parent.glob('*.csproj')):
            return parent
    return None


def check_framework(repo):
    errors = []
    components = repo / 'framework/components'
    resource_dirs = sorted(d for d in components.rglob('Resources')
                           if d.is_dir() and all((d / f'{lang}.json').is_file() for lang in project.LOCALES)
                           and not {'bin', 'obj'} & set(d.relative_to(components).parts))
    if not resource_dirs:
        return ['framework: no component resource directory found; the enumeration is probably wrong']

    template_src = repo / 'template/backend/src'
    owners = {code: project.relative(path, repo)
              for layer in ('*.Domain', '*.Application') for p in sorted(template_src.glob(layer))
              for path, _, code in project.error_code_constants(p)}
    package_texts = {}
    for path, _, code in framework_error_codes(components):
        name = project.relative(path, repo)
        if not FRAMEWORK_CODE.fullmatch(code):
            errors.append(f'{name}: framework error code {code} must look like Owner:Name')
        if code in owners:
            errors.append(f'{name}: duplicate error code {code} (also {owners[code]})')
        owners.setdefault(code, name)
        package = owning_package(path, components)
        if package is None:
            errors.append(f'{name}: error code file is not inside a package (no *.csproj above it)')
            continue
        for lang in project.LOCALES:
            resource = package / 'Resources' / f'{lang}.json'
            if resource not in package_texts:
                package_texts[resource] = project.read_resource(resource, lang)[0] if resource.is_file() else None
            texts = package_texts[resource]
            shown = project.relative(resource, repo)
            if texts is None:
                errors.append(f'{name}: error code {code} needs a sentence in its own package, but {shown} is missing or invalid')
            elif code not in texts:
                errors.append(f'{name}: error code {code} has no resource entry in {shown}')

    host_dir = next(template_src.glob('*.Api/Resources'), None)
    host_texts = project.compare_resource_pair('host', host_dir)[0] if host_dir else None
    for directory in resource_dirs:
        label = f'framework {directory.parent.name}'
        texts, problems = project.compare_resource_pair(label, directory)
        errors += problems
        if texts is None:
            continue
        for key in sorted(set(texts) & set(host_texts or {}) - HOST_OVERRIDE_ALLOWED):
            errors.append(f'host resources copy {key} from {directory.parent.name}: delete the host entry, '
                          'or add it to HOST_OVERRIDE_ALLOWED with the reason')
    return errors


def check_english_tables(frontend):
    en, _, errors = project.load_frontend_catalog(frontend)
    if errors:
        return []  # 词条本身的问题由分发脚本报告，这里不重复
    errors = []
    validation_found = False
    for path in project.source_files(frontend / 'src', {'.ts', '.html'}):
        text = path.read_text(encoding='utf-8')
        html = path.with_suffix('.html')
        html_refs = project.template_refs(html.read_text(encoding='utf-8')) if html.exists() else []
        prefixes = dict(html_refs)
        for table in project.TABLE.finditer(text):
            values = {}
            for m in ENTRY.finditer(table[2]):
                key = prefixes.get(m[1], m[1])
                values[m[1]] = re.sub(r'\\(.)', r'\1', m[2][1:-1])
                if key not in en or values[m[1]] != en[key]:
                    errors.append(f'{path.name}: English table differs for {key}')
            for raw, _ in html_refs + [(m[1], m[1]) for m in project.CALL.finditer(project.TABLE.sub('', text))]:
                if raw not in values:
                    errors.append(f'{path.name}: English table missing {raw}')
            if table[1]:
                validation_found = True
                for key in en:
                    if key.startswith('validation.') and key not in values:
                        errors.append(f'{path.name}: validation table missing {key}')
    if not validation_found:
        errors.append('missing ENGLISH_VALIDATION table')
    return errors


def check(repo):
    return check_framework(repo) + check_english_tables(repo / 'template/frontend')


def self_test():
    def write(name, text):
        def apply(root):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8')
        return apply

    def edit(name, old, new):
        def apply(root):
            path = root / name
            text = path.read_text(encoding='utf-8')
            assert old in text, (name, old)
            path.write_text(text.replace(old, new), encoding='utf-8')
        return apply

    component = 'framework/components/settings/Leistd.Settings.Core'

    def fixture(root):
        project.write_fixture(root / 'template')
        write('template/frontend/src/util.ts', "const ENGLISH_VALIDATION: Record<string, string> = {\n  'validation.required': 'Required',\n};\n")(root)
        for lang, sentence in (('en', 'Setting {Name} is undefined.'), ('zh-CN', '未定义设置 {Name}。')):
            write(f'{component}/Resources/{lang}.json', json.dumps({'culture': lang, 'texts': {'Setting:Undefined': sentence}}))(root)
        write(f'{component}/Errors/SettingErrorCodes.cs', 'public static class SettingErrorCodes { public const string Undefined = "Setting:Undefined"; }')(root)
        write(f'{component}/Leistd.Settings.Core.csproj', '<Project />')(root)

    page = 'template/frontend/src/page.ts'
    host_en = 'template/backend/src/Demo.Api/Resources/en.json'
    host_zh = 'template/backend/src/Demo.Api/Resources/zh-CN.json'
    component_zh = f'{component}/Resources/zh-CN.json'
    def host_copies(key):
        def apply(root):
            edit(host_en, '"User:NotFound"', f'"{key}": "x",\n    "User:NotFound"')(root)
            edit(host_zh, '"User:NotFound"', f'"{key}": "x", "User:NotFound"')(root)
            for lang in ('en', 'zh-CN'):
                edit(f'{component}/Resources/{lang}.json', '"texts": {', f'"texts": {{"{key}": "y", ')(root)
        return apply

    valid = [
        ('fixture as written', lambda root: None),
        ('allow-listed host override', host_copies('Tenant:DedicatedDatabaseMissing')),
    ]
    cases = [
        ('no component resources', lambda root: shutil.rmtree(root / f'{component}/Resources'), 'no component resource directory found'),
        ('framework key sets', edit(component_zh, '"Setting:Undefined"', '"Setting:Missing"'), 'key sets differ'),
        ('framework placeholders', edit(component_zh, '{Name}', '{Key}'), 'placeholders differ for Setting:Undefined'),
        ('framework culture', edit(component_zh, '"culture": "zh-CN"', '"culture": "zh"'), 'does not match file name zh-CN'),
        ('framework code format', edit(f'{component}/Errors/SettingErrorCodes.cs', '"Setting:Undefined"', '"setting.undefined"'), 'must look like Owner:Name'),
        ('framework code collides with template', edit(f'{component}/Errors/SettingErrorCodes.cs', 'public const string Undefined', 'public const string NotFound = "User:NotFound";\n    public const string Undefined'), 'duplicate error code User:NotFound'),
        ('framework code without resource', edit(f'{component}/Errors/SettingErrorCodes.cs', 'public const string Undefined', 'public const string Locked = "Setting:Locked";\n    public const string Undefined'), 'error code Setting:Locked has no resource entry'),
        ('host copies component key', host_copies('Setting:Copied'), 'host resources copy Setting:Copied'),
        # 错误码文件不止 *ErrorCodes.cs：Errors/ 下的其他文件、任意 *Codes.cs 都要有随包译文
        ('codes outside *ErrorCodes.cs', write(f'{component}/Errors/SettingFailureCodes.cs', 'public static class SettingFailureCodes { public const string Locked = "Setting:Locked"; }'), 'error code Setting:Locked has no resource entry'),
        ('*Codes.cs outside Errors/', write(f'{component}/Stores/StoreCodes.cs', 'public static class StoreCodes { public const string Busy = "Setting:Busy"; }'), 'error code Setting:Busy has no resource entry'),
        ('package without any resources', lambda root: [write('framework/components/records/Leistd.Records.Core/Leistd.Records.Core.csproj', '<Project />')(root), write('framework/components/records/Leistd.Records.Core/Errors/OperationFailureCodes.cs', 'public static class OperationFailureCodes { public const string Forbidden = "Error:Forbidden"; }')(root)], 'error code Error:Forbidden needs a sentence in its own package'),
        # 宿主资源里有同名键不能替组件补译文
        ('host entry does not count', lambda root: [write(f'{component}/Errors/SettingFailureCodes.cs', 'public static class SettingFailureCodes { public const string Locked = "Setting:Locked"; }')(root), edit(host_en, '"User:NotFound"', '"Setting:Locked": "x",\n    "User:NotFound"')(root), edit(host_zh, '"User:NotFound"', '"Setting:Locked": "x", "User:NotFound"')(root)], 'error code Setting:Locked has no resource entry'),
        ('English value', edit(page, "'Users'", "'Wrong'"), 'English table differs for users.title'),
        ('English coverage', edit(page, "  'title': 'Users',\n", ''), 'English table missing title'),
        ('validation table coverage', lambda root: [write(f'template/frontend/public/i18n/{lang}.json', json.dumps({'validation': {'required': 'Required', 'email': 'Invalid email'}, 'common': {'save': 'Save'}}))(root) for lang in project.LOCALES], 'validation table missing validation.email'),
        ('validation table', write('template/frontend/src/util.ts', ''), 'missing ENGLISH_VALIDATION table'),
    ]
    failures = []
    for name, mutate, *expected in [(n, m) for n, m in valid] + cases:
        with tempfile.TemporaryDirectory(prefix='check-i18n-repo-') as tmp:
            root = Path(tmp)
            fixture(root)
            mutate(root)
            errors = check(root)
        if expected and not any(expected[0] in e for e in errors):
            failures.append(f'{name}: expected "{expected[0]}", got {errors}')
        if not expected and errors:
            failures.append(f'{name}: expected no problem, got {errors}')
    if failures:
        print('FAIL: check-i18n-repo self-test')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print(f'PASS: check-i18n-repo self-test ({len(valid)} valid fixtures, {len(cases)} independent rule mutations)')
    return 0


def main():
    if '--self-test' in sys.argv[1:]:
        return self_test()
    problems = check(REPO)
    if problems:
        print(f'FAIL: repository i18n check found {len(problems)} problem(s):')
        for problem in problems:
            print(f'  - {problem}')
        return 1
    print('PASS: framework resources and error codes, host does not copy component texts, English tables match')
    return 0


if __name__ == '__main__':
    sys.exit(main())
