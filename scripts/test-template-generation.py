#!/usr/bin/env python3
"""实际生成全部有效形态，验证原始输入映射、裁剪不变量及角色无效参数等价。

部署资产在每个形态的生成产物上核对（判据自检：`--self-test`）：
- compose 引用的 `${VAR}` 与 `deploy/.env.example` 登记的变量双向一致；
- compose 里的 `Section__Key` 能对上 appsettings 的键或 Options 类型的属性链；
- Serilog 的 Override 类别是本项目命名空间、所引用包或 Microsoft/System 的前缀；
- 生成项目根目录与 `docs/standards/project-structure.md` §1 的清单一致。

CI 交付参数 `Ci` 不是能力：全部形态按默认值生成，有无前端两类形态另生成其余取值，核对差异只落在
CI 薄壳与描述它的文档；每种 `scripts/verify.ps1` 产物核对步骤清单与失败即停（命令换成记录调用的替身）。

证明拆成互不引用产物的四片（`--shard`）：
- `0`/`1`/`2`：320 个有效形态按序号取模分片，逐个生成并校验，记录每个形态的摘要与 verify.ps1 键；
- `control`：768 组原始输入映射、角色等价比较、Ci 变体与全部 verify.ps1 变体的步骤与失败传播，
  所需的代表形态由本片自行重新生成。
汇总（check-quality-proof.ps1 -Kind generation）核对四片同一候选与源码摘要、形态无缺号无重复、
对照片各项证明齐全，并交叉核对代表形态摘要与每个形态的 verify.ps1 键都被对照片覆盖。
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import importlib.util
import itertools
import json
import os
from pathlib import Path
import posixpath
import re
import shutil
import subprocess
import sys
import tempfile
import threading
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('scenario_coverage', ROOT / 'scripts/check-template-scenario-coverage.py')
model = importlib.util.module_from_spec(spec)
spec.loader.exec_module(model)


def cli_arguments(config, values):
    args = []
    for name, symbol in config['symbols'].items():
        if symbol.get('type') != 'parameter' or symbol.get('datatype') not in ('bool', 'choice'):
            continue
        args += ['--' + re.sub(r'(?<!^)(?=[A-Z])', '-', name).lower(), str(values[name]).lower() if isinstance(values[name], bool) else values[name]]
    return args


MARKER = '.leistd/template.json'
GUID = rb'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'


def normalize(content, config, relative=''):
    # Only template-declared random GUID substitutions are ignored; business GUIDs remain evidence.
    for original in config.get('guids', []):
        content = re.sub(rb'(<UserSecretsId>)[^<]+(</UserSecretsId>)', rb'\1TEMPLATE-GUID\2', content)
    if relative == MARKER:
        # 来源标记记录原始输入：角色等价与 Ci 变体比较的是产物字节，标记的 parameters 另由 check_marker 逐项核对
        content = re.sub(GUID, b'TEMPLATE-GUID', content)
        content = re.sub(rb'"parameters": \{[^}]*\}', b'"parameters": "RAW-INPUT"', content)
    return content


def check_marker(output, values, config, sdk):
    """来源标记：源码安装的占位身份、生成名称、全部原始参数取值、各项目实际的 UserSecretsId 与生成时的 SDK。"""
    marker = json.loads((output / MARKER).read_text(encoding='utf-8'))
    assert marker['schemaVersion'] == 1 and marker['package'] == {'id': 'Leistd.Templates', 'version': '0.0.0-source', 'sourceSha': '0' * 40}, f'Marker package: {marker}'
    assert marker['name'] == 'Generation.Probe' and marker['sdkVersion'] == sdk, f'Marker name/SDK: {marker}'
    parameters = {name: values[name] for name, symbol in config['symbols'].items() if symbol.get('type') == 'parameter'}
    assert marker['parameters'] == parameters and all(type(marker['parameters'][n]) is type(v) for n, v in parameters.items()), f'Marker parameters: {marker["parameters"]} != {parameters}'
    secrets = {}
    for project in output.rglob('*.csproj'):
        found = re.search(r'<UserSecretsId>([^<]+)</UserSecretsId>', project.read_text(encoding='utf-8'))
        if found:
            secrets[project.relative_to(output).as_posix()] = found[1]
    assert secrets and marker['userSecretsIds'] == secrets, f'Marker UserSecretsIds: {marker["userSecretsIds"]} != {secrets}'


def validate_relative_modules(output, files):
    for relative in files:
        if not relative.endswith('.ts'):
            continue
        source = (output / relative).read_text(encoding='utf-8')
        for module in re.findall(r"(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s*)['\"](\.[^'\"]+)['\"]", source):
            stem = posixpath.normpath(posixpath.join(posixpath.dirname(relative), module))
            candidates = [stem, stem + '.ts', stem + '.json', stem + '/index.ts']
            if stem.endswith('.js'):
                candidates.append(stem[:-3] + '.ts')
            assert any(candidate in files for candidate in candidates), f'Missing relative module: {relative} -> {module}'
        for asset in re.findall(r"\b(?:templateUrl|styleUrl)\s*:\s*['\"]([^'\"]+)['\"]", source):
            target = posixpath.normpath(posixpath.join(posixpath.dirname(relative), asset))
            assert target in files, f'Missing component asset: {relative} -> {asset}'


def translation_keys(node, prefix=''):
    keys = {}
    for key, value in node.items():
        name = f'{prefix}.{key}' if prefix else key
        if isinstance(value, dict):
            keys.update(translation_keys(value, name))
        else:
            keys[name] = value
    return keys


COMPOSE_VARIABLE = re.compile(r'\$\{([A-Za-z_][A-Za-z0-9_]*)')
ENV_EXAMPLE_ENTRY = re.compile(r'^#?\s*([A-Z][A-Z0-9_]*)=', re.M)
COMPOSE_LIST_ENV = re.compile(r'^\s*-\s*([A-Za-z_][\w.]*)=')
COMPOSE_MAP_ENV = re.compile(r'^\s+([A-Za-z_][\w.]*__[\w.]*):')
CLASS_DECL = re.compile(r'\b(?:class|record)\s+(\w+)(?:<[^>{]*>)?(?:\s*\([^)]*\))?\s*(?::\s*([^{\n]+))?')
PROPERTY_DECL = re.compile(r'\bpublic\s+(?:(?:required|virtual|override|new|static)\s+)*([\w<>\[\]?,.\s]+?)\s+(\w+)\s*\{\s*(?:get|set|init)\b')
SECTION_CONST = re.compile(r'\bconst\s+string\s+(\w+)\s*=\s*(\$?)"([^"]+)"')
NAMESPACE_DECL = re.compile(r'^\s*namespace\s+([\w.]+)', re.M)
# 由宿主自身解析、不经 Options 绑定的根节
FREE_CONFIGURATION_ROOTS = {'connectionstrings', 'serilog'}
LOGGING_CATEGORY_ROOTS = ('Microsoft', 'System')


def compose_lines(text):
    """compose 的有效行：去掉整行注释与行尾注释。"""
    return [re.sub(r'\s+#.*$', '', line) for line in text.split('\n') if not line.lstrip().startswith('#')]


def check_compose_variables(compose, env_example):
    used = set(COMPOSE_VARIABLE.findall('\n'.join(compose_lines(compose))))
    listed = set(ENV_EXAMPLE_ENTRY.findall(env_example))
    return ([f'compose variable missing from .env.example: {name}' for name in sorted(used - listed)]
            + [f'.env.example variable not used by compose: {name}' for name in sorted(listed - used)])


def options_catalog(sources):
    """从 C# 源码收集 Options 形态：类 -> (属性 -> 类型名, 基类)，配置节 -> [(类名, 是否按名字分节)]。"""
    classes, sections, constants = {}, {}, {}
    for text in sources:
        text = re.sub(r'/\*[\s\S]*?\*/', lambda m: ' ' * len(m[0]), text)
        text = re.sub(r'//[^\n]*', lambda m: ' ' * len(m[0]), text)
        # 字符串内容换成等长占位，花括号与关键字只在代码里计数；取值时回到原文
        masked = re.sub(r'"[^"\n]*"', lambda m: '"' + ' ' * (len(m[0]) - 2) + '"', text)
        spans = []
        for declared in CLASS_DECL.finditer(masked):
            body = re.compile(r'[{;]').search(masked, declared.end())
            if body is None or body[0] != '{':
                continue
            depth, index = 0, body.start()
            while index < len(masked):
                depth += {'{': 1, '}': -1}.get(masked[index], 0)
                if depth == 0:
                    break
                index += 1
            spans.append((body.start(), index, declared[1]))
            bases = [b.strip().split('<')[0] for b in (declared[2] or '').split(',') if b.strip()]
            classes.setdefault(declared[1], ({}, bases))

        def owner_at(position):
            inside = [span for span in spans if span[0] < position < span[1]]
            return max(inside, key=lambda span: span[0])[2] if inside else None

        for match in PROPERTY_DECL.finditer(masked):
            if owner := owner_at(match.start()):
                classes[owner][0][match[2].lower()] = match[1].strip()
        for match in SECTION_CONST.finditer(text):
            if owner := owner_at(match.start()):
                constants[(owner, match[1])] = (match[3], match[2] == '$')
    def resolve(owner, name, seen=()):
        value, interpolated = constants[(owner, name)]
        if not interpolated:
            return value
        def part(match):
            reference = match[1].split('.')
            key = (reference[0], reference[1]) if len(reference) == 2 else (owner, reference[0])
            return resolve(*key, seen + (key,)) if key in constants and key not in seen else match[0]
        return re.sub(r'\{([\w.]+)\}', part, value)
    for (owner, name), _ in constants.items():
        if name == 'SectionName' or name.endswith('SectionPrefix'):
            sections.setdefault(resolve(owner, name).lower(), []).append((owner, name.endswith('SectionPrefix')))
    return classes, sections


def element_type(type_name):
    """属性类型 -> (元素类名, 下一段是否为字典键)。"""
    type_name = type_name.replace('?', '').strip()
    dictionary = re.match(r'(?:I?Dictionary|IReadOnlyDictionary)<\s*\w+\s*,\s*(.+)>$', type_name)
    if dictionary:
        return element_type(dictionary[1])[0], True
    generic = re.match(r'[\w.]+<\s*(.+)>$', type_name)
    if generic:
        return element_type(generic[1])
    return type_name.removesuffix('[]').split('.')[-1], False


def check_configuration_key(key, catalog, settings_keys):
    """`A__B__C` 是否对得上 appsettings 的键或 Options 属性链；对不上返回问题描述。"""
    classes, sections = catalog
    path = key.split('__')
    normalized = ':'.join('#' if part.isdigit() else part.lower() for part in path)
    if normalized in settings_keys or path[0].lower() in FREE_CONFIGURATION_ROOTS:
        return None
    segments = [part for part in path if not part.isdigit()]
    lowered = [part.lower() for part in segments]
    matched = max((s for s in sections if lowered[:len(s.split(':'))] == s.split(':')), key=lambda s: len(s.split(':')), default=None)
    if matched is None:
        return f'{key}: no Options section or appsettings key for {segments[0]}'
    remaining = segments[len(matched.split(':')):]
    problems = []
    for owner, by_name in sections[matched]:
        problem = check_options_path(key, owner, by_name, remaining, classes)
        if problem is None:
            return None
        problems.append(problem)
    return '; '.join(problems)


def check_options_path(key, owner, by_name, remaining, classes):
    """一条属性路径须完整匹配同一 Options 类型。"""
    current, free = (None, True) if by_name else (owner, False)
    all_properties = {prop for props, _ in classes.values() for prop in props}
    for segment in remaining:
        if free:
            free = False
            continue
        if current is None:
            if segment.lower() not in all_properties:
                return f'{key}: {segment} is not an Options property'
            continue
        properties, seen, queue = {}, set(), [current]
        while queue:
            name = queue.pop()
            if name in classes and name not in seen:
                seen.add(name)
                properties = {**classes[name][0], **properties}
                queue += classes[name][1]
        if segment.lower() not in properties:
            return f'{key}: {segment} is not a property of {current}'
        current, free = element_type(properties[segment.lower()])
        current = current if current in classes else None
    return None


def check_logging_categories(keys, namespaces, packages):
    errors = []
    roots = set(namespaces) | set(packages)
    for key in keys:
        if not key.lower().startswith('serilog__minimumlevel__override__'):
            continue
        category = key.split('__', 3)[3]
        legal = (category.split('.')[0] in LOGGING_CATEGORY_ROOTS
                 or any(root == category or root.startswith(category + '.') or category.startswith(root + '.') for root in roots))
        if not legal:
            errors.append(f'{key}: Serilog override category {category} matches no project namespace or referenced package')
    return errors


def check_root_entries(entries, project_structure):
    section = re.search(r'^## 1\..*?```text\n(.*?)```', project_structure, re.S | re.M)
    if section is None:
        return ['project-structure.md: missing §1 project root listing']
    documented = {m[1].rstrip('/') for m in re.finditer(r'^[├└]── (\S+)', section[1], re.M)}
    documented = {entry.split('/')[0] for entry in documented}
    # 点开头的条目（.gitignore 等）不要求登记；登记了的（.agents、CI 薄壳）必须存在
    actual = {entry for entry in entries if not entry.startswith('.') or entry in documented or entry == '.agents'}
    return ([f'root entry not listed in project-structure.md §1: {entry}' for entry in sorted(actual - documented)]
            + [f'project-structure.md §1 lists a missing root entry: {entry}' for entry in sorted(documented - actual)])


def flatten_settings(node, prefix=''):
    keys = set()
    if isinstance(node, dict):
        for key, value in node.items():
            keys |= flatten_settings(value, f'{prefix}:{key.lower()}' if prefix else key.lower())
    elif isinstance(node, list):
        for value in node:
            keys |= flatten_settings(value, f'{prefix}:#')
    else:
        keys.add(prefix)
    return keys


FRAMEWORK_SOURCES = None


def check_deployment(output):
    """生成产物上的部署资产核对，返回问题列表。"""
    global FRAMEWORK_SOURCES
    if FRAMEWORK_SOURCES is None:
        FRAMEWORK_SOURCES = [p.read_text(encoding='utf-8') for p in sorted((ROOT / 'framework').rglob('*.cs'))
                             if not {'bin', 'obj', 'tests'} & set(p.relative_to(ROOT / 'framework').parts)]
    project_sources = [p.read_text(encoding='utf-8') for p in sorted((output / 'backend/src').rglob('*.cs'))
                       if not {'bin', 'obj'} & set(p.parts)]
    catalog = options_catalog(FRAMEWORK_SOURCES + project_sources)
    settings_keys = set()
    for path in sorted((output / 'backend/src').glob('*/appsettings*.json')):
        settings_keys |= flatten_settings(json.loads(re.sub(r'^\s*//.*$', '', path.read_text(encoding='utf-8'), flags=re.M)))
    compose = (output / 'deploy/docker-compose.yml').read_text(encoding='utf-8')
    keys = [m[1] for line in compose_lines(compose) for m in [COMPOSE_LIST_ENV.match(line) or COMPOSE_MAP_ENV.match(line)] if m]
    namespaces = {m for text in project_sources for m in NAMESPACE_DECL.findall(text)}
    packages = re.findall(r'<PackageVersion\s+Include="([^"]+)"', (output / 'backend/Directory.Packages.props').read_text(encoding='utf-8'))
    return (check_compose_variables(compose, (output / 'deploy/.env.example').read_text(encoding='utf-8'))
            + [problem for key in keys if '__' in key and not key.lower().startswith('serilog__')
               for problem in [check_configuration_key(key, catalog, settings_keys)] if problem]
            + check_logging_categories(keys, namespaces, packages)
            + check_root_entries([p.name for p in output.iterdir()],
                                 (output / 'docs/standards/project-structure.md').read_text(encoding='utf-8')))


def self_test_gitlab_ci():
    """GitLab 薄壳判据：合法配置通过，语法错误与缺 DinD 变量都失败。

    在完整生成运行开头执行（该运行本就需要 PyYAML 解析产物），不放进 --self-test：
    check-all 的静态闸门不依赖 PyYAML。
    """
    valid = """stages:
  - verify
verify:
  stage: verify
  image: mcr.microsoft.com/dotnet/sdk:10.0
  services:
    - name: docker:dind
      alias: docker
      command: ["--tls=false"]
  variables:
    DOCKER_HOST: "tcp://docker:2375"
    DOCKER_TLS_CERTDIR: ""
    NUGET_PACKAGES: "$CI_PROJECT_DIR/.nuget/packages"
  cache:
    - key:
        prefix: nuget
        files:
          - backend/Directory.Packages.props
      paths:
        - .nuget/packages
      policy: $CACHE_POLICY
  rules:
    - if: $CI_PIPELINE_SOURCE == "schedule"
      when: never
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
      variables:
        CACHE_POLICY: pull
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
      variables:
        CACHE_POLICY: pull-push
  before_script:
    - apt-get update
  script:
    - pwsh -NoProfile -File scripts/verify.ps1
"""
    failures = []
    check_gitlab_ci(valid, False)
    for label, text in [('语法错误', valid.replace('  stage: verify', '  stage: [verify')),
                        ('缺 DOCKER_HOST', valid.replace('    DOCKER_HOST: "tcp://docker:2375"\n', '')),
                        ('合并请求缓存可写', valid.replace('CACHE_POLICY: pull\n', 'CACHE_POLICY: pull-push\n')),
                        ('缓存在项目目录外', valid.replace('"$CI_PROJECT_DIR/.nuget/packages"', '"/root/.nuget/packages"'))]:
        try:
            check_gitlab_ci(text, False)
            failures.append(f'GitLab 薄壳判据未拒绝：{label}')
        except Exception:
            pass
    return failures


def self_test():
    """部署资产判据的夹具：合法、违规与例外各至少一例。"""
    options = ["""
public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";
    public ProviderOptions Github { get; } = new();
    public sealed class ProviderOptions
    {
        public string? ClientId { get; set; }
        public bool IsAvailable => ClientId is not null;
    }
}
public class ServiceClientOptions { public string? BaseAddress { get; set; } }
public static class ServiceClientRegistration { public const string ConfigurationSectionPrefix = "Leistd:ServiceClients"; }
public sealed class BackgroundJobOptions { public const string SectionName = "Leistd:BackgroundJobs"; }
public sealed class RetentionOptions
{
    public const string SubsectionName = "Retention";
    public const string SectionName = $"{BackgroundJobOptions.SectionName}:{SubsectionName}";
    public int Days { get; set; }
    public List<EndpointOptions> Endpoints { get; set; } = [];
}
public sealed class EndpointOptions { public string? Path { get; init; } }
"""]
    catalog = options_catalog(options)
    settings = flatten_settings({'Authentication': {'Issuer': ''}, 'Cors': {'AllowedOrigins': ['x']}})
    cases = [
        ('declared provider property', check_configuration_key('ExternalAuth__Github__ClientId', catalog, settings), None),
        ('extra key under a declared section', check_configuration_key('ExternalAuth__Github__RedirectUri', catalog, settings), 'RedirectUri is not a property of ProviderOptions'),
        ('computed property is not bindable', check_configuration_key('ExternalAuth__Github__IsAvailable', catalog, settings), 'IsAvailable'),
        ('unknown section', check_configuration_key('MyProject__Feature__Enabled', catalog, settings), 'no Options section'),
        ('named client section', check_configuration_key('Leistd__ServiceClients__Identity__BaseAddress', catalog, settings), None),
        ('named client unknown property', check_configuration_key('Leistd__ServiceClients__Identity__Endpoint', catalog, settings), 'Endpoint is not an Options property'),
        ('interpolated section and list element', check_configuration_key('Leistd__BackgroundJobs__Retention__Endpoints__0__Path', catalog, settings), None),
        ('appsettings key with index', check_configuration_key('Cors__AllowedOrigins__0', catalog, settings), None),
        ('connection strings are free', check_configuration_key('ConnectionStrings__Default', catalog, settings), None),
    ]
    shared_options = [
        'public class ServerOptions { public const string SectionName = "OAuth"; public CertificateOptions[] Certificates { get; set; } }',
        'public class ResourceOptions { public const string SectionName = "OAuth"; public ResourceItemOptions[] ApiResources { get; set; } }',
        'public class CertificateOptions { public string Path { get; set; } } public class ResourceItemOptions { public string Scope { get; set; } }',
        'public class NamedOptions { public const string SectionPrefix = "Shared"; public string BaseAddress { get; set; } }',
        'public class FixedOptions { public const string SectionName = "Shared"; public int Retries { get; set; } }',
    ]
    for label, sources in [('forward', shared_options), ('reverse', list(reversed(shared_options)))]:
        shared_catalog = options_catalog(sources)
        for name, key, expected in [
            ('server path', 'OAuth__Certificates__0__Path', None),
            ('resource path', 'OAuth__ApiResources__0__Scope', None),
            ('unknown certificate property', 'OAuth__Certificates__0__Unknown', 'Unknown is not a property'),
            ('cross-type certificate property', 'OAuth__Certificates__0__Scope', 'Scope is not a property of CertificateOptions'),
            ('cross-type resource property', 'OAuth__ApiResources__0__Path', 'Path is not a property of ResourceItemOptions'),
            ('shared named section', 'Shared__Identity__BaseAddress', None),
            ('shared fixed section', 'Shared__Retries', None),
            ('unknown named property', 'Shared__Identity__Unknown', 'Unknown is not an Options property'),
        ]:
            cases.append((f'{label}: {name}', check_configuration_key(key, shared_catalog, settings), expected))
    failures = [f'{name}: expected {expected!r}, got {actual!r}' for name, actual, expected in cases
                if (expected is None) != (actual is None) or (expected and expected not in actual)]
    compose = 'services:\n  api:\n    image: ${IMAGE:-api}\n    environment:\n      - A__B=${SMTP_HOST:?required}\n      # - C__D=${COMMENTED}\n'
    variable_cases = [
        ('variables match', check_compose_variables(compose, 'SMTP_HOST=\n# IMAGE=api\n'), []),
        ('missing variable', check_compose_variables(compose, '# IMAGE=api\n'), ['compose variable missing from .env.example: SMTP_HOST']),
        ('extra variable', check_compose_variables(compose, 'SMTP_HOST=\n# IMAGE=api\nUNUSED=\n'), ['.env.example variable not used by compose: UNUSED']),
    ]
    logging_cases = [
        ('framework category', ['Serilog__MinimumLevel__Override__Microsoft.AspNetCore'], []),
        ('project namespace prefix', ['Serilog__MinimumLevel__Override__Generation.Probe'], []),
        ('package category', ['Serilog__MinimumLevel__Override__Leistd.OperationRecords'], []),
        ('invalid category', ['Serilog__MinimumLevel__Override__MyProject'], ['MyProject']),
    ]
    structure = '# 项目目录\n\n## 1. 项目根\n\n```text\n{project-root}/\n├── .agents/skills/  # x\n├── backend/\n└── README.md\n```\n'
    root_cases = [
        ('documented root', check_root_entries(['.agents', '.gitignore', 'backend', 'README.md'], structure), []),
        ('extra root file', check_root_entries(['.agents', 'backend', 'README.md', 'VERSION'], structure), ['VERSION']),
        ('missing root entry', check_root_entries(['.agents', 'README.md'], structure), ['backend']),
    ]
    for name, actual, expected in variable_cases:
        if actual != expected:
            failures.append(f'{name}: expected {expected}, got {actual}')
    for name, keys, expected in logging_cases + [(n, None, e) for n, _, e in root_cases]:
        actual = check_logging_categories(keys, {'Generation.Probe.Api'}, ['Leistd.OperationRecords.Core']) if keys else next(a for n2, a, _ in root_cases if n2 == name)
        if len(actual) != len(expected) or not all(word in problem for word, problem in zip(expected, actual)):
            failures.append(f'{name}: expected {expected}, got {actual}')
    if failures:
        print('FAIL: deployment asset self-test')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    total = len(cases) + len(variable_cases) + len(logging_cases) + len(root_cases)
    print(f'PASS: deployment asset self-test ({total} cases)')
    return 0


# 已核对的 verify.ps1 变体：键为脚本内容与步骤清单，值写入生成证明（步骤与失败传播）
VERIFY_CHECKED = {}
SHAPE_SHARDS = 3
VERIFY_LOCK = threading.Lock()
SHIM_COMMANDS = ('dotnet', 'npm', 'python3', 'py')


def expected_verify_steps(values):
    steps = ['check-error-codes']
    steps += ['check-i18n'] if values['IncludeLocalization'] else []
    steps += ['check-operation-action-i18n'] if values['SpaFrontend'] and values['IncludeOperationRecords'] else []
    steps += ['check-test-diagnostics', 'backend-restore', 'backend-build', 'backend-format', 'backend-test:Generation.Probe.UnitTests', 'backend-test:Generation.Probe.IntegrationTests']
    steps += ['frontend-install', 'frontend-lint', 'frontend-test', 'frontend-build'] if values['SpaFrontend'] else []
    steps += ['frontend-e2e'] if values['LocalIdentity'] else []
    return steps


def expected_fast_steps(values):
    """-Fast：静态检查、还原与构建、单元测试、前端 lint；不含集成测试、安装、前端单测、构建与端到端。"""
    excluded = {'backend-test:Generation.Probe.IntegrationTests', 'frontend-install', 'frontend-test', 'frontend-build', 'frontend-e2e'}
    return [step for step in expected_verify_steps(values) if step not in excluded]


def check_gitlab_ci(text, spa_frontend):
    """按 YAML 实际解析 GitLab 薄壳：语法错误直接失败，再核对单一 verify 作业、DinD 与 Testcontainers 连接变量。"""
    try:
        import yaml
    except ImportError as error:
        raise SystemExit('需要 PyYAML 解析生成的 .gitlab-ci.yml（python -m pip install PyYAML==6.0.3）') from error
    document = yaml.safe_load(text)
    audit_expected = ['frontend-audit'] if spa_frontend else []
    assert isinstance(document, dict) and list(document) == ['stages', 'verify'] + audit_expected, f'GitLab CI top-level keys: {document and list(document)}'
    assert document['stages'] == ['verify'], f'GitLab CI stages: {document["stages"]}'
    job = document['verify']
    assert job['stage'] == 'verify', 'GitLab CI job stage'
    assert str(job['image']).startswith('mcr.microsoft.com/dotnet/sdk:'), f'GitLab CI image: {job["image"]}'
    services = [s for s in job['services'] if isinstance(s, dict) and s.get('name') == 'docker:dind']
    assert services and services[0].get('command') == ['--tls=false'], 'GitLab CI DinD service with --tls=false'
    variables = job['variables']
    assert variables.get('DOCKER_HOST') == 'tcp://docker:2375' and variables.get('DOCKER_TLS_CERTDIR') == '', 'Testcontainers DinD variables'
    assert isinstance(job.get('before_script'), list) and job['before_script'], 'GitLab CI before_script'
    assert job['script'] == ['pwsh -NoProfile -File scripts/verify.ps1'], f'GitLab CI script: {job["script"]}'
    # 缓存：NuGet 必有，npm 随前端；key 取依赖文件，只缓存下载内容；合并请求只读、默认分支读写
    caches = {entry['key']['prefix']: entry for entry in job['cache']}
    assert list(caches) == (['nuget', 'npm'] if spa_frontend else ['nuget']), f'GitLab CI caches: {list(caches)}'
    assert caches['nuget']['key']['files'] == ['backend/Directory.Packages.props'] and caches['nuget']['paths'] == ['.nuget/packages'], 'GitLab CI NuGet cache'
    assert variables.get('NUGET_PACKAGES') == '$CI_PROJECT_DIR/.nuget/packages', 'GitLab CI NuGet cache must live inside the project directory'
    if spa_frontend:
        assert caches['npm']['key']['files'] == ['frontend/package-lock.json'] and caches['npm']['paths'] == ['.npm'], 'GitLab CI npm cache'
        assert variables.get('npm_config_cache') == '$CI_PROJECT_DIR/.npm', 'GitLab CI npm cache must live inside the project directory'
    assert all(entry['policy'] == '$CACHE_POLICY' for entry in caches.values()), 'GitLab CI cache policy'
    policies = {rule['if']: rule.get('variables', {}).get('CACHE_POLICY') for rule in job['rules']}
    assert policies == {'$CI_PIPELINE_SOURCE == "schedule"': None, '$CI_PIPELINE_SOURCE == "merge_request_event"': 'pull',
                        '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH': 'pull-push'}, f'GitLab CI cache policy by pipeline: {policies}'
    if spa_frontend:
        audit = document['frontend-audit']
        assert audit['stage'] == 'verify' and 'npm audit --omit=dev --audit-level=high --package-lock-only' in '\n'.join(audit['script']), 'GitLab CI audit job'
        changes = [rule for rule in audit['rules'] if 'changes' in rule]
        assert len(changes) == 1 and changes[0]['changes'] == ['frontend/package-lock.json'], 'GitLab CI audit must follow the lock file'
        assert any('"schedule"' in rule['if'] and '"web"' in rule['if'] for rule in audit['rules']), 'GitLab CI audit schedule and manual runs'


def verify_key(output, values):
    """verify.ps1 变体的身份：脚本内容摘要与预期步骤清单（形态片记录，对照片据此证明已覆盖）。"""
    script = output / 'scripts/verify.ps1'
    return {'Script': hashlib.sha256(script.read_bytes()).hexdigest(), 'Steps': expected_verify_steps(values)}


def check_verify_script(output, values):
    """scripts/verify.ps1：两种模式的步骤清单符合启用的能力；注入失败时以该步退出码退出，后续步骤不执行；
    -Fast 恰好执行它列出的步骤，有前端而缺 node_modules 时不执行任何步骤即失败。

    命令换成记录调用的替身（PATH 最前），判据只看实际被调用的命令序列与退出码。
    同一脚本内容与相同相关取值只核对一次。
    """
    script = output / 'scripts/verify.ps1'
    expected = expected_verify_steps(values)
    expected_fast = expected_fast_steps(values)
    key = (hashlib.sha256(script.read_bytes()).hexdigest(), tuple(expected))
    with VERIFY_LOCK:
        if key in VERIFY_CHECKED:
            return
    pwsh = shutil.which('pwsh')
    assert pwsh, 'pwsh is required to check scripts/verify.ps1'
    listed = subprocess.run([pwsh, '-NoProfile', '-File', str(script), '-List'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    assert listed.returncode == 0, f'verify -List failed: {listed.stdout}{listed.stderr}'
    listed_rows = [line.split('\t') for line in listed.stdout.splitlines() if line.strip()]
    assert {row[0] for row in listed_rows} <= {'full', 'fast'}, f'verify -List modes: {sorted({row[0] for row in listed_rows})}'
    rows = [row[1:] for row in listed_rows if row[0] == 'full']
    fast_rows = [row[1:] for row in listed_rows if row[0] == 'fast']
    assert [row[0] for row in rows] == expected, f'verify steps {[row[0] for row in rows]} != {expected}'
    assert [row[0] for row in fast_rows] == expected_fast, f'verify -Fast steps {[row[0] for row in fast_rows]} != {expected_fast}'
    if values['SpaFrontend']:
        install = next(row for row in rows if row[0] == 'frontend-install')
        assert install[2].strip() == 'npm ci --no-audit', f'frontend-install must pass --no-audit (no implicit audit request): {install}'
    assert all(row in rows for row in fast_rows), 'verify -Fast rows must be the same steps as the full mode'
    with tempfile.TemporaryDirectory(dir=output.parent) as shim_root:
        shims = Path(shim_root)
        for name in SHIM_COMMANDS:
            if os.name == 'nt':
                (shims / f'{name}.cmd').write_text('@echo off\r\necho %~n0 %*>>"%SHIM_LOG%"\r\nif "%~n0 %*"=="%SHIM_FAIL%" exit /b 7\r\nexit /b 0\r\n', encoding='utf-8')
            else:
                shim = shims / name
                shim.write_text('#!/bin/sh\nline="$(basename "$0") $*"\nprintf \'%s\\n\' "$line" >> "$SHIM_LOG"\n[ "$line" = "$SHIM_FAIL" ] && exit 7\nexit 0\n', encoding='utf-8')
                shim.chmod(0o755)
        commands = [row[2] for row in rows]
        failing = len(commands) // 2
        log = shims / 'calls.log'
        environment = dict(os.environ, PATH=str(shims) + os.pathsep + os.environ.get('PATH', ''), SHIM_LOG=str(log), SHIM_FAIL=commands[failing])
        run = subprocess.run([pwsh, '-NoProfile', '-File', str(script)], capture_output=True, text=True, encoding='utf-8', errors='replace', env=environment)
        calls = log.read_text(encoding='utf-8').splitlines() if log.exists() else []
        # -Fast：全部步骤成功时恰好调用列出的命令；有前端时先证明缺依赖会在任何步骤之前失败
        fast_environment = dict(environment, SHIM_FAIL='')
        node_modules = output / 'frontend/node_modules'
        if values['SpaFrontend']:
            assert not node_modules.exists(), 'generated output must not contain frontend/node_modules'
            missing_log = log.with_name('missing.log')
            missing = subprocess.run([pwsh, '-NoProfile', '-File', str(script), '-Fast'], capture_output=True, text=True, encoding='utf-8', errors='replace',
                                     env=dict(fast_environment, SHIM_LOG=str(missing_log)))
            assert missing.returncode == 1 and 'npm ci' in missing.stdout and not missing_log.exists(), \
                f'verify -Fast without node_modules must fail before any step: {missing.returncode} {missing.stdout}{missing.stderr}'
            node_modules.mkdir()
        try:
            fast_log = log.with_name('fast.log')
            fast = subprocess.run([pwsh, '-NoProfile', '-File', str(script), '-Fast'], capture_output=True, text=True, encoding='utf-8', errors='replace',
                                  env=dict(fast_environment, SHIM_LOG=str(fast_log)))
            fast_calls = fast_log.read_text(encoding='utf-8').splitlines() if fast_log.exists() else []
        finally:
            if values['SpaFrontend']:
                node_modules.rmdir()
    assert run.returncode == 7, f'verify must exit with the failing step code 7, got {run.returncode}: {run.stdout}{run.stderr}'
    assert calls == commands[:failing + 1], f'verify must stop at the failing step: ran {calls}, expected {commands[:failing + 1]}'
    assert fast.returncode == 0, f'verify -Fast with all steps passing must succeed: {fast.stdout}{fast.stderr}'
    assert fast_calls == [row[2] for row in fast_rows], f'verify -Fast must run exactly its listed steps: ran {fast_calls}'
    with VERIFY_LOCK:
        VERIFY_CHECKED[key] = {'Key': verify_key(output, values), 'Steps': expected, 'FailingStep': rows[failing][0],
                               'ExitCode': run.returncode, 'Calls': len(calls)}


def validate(output, values, config, verify=True, sdk=None):
    digests = {}
    for path in sorted(output.rglob('*')):
        if not path.is_file():
            continue
        content = path.read_bytes()
        relative = path.relative_to(output).as_posix()
        digests[relative] = hashlib.sha256(normalize(content, config, relative)).hexdigest()
        if path.suffix == '.csproj':
            ElementTree.fromstring(content)
        if path.suffix in ('.cs', '.ts'):
            assert content.strip(), f'Empty source: {relative}'
        if path.name in ('package.json', 'package-lock.json', 'angular.json'):
            json.loads(content)
        if relative.startswith('frontend/public/i18n/') and path.suffix == '.json':
            json.loads(content)
        if path.suffix in ('.cs', '.ts', '.json', '.csproj', '.html', '.mjs', '.md', '.yml', '.ps1'):
            assert not re.search(rb'^\s*(?://|<!--|/\*)?\s*#(?:if|else|endif|elif)\b', content, re.M), f'Unprocessed condition: {relative}'
    validate_relative_modules(output, digests)
    assert MARKER in digests, 'Missing source marker'
    for script in ('scripts/template-marker.ps1', 'scripts/sync-template.ps1'):
        assert script in digests, f'Template sync script must ship with every shape: {script}'
    check_marker(output, values, config, sdk)
    for relative in digests:
        if relative.startswith('frontend/public/i18n/') and relative.endswith('/en.json'):
            chinese = posixpath.join(posixpath.dirname(relative), 'zh-CN.json')
            assert chinese in digests, f'Missing translation pair: {relative}'
            english_keys = translation_keys(json.loads((output / relative).read_text(encoding='utf-8')))
            chinese_keys = translation_keys(json.loads((output / chinese).read_text(encoding='utf-8')))
            assert english_keys.keys() == chinese_keys.keys(), f'Generated translation keys differ: {relative}'
            for key in english_keys:
                assert set(re.findall(r'\{\{(\w+)\}\}', english_keys[key])) == set(re.findall(r'\{\{(\w+)\}\}', chinese_keys[key])), f'Generated translation placeholders differ: {relative}/{key}'
    api = output / 'backend/src/Generation.Probe.Api'
    infra = output / 'backend/src/Generation.Probe.Infrastructure'
    frontend = output / 'frontend'
    assert frontend.exists() == values['SpaFrontend'], 'Frontend applicability'
    settings = json.loads(re.sub(r'^\s*//.*$', '', (api / 'appsettings.json').read_text(encoding='utf-8'), flags=re.M))
    assert ('Routing' in settings['Leistd'].get('MultiTenancy', {})) == (values['RemoteTokenAuth'] and values['IncludeMultiTenancy']), 'Tenant routing configuration applicability'
    readme = (output / 'README.md').read_text(encoding='utf-8')
    testing = (output / 'docs/standards/testing.md').read_text(encoding='utf-8')
    deployment = (output / 'docs/deploy/README.md').read_text(encoding='utf-8')
    invocation = (output / 'docs/standards/service-invocation.md').read_text(encoding='utf-8')
    assert ('前端使用 Angular 22。' in readme) == values['SpaFrontend'], 'Frontend introduction applicability'
    assert ('npm ' in testing) == values['SpaFrontend'], 'Frontend testing instructions applicability'
    assert ('npm start' in deployment) == values['SpaFrontend'], 'Frontend deployment instructions applicability'
    action_check = 'scripts/check-operation-action-i18n.py'
    assert (action_check in testing) == (action_check in digests), 'Testing instructions reference an excluded action checker'
    i18n_check = 'scripts/check-i18n.py'
    assert (i18n_check in digests) == values['IncludeLocalization'], 'i18n checker applicability'
    assert (i18n_check in testing) == (i18n_check in digests), 'Instructions reference an excluded i18n checker'
    assert 'pwsh scripts/verify.ps1' in readme and 'pwsh scripts/verify.ps1' in testing, 'Full regression entry is not documented'
    ci_files = {'github': '.github/workflows/ci.yml', 'gitlab': '.gitlab-ci.yml'}
    for choice, ci_file in ci_files.items():
        assert (ci_file in digests) == (values['Ci'] == choice), f'CI wrapper file set: {ci_file}'
        assert (ci_file in testing) == (values['Ci'] == choice), f'CI wrapper instructions: {ci_file}'
        if ci_file in digests:
            wrapper = (output / ci_file).read_text(encoding='utf-8')
            assert '\t' not in wrapper, f'CI wrapper YAML must not contain tabs: {ci_file}'
            assert 'scripts/verify.ps1' in wrapper, f'CI wrapper must call verify: {ci_file}'
            assert ('playwright' in wrapper) == values['SpaFrontend'], f'CI wrapper browser setup applicability: {ci_file}'
            assert ('frontend/playwright-report/' in wrapper) == values['LocalIdentity'], f'CI wrapper E2E report upload applicability: {ci_file}'
    if values['Ci'] == 'gitlab':
        check_gitlab_ci((output / ci_files['gitlab']).read_text(encoding='utf-8'), values['SpaFrontend'])
    if values['Ci'] == 'github':
        github_ci = (output / ci_files['github']).read_text(encoding='utf-8')
        assert "hashFiles('backend/Directory.Packages.props', 'backend/**/*.csproj')" in github_ci and 'path: ~/.nuget/packages' in github_ci, 'GitHub CI NuGet cache'
        assert ('cache: npm' in github_ci) == values['SpaFrontend'], 'GitHub CI npm cache applicability'
    # 前端生产依赖审计不在 verify 里：GitHub 为 verify 作业内的一步（锁文件变化、定时、手动才执行），GitLab 为独立作业
    if values['Ci'] == 'github':
        assert 'merge_group:' in github_ci, 'GitHub CI must report the required check for merge queue'
        assert ('npm audit --omit=dev --audit-level=high --package-lock-only' in github_ci) == values['SpaFrontend'], 'GitHub audit step applicability'
        assert ('schedule:' in github_ci) == values['SpaFrontend'], 'GitHub audit schedule applicability'
        if values['SpaFrontend']:
            assert 'frontend/package-lock.json' in github_ci and 'workflow_dispatch' in github_ci and '未改依赖，跳过审计' in github_ci, 'GitHub audit conditions'
    assert not (output / '.github/workflows/audit.yml').exists(), 'GitHub audit must not be a separate workflow'
    verify_text = (output / 'scripts/verify.ps1').read_text(encoding='utf-8')
    assert 'npm audit' not in verify_text, 'verify.ps1 must not run the dependency audit'
    assert not any(p.startswith('.github/') and p != ci_files['github'] for p in digests), 'Unexpected files under .github'
    # 浏览器端到端随本地身份形态下发（有登录界面与可预置账号）；测试规范的命令说明同进退
    assert ('frontend/playwright.config.ts' in digests) == values['LocalIdentity'], 'E2E project applicability'
    assert ('npm run e2e' in testing) == values['LocalIdentity'], 'E2E command instructions applicability'
    if verify:
        check_verify_script(output, values)
    assert ('单实例配 `KeysPath`' in deployment) == values['SpaFrontend'], 'Browser session deployment prerequisite applicability'
    assert ('/api/v1/auth/signin' in invocation) == (values['OpenIddictServer'] or values['ResourceBrowserSession']), 'Browser relying-party instructions applicability'
    assert ('tenant-routing.read' in invocation) == values['IncludeMultiTenancy'], 'Tenant machine scope instructions applicability'
    examples = re.findall(r'```json\s*\n(.*?)\n```', invocation, re.S)
    assert examples, 'Missing service invocation configuration example'
    for example in examples:
        documented = json.loads(example)
        authentication = documented.get('Authentication')
        assert (authentication is not None) == values['RemoteTokenAuth'], 'Resource authentication example applicability'
        if authentication is not None:
            assert ('ClientId' in authentication) == values['ResourceBrowserSession'], 'Browser client credentials example applicability'
        assert ('Identity' in documented['Leistd']['ServiceClients']) == (values['RemoteTokenAuth'] and values['IncludeMultiTenancy']), 'Tenant routing client example applicability'
    backend_readme = (output / 'backend/README.md').read_text(encoding='utf-8')
    infrastructure_project = (infra / 'Generation.Probe.Infrastructure.csproj').read_text(encoding='utf-8')
    migrator_protection = values['LocalIdentity'] and values['IncludeMultiTenancy']
    assert ('API 与 `DbMigrator` 必须共用密钥环' in backend_readme) == migrator_protection, 'Migrator key-sharing instructions applicability'
    assert ('API 与 DbMigrator 必须共享密钥环' in infrastructure_project) == migrator_protection, 'Migrator key-sharing package comment applicability'
    development_compose = (output / 'deploy/docker-compose.dev.yml').read_text(encoding='utf-8')
    assert ('mailpit' in development_compose) == values['Email'], 'Development mail dependency applicability'
    assert (api / 'Controllers/TenantController.cs').exists() == values['Impersonation'], 'Impersonation controller'
    assert (infra / 'Persistence/Migrations/Control').exists() == (values['LocalIdentity'] and values['IncludeMultiTenancy']), 'Control migrations'
    assert (infra / 'Persistence/IdentityControlDbContext.cs').exists() == (values['LocalIdentity'] and values['IncludeMultiTenancy']), 'Control context'
    assert (api / 'Controllers/SettingController.cs').exists() == values['Email'], 'Email test controller'
    assert (output / 'backend/src/Generation.Probe.DbMigrator/Runners/ResourceAdminBootstrapRunner.cs').exists() == values['RemoteTokenAuth'], 'Resource bootstrap'
    deployment_problems = check_deployment(output)
    assert not deployment_problems, 'Deployment assets: ' + '; '.join(deployment_problems)
    error_codes = subprocess.run([sys.executable, str(output / 'scripts/check-error-codes.py')], capture_output=True, text=True, encoding='utf-8', errors='replace')
    assert error_codes.returncode == 0, f'Error code checker: {error_codes.stdout}{error_codes.stderr}'
    assert not (api / 'Localization').exists() or values['IncludeLocalization'], 'Localization marker type without localization'
    migrations = '\n'.join(p.read_text(encoding='utf-8') for p in (infra / 'Persistence/Migrations').rglob('*.cs'))
    assert ('name: "OperationRecords"' in migrations) == values['IncludeOperationRecords'], 'Operation record tables'
    assert 'CreationTime' in migrations and 'TenantId' in migrations, 'Baseline entity audit/scope columns'
    if values['SpaFrontend']:
        package = json.loads((frontend / 'package.json').read_text(encoding='utf-8'))
        lock = json.loads((frontend / 'package-lock.json').read_text(encoding='utf-8'))
        assert ('@microsoft/signalr' in package['dependencies']) == (values['IncludeNotifications'] or values['IncludeRealTime']), 'SignalR dependency'
        assert ('qrcode' in package['dependencies']) == values['LocalIdentity'], 'QR dependency'
        assert package['dependencies'] == lock['packages']['']['dependencies'], 'Lock runtime roots'
        assert package['devDependencies'] == lock['packages']['']['devDependencies'], 'Lock build roots'
        assert (frontend / 'src/app/features/platform/components/operation-records').exists() == values['IncludeOperationRecords'], 'History UI'
        assert (frontend / 'src/app/core/services/tenant-context-service.ts').exists() == values['IncludeMultiTenancy'], 'Tenant frontend'
        assert ('POST /api/v1/settings/email/test' in (frontend / '_mock/api/setting.ts').read_text(encoding='utf-8')) == values['Email'], 'Email mock endpoint'
        if values['OpenIddictServer']:
            scopes = (frontend / '_mock/data/open-applications.ts').read_text(encoding='utf-8')
            for scope in ('tenant-routing.read', 'tenant-migration.read'):
                assert (scope in scopes) == values['IncludeMultiTenancy'], f'Mock machine scope: {scope}'
        tenants = frontend / 'src/app/features/platform/components/tenants'
        if tenants.exists():
            assert ('onImpersonate' in (tenants / 'tenants.ts').read_text(encoding='utf-8')) == values['Impersonation'], 'Impersonation page action'
            assert ('canImpersonate' in (tenants / 'widgets/tenant-table/tenant-table.ts').read_text(encoding='utf-8')) == values['Impersonation'], 'Impersonation table action'
    return digests


def diagnose_hive_failure(hive):
    """Post-exit metadata only: a vanished owner cannot be inferred from this snapshot."""
    state = {'hive': str(hive), 'files': [], 'owners': [], 'processScan': 'unavailable'}
    paths = [hive / 'packages.json']
    try:
        paths += sorted(hive.rglob('*.lock'))
    except OSError as error:
        state['fileScanError'] = type(error).__name__
    for path in paths:
        item = {'path': str(path)}
        try:
            stat = path.stat()
            item.update(size=stat.st_size, modifiedNs=stat.st_mtime_ns, inode=stat.st_ino)
        except OSError as error:
            item['error'] = type(error).__name__
        state['files'].append(item)
    proc = Path('/proc')
    if proc.is_dir():
        state['processScan'] = 'available'
        targets = {str(path.absolute()) for path in paths}
        try:
            processes = list(proc.glob('[0-9]*'))
        except OSError as error:
            processes = []
            state['processScan'] = type(error).__name__
        for process in processes:
            try:
                descriptors = list((process / 'fd').iterdir())
            except OSError:
                continue
            held = []
            for descriptor in descriptors:
                try:
                    target = os.readlink(descriptor)
                    if target.removesuffix(' (deleted)') in targets:
                        held.append({'fd': descriptor.name, 'path': target})
                except OSError:
                    continue
            if held:
                try:
                    command = (process / 'cmdline').read_bytes().decode('utf-8', errors='replace').rstrip('\0').split('\0')
                except OSError:
                    command = []
                state['owners'].append({'pid': int(process.name), 'command': command, 'files': held})
    print('Template hive failure diagnostics: ' + json.dumps(state, ensure_ascii=True), file=sys.stderr)


def shape_digest(digest):
    """一个生成形态的整体摘要：逐文件摘要按路径排序后再取摘要。"""
    return hashlib.sha256(json.dumps(sorted(digest.items()), separators=(',', ':')).encode('utf-8')).hexdigest()


def verify_signature(config, values):
    """verify.ps1 模板源在这组取值下保留哪些行：同一签名生成同一脚本，对照片每个签名核对一次。"""
    path = 'template/scripts/verify.ps1'
    text = (ROOT / path).read_text(encoding='utf-8')
    matches = [(condition, model.file_conditions(relative, modifiers)) for relative, _, condition, modifiers in model.iter_sources(config, [path])]
    assert matches, 'verify.ps1 is not a template source'
    return tuple(any(model.generated(values, frames, condition, excludes) for condition, excludes in matches)
                 for _, frames in model.line_contexts(text)), tuple(expected_verify_steps(values))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workers', type=int, default=4)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--shard', choices=[*map(str, range(SHAPE_SHARDS)), 'control'],
                        help='0..2：按序号取模的形态片；control：输入映射、角色等价、Ci 变体与 verify 检查')
    parser.add_argument('--self-test', action='store_true', help='只运行部署资产判据的夹具自检')
    args = parser.parse_args()
    if args.self_test:
        raise SystemExit(self_test())
    if args.shard is None:
        parser.error('--shard is required (0, 1, 2 or control)')
    assert 1 <= args.workers <= 8
    gitlab_failures = self_test_gitlab_ci()
    assert not gitlab_failures, f'GitLab 薄壳判据自检失败：{gitlab_failures}'
    def source_digests():
        return {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted((ROOT / 'template').rglob('*')) if p.is_file()
                and not any(part in ('node_modules', 'bin', 'obj', '.cache') for part in p.relative_to(ROOT / 'template').parts)}
    candidate_sources = source_digests()
    config = json.loads(model.CONFIG_PATH.read_text(encoding='utf-8'))
    # Ci 只选择 CI 薄壳，不是产品能力：能力形态按默认 Ci 生成，三种取值另行比对
    default_ci = config['symbols']['Ci']['defaultValue']
    combinations = [values for values in model.all_combinations(config) if values['Ci'] == default_ci]
    groups = {}
    for values in combinations:
        groups.setdefault(model.effective_shape(values), []).append(values)
    assert len(combinations) == 768 and len(groups) == 320, 'Public parameter model changed; update its verification contract.'
    root = args.output or Path(tempfile.mkdtemp(prefix='generation-', dir=ROOT / '.tmp'))
    root = root.resolve()
    assert root.is_relative_to((ROOT / '.tmp').resolve())
    root.mkdir(parents=True, exist_ok=True)
    (root / 'generation-results.json').unlink(missing_ok=True)
    hive = root / 'hive'
    subprocess.run(['dotnet', 'new', 'install', str(ROOT / 'template'), '--debug:custom-hive', str(hive), '--force'], check=True, capture_output=True)
    # 标记记录 dotnet new 所用的 SDK：与生成命令同一工作目录取版本（同一 global.json）
    sdk = subprocess.check_output(['dotnet', '--version'], text=True, encoding='utf-8').strip()
    worker_state = threading.local()

    def initialize_worker():
        # dotnet new updates hive metadata during generation; concurrent processes must not share it.
        worker_hive = root / 'worker-hives' / threading.current_thread().name
        if worker_hive.exists():
            shutil.rmtree(worker_hive)
        shutil.copytree(hive, worker_hive)
        worker_state.hive = worker_hive

    def generate(item, verify=True):
        index, values = item
        output = root / 'generated' / str(index)
        if output.exists():
            shutil.rmtree(output)
        active_hive = getattr(worker_state, 'hive', hive)
        result = subprocess.run(['dotnet', 'new', 'fullstack-app', '-n', 'Generation.Probe', '-o', str(output), *cli_arguments(config, values), '--debug:custom-hive', str(active_hive)], capture_output=True, text=True, encoding='utf-8', errors='replace')
        if result.returncode:
            diagnose_hive_failure(active_hive)
        assert result.returncode == 0, f'{index}: {result.stdout}\n{result.stderr}'
        try:
            digest = validate(output, values, config, verify, sdk)
        except Exception as exc:
            raise AssertionError(f'{index}: {dict(zip(model.EFFECTIVE_DIMENSIONS, model.effective_shape(values)))}: {exc}') from exc
        return index, digest

    representatives = [group[0] for group in groups.values()]
    report = {'Version': 1, 'Result': 'pass', 'Shard': args.shard, 'ShapeShards': SHAPE_SHARDS,
              'CandidateSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True, encoding='utf-8', errors='replace').strip(),
              'TemplateSourceDigests': candidate_sources, 'EffectiveShapes': len(groups)}
    if args.shard != 'control':
        # 形态片：只生成序号落在本片的代表形态；verify.ps1 只记身份，执行核对归对照片
        indexes = [i for i in range(len(representatives)) if i % SHAPE_SHARDS == int(args.shard)]
        def generate_shape(index):
            _, digest = generate((index, representatives[index]), verify=False)
            output = root / 'generated' / str(index)
            return {'Index': index, 'Effective': list(model.effective_shape(representatives[index])), 'Files': len(digest),
                    'Digest': shape_digest(digest), 'VerifyKey': verify_key(output, representatives[index])}
        with ThreadPoolExecutor(max_workers=args.workers, initializer=initialize_worker) as pool:
            shapes = list(pool.map(generate_shape, indexes))
        report['Shapes'] = shapes
        summary = f'shard {args.shard}: {len(shapes)} generated shapes of {len(groups)}'
    else:
        # 对照片：自行重新生成所需的代表形态，不引用形态片的产物。先确定全部工作项与编号，再并行生成。
        role_variants = []
        # Both sparse and all-feature endpoints of each role exercise all ignored input values.
        for role in ('Identity', 'Standalone', 'Resource'):
            indexes = [i for i, v in enumerate(representatives) if v['ServiceRole'] == role]
            for index in (indexes[0], indexes[-1]):
                for variant in groups[model.effective_shape(representatives[index])][1:]:
                    role_variants.append((len(representatives) + len(role_variants), variant, index, role))
        # Ci 取值只改变 CI 薄壳与描述它的文档；有无前端两类形态各比一次
        ci_items = []
        for frontend in (True, False):
            index = next(i for i, v in enumerate(representatives) if v['SpaFrontend'] == frontend)
            for choice in [c['choice'] for c in config['symbols']['Ci']['choices'] if c['choice'] != default_ci]:
                ci_items.append((len(representatives) + len(role_variants) + len(ci_items), dict(representatives[index], Ci=choice), index, choice))
        # verify.ps1：每个模板级签名（保留的行与预期步骤）取一个代表形态核对步骤清单与失败传播
        signatures = {}
        for index, values in enumerate(representatives):
            signatures.setdefault(verify_signature(config, values), index)
        needed = sorted({index for _, _, index, _ in role_variants + ci_items} | set(signatures.values()))
        with ThreadPoolExecutor(max_workers=args.workers, initializer=initialize_worker) as pool:
            digests = dict(pool.map(lambda index: generate((index, representatives[index])), needed))
            role_digests = dict(pool.map(lambda item: generate((item[0], item[1]), verify=False), role_variants))
            ci_digests = dict(pool.map(lambda item: generate((item[0], item[1])), ci_items))
        comparisons = []
        for variant_id, _, index, role in role_variants:
            assert role_digests[variant_id] == digests[index], f'Ignored role parameter changed actual bytes: {role}/{variant_id}'
            comparisons.append({'variant': variant_id, 'representative': index, 'role': role, 'Digest': shape_digest(role_digests[variant_id])})
        ci_variants = []
        ci_dependent = {'.github/workflows/ci.yml', '.gitlab-ci.yml', 'README.md', 'docs/standards/testing.md', 'docs/standards/project-structure.md'}
        for variant_id, _, index, choice in ci_items:
            digest, expected = ci_digests[variant_id], digests[index]
            changed = {path for path in digest.keys() | expected.keys() if digest.get(path) != expected.get(path)}
            assert changed - ci_dependent == set(), f'Ci={choice} changed files outside the CI wrapper: {sorted(changed - ci_dependent)}'
            ci_variants.append({'variant': variant_id, 'representative': index, 'ci': choice, 'changed': sorted(changed)})
        # 汇总要求两者相等：多出的变体说明签名没有区分出实际不同的脚本
        assert len(VERIFY_CHECKED) == len(signatures), f'verify.ps1 variants checked {len(VERIFY_CHECKED)} != signatures {len(signatures)}'
        report.update({'RawInputs': len(combinations),
                       'InputMapping': [{'input': cli_arguments(config, v), 'effective': list(model.effective_shape(v))} for v in combinations],
                       'Representatives': [{'Index': index, 'Effective': list(model.effective_shape(representatives[index])),
                                            'Digest': shape_digest(digest)} for index, digest in sorted(digests.items())],
                       'EquivalenceComparisons': comparisons, 'CiVariants': ci_variants,
                       'VerifySignatures': len(signatures), 'VerifyChecks': list(VERIFY_CHECKED.values())})
        summary = (f'control: {len(combinations)} input mappings, {len(comparisons)} actual equivalence comparisons, '
                   f'{len(ci_variants)} Ci variants, {len(VERIFY_CHECKED)} verify.ps1 variants for {len(signatures)} signatures')
    assert candidate_sources == source_digests(), 'Template source changed during generation; evidence does not describe one candidate.'
    (root / 'generation-results.json').write_text(json.dumps(report, indent=1), encoding='utf-8')
    print(f'PASS: {summary}: {root}')


if __name__ == '__main__':
    main()
