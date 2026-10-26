#!/usr/bin/env python3
"""本项目的多语言资源、引用与展示文案必须一致。

漏译、裸键、占位符错位都不会让测试变红：页面照常渲染，只是某种语言下显示成键名、
半截英文或写死的中文。这道闸门把它们变成失败。

语言清单：
  0. 前端 `LANG_OPTIONS` 与后端 `SettingConstant.Display.SupportedLanguages` 同集同序（首项为默认语言），
     且含源语言英文；下面各条对清单里的每种语言执行，脚本不另写语言。
前端（有 `frontend/` 时）：
  1. 全局与各 scope 的每种语言都是合法 JSON，键集合与英文一致，值为非空字符串；
     插值只用 Transloco 的 `{{name}}`，插值名集合与英文一致；全局与 scope 键不重名。
  2. 模板与 TS 里的静态引用（`t('…')`、结构指令 prefix、`translateSignal`、`transloco.translate`、
     `| transloco`）都有词条，`translateObjectSignal` 的对象前缀下至少有一条词条；
     Signal Forms 用到的校验类型在 `validation` 段有句子。
  3. 从路由 loadComponent/component 进入，沿模板 selector 追溯嵌入组件，
     每个入口（含继承父路由与 loadChildren 的登记）都用 provideTranslocoScope 登记了整棵组件树需要的 scope。
后端：
  4. `backend/src/*.Api/Resources` 的每种语言是合法 JSON，`culture` 与文件名一致，
     `texts` 键集合与英文一致，同一键的 `{Name}` 占位符集合一致。
  5. Domain、Application 的 `*ErrorCodes.cs` 常量在资源里有句子；码的形态、归属与引用
     由不随本地化裁剪的 `scripts/check-error-codes.py` 检查。
  6. `[Display(Name = "…")]` 与 `ErrorMessage = "…"` 的原文都是资源键；`Dtos/` 下的校验特性必须显式写 ErrorMessage
     （不写时落成 .NET 内置英文，本地化查不到）。
两端：
  7. 源码（不含注释与单测）不写死中文展示文案；刻意保留的登记在 HARDCODED_CJK_ALLOWED。
  8. 非英文词条与英文原文相同即视为没翻译；去掉插值后没有字母的不算。确实不用翻译的逐条登记在
     UNTRANSLATED_ALLOWED（语言、资源、键与理由），不按语言或目录整体豁免；译好后登记即失效，须删掉。

动态键、非字面量路由与登记表达式不在静态判据之内，新用法要补检查。

用法：`python3 scripts/check-i18n.py`；判据自检：`python3 scripts/check-i18n.py --self-test`。
"""
import json
from pathlib import Path
import re
import sys
import tempfile

# 脚本所在目录的上一级就是被检查的根：生成项目里是项目根，本仓里是 template/。
ROOT = Path(__file__).resolve().parents[1]
# 源语言：其他语言的键、占位符都与它比较，与它相同的值视为没翻译。
SOURCE = 'en'
LANGUAGE_SERVICE = 'frontend/src/app/core/services/language-service.ts'

# 与英文相同、但确实不用翻译的词条：(语言, 资源, 键) → 理由。资源是前端 scope 路径（全局词条写 global）
# 或 backend。生成项目缺少某个功能时，对应的键不存在，登记照常保留。
UNTRANSLATED_ALLOWED = {
    ('zh-CN', 'account', 'sessions.ip'): 'IP 是通用缩写',
    ('zh-CN', 'openApp', 'appType.web'): '应用类型的通用叫法',
    ('zh-CN', 'openApp', 'field.clientId'): 'OAuth 协议字段名',
    ('zh-CN', 'settings', 'externalLoginProviders.clientId'): 'OAuth 协议字段名',
    ('zh-CN', 'backend', 'Client ID'): 'OAuth 协议字段名',
}

# 语言切换菜单里每种语言用自己的文字显示，不随界面语言变化。
HARDCODED_CJK_ALLOWED = {
    'frontend/src/app/core/services/language-service.ts',
}


def source_json(text):
    """去掉模板条件指令行后解析；普通注释与非法 JSON 仍然失败。

    模板源码里两个分支的键同时存在，生成项目里已是严格 JSON，两边都能直接解析。
    """
    text = re.sub(r'(?m)^[ \t]*//[ \t]*#(?:if|elif|else|endif)\b[^\n]*(?:\n|$)', '', text)
    return json.loads(text)


def flatten(node, prefix=''):
    result = {}
    for key, value in node.items():
        name = f'{prefix}.{key}' if prefix else key
        if isinstance(value, dict):
            result.update(flatten(value, name))
        else:
            result[name] = value
    return result


def source_files(directory, suffixes):
    """项目源码文件：跳过构建产物、依赖与单测。"""
    if not directory.is_dir():
        return []
    return sorted(
        p for p in directory.rglob('*')
        if p.is_file() and p.suffix in suffixes and '.spec.' not in p.name
        and not {'bin', 'obj', 'node_modules'} & set(p.relative_to(directory).parts))


def relative(path, root):
    return path.relative_to(root).as_posix()


# ---------------------------------------------------------------- 语言清单


def declared_locales(root):
    """两端各自声明的语言清单。返回 (清单, 问题)；两端不一致时以后端为准继续检查。"""
    errors = []
    backend = None
    constants = sorted((root / 'backend/src').glob('*.Application/Settings/Provider/SettingConstant.cs'))
    if len(constants) == 1:
        found = re.search(r'SupportedLanguages\s*=\s*\[([^\]]*)\]', constants[0].read_text(encoding='utf-8'))
        backend = re.findall(r'"([^"]+)"', found[1]) if found else None
    if not backend:
        errors.append('backend: SettingConstant.Display.SupportedLanguages not found')
    frontend = None
    if (root / 'frontend').is_dir():
        path = root / LANGUAGE_SERVICE
        text = path.read_text(encoding='utf-8') if path.is_file() else ''
        found = re.search(r'export const LANG_OPTIONS\s*=\s*\[(.*?)\]\s*as const', text, re.S)
        frontend = re.findall(r"\bid:\s*'([^']+)'", found[1]) if found else None
        if not frontend:
            errors.append(f'{LANGUAGE_SERVICE}: LANG_OPTIONS not found')
    if backend and frontend and backend != frontend:
        errors.append(f'language lists differ (same languages in the same order, the first is the default): '
                      f'frontend LANG_OPTIONS {frontend}, backend SupportedLanguages {backend}')
    locales = backend or frontend or []
    if locales and SOURCE not in locales:
        errors.append(f'language list must include the source language {SOURCE}: {locales}')
    for key, reason in UNTRANSLATED_ALLOWED.items():
        if not (isinstance(reason, str) and reason.strip()):
            errors.append(f'UNTRANSLATED_ALLOWED{key}: give the reason it needs no translation')
    return locales, errors


def untranslated(lang, resource, entries, source, placeholder):
    """`lang` 里与英文原文相同的词条，以及已经译好、不再需要的例外登记。"""
    errors = []
    for key, value in entries.items():
        allowed = (lang, resource, key) in UNTRANSLATED_ALLOWED
        same = isinstance(value, str) and value == source.get(key)
        if same and not allowed and re.search(r'[^\W\d_]', placeholder.sub('', value)):
            errors.append(f'{resource}/{lang}:{key}: same as English; translate it, or register the exact key '
                          f'with a reason in UNTRANSLATED_ALLOWED')
        elif allowed and not same:
            errors.append(f'{resource}/{lang}:{key}: stale UNTRANSLATED_ALLOWED entry (now translated); remove it')
    return errors


# ---------------------------------------------------------------- 前端

# 不启用多语言时组件内联的英文表（模板源码里与词条并存）：表里的键不是 Transloco 引用。
TABLE = re.compile(r'const ENGLISH(_VALIDATION)?: Record<string, string> = \{(.*?)\n\};', re.S)
CALL = re.compile(r"\bt\(\s*'([\w.]+)'(?!\s*\+)")
TS_CALL = re.compile(r"(?:transloco\.translate|\btranslateSignal)\(\s*'([\w.]+)'(?!\s*\+)")
OBJECT = re.compile(r"\btranslateObjectSignal\(\s*'([\w.]+)'")
PIPE = re.compile(r"'([\w.]+)'\s*\|\s*transloco")
FRONTEND_PLACEHOLDER = re.compile(r'\{\{[^}]+\}\}')
PREFIX = re.compile(r'''(?:prefix|read):\s*'([\w.]+)' ''', re.X)
INLINE_TEMPLATE = re.compile(r'\btemplate\s*:\s*`([^`]*)`', re.S)


def template_refs(text):
    """按结构指令的词法块还原前缀，返回 (写在模板里的键, 完整键)；全局与嵌套功能翻译可共存。"""
    stack = []
    refs = []
    tokens = re.compile(r'<(/?)([\w-]+)\b([^>]*)>|\bt\(\s*\'([\w.]+)\'(?!\s*\+)')
    for match in tokens.finditer(text):
        if match[4]:
            prefix = next((p for _, p in reversed(stack) if p is not None), '')
            refs.append((match[4], f'{prefix}.{match[4]}' if prefix else match[4]))
        elif match[1]:
            for i in range(len(stack) - 1, -1, -1):
                if stack[i][0] == match[2]:
                    del stack[i:]
                    break
        else:
            attr = match[3]
            prefix = None
            if '*transloco=' in attr:
                found = PREFIX.search(attr)
                prefix = found[1] if found else ''
            effective = prefix if prefix is not None else next((p for _, p in reversed(stack) if p is not None), '')
            for call in CALL.finditer(attr):
                refs.append((call[1], f'{effective}.{call[1]}' if effective else call[1]))
            if not attr.rstrip().endswith('/') and match[2] not in {'input', 'img', 'br', 'hr', 'meta', 'link'}:
                stack.append((match[2], prefix))
    return refs


def source_parts(text, separator=','):
    """只在顶层切分 TS 属性，跳过字符串、注释及嵌套括号。"""
    pieces, stack = [], []
    start, index = 0, 0
    while index < len(text):
        char = text[index]
        if text.startswith('//', index):
            end = text.find('\n', index)
            index = len(text) if end < 0 else end
            continue
        if text.startswith('/*', index):
            end = text.find('*/', index + 2)
            index = len(text) if end < 0 else end + 2
            continue
        if char in "'\"`":
            quote = char
            index += 1
            while index < len(text) and text[index] != quote:
                index += 2 if text[index] == '\\' else 1
        elif char == separator and not stack:
            pieces.append(text[start:index])
            start = index + 1
        elif char in '({[':
            stack.append(char)
        elif char in ')}]':
            if stack:
                stack.pop()
        index += 1
    return [*pieces, text[start:]]


def scope_bindings(text, context=None):
    """登记只读自官方 provider 调用，alias 映射同样由登记推导。"""
    bindings = {}
    for call in re.finditer(r'provideTranslocoScope\(', text):
        arguments = source_parts(text[call.end():], separator=')')[0]
        for argument in source_parts(arguments):
            argument = argument.strip()
            literal = re.fullmatch(r"'([\w/-]+)'", argument)
            if literal:
                bindings[literal[1]] = literal[1]
                continue
            if not argument.startswith('{') and context:
                constant = re.search(r'\bconst\s+' + re.escape(argument) + r'\s*(?::\s*[\w.<>\[\],\s]+)?=\s*\{', context)
                if constant:
                    argument = '{' + source_parts(context[constant.end():], separator='}')[0] + '}'
            scope = re.search(r"\bscope:\s*'([\w/-]+)'", argument)
            alias = re.search(r"\balias:\s*'([\w-]+)'", argument)
            if scope:
                bindings[alias[1] if alias else scope[1]] = scope[1]
    return bindings


def check_route_scopes(frontend, scopes):
    """从路由组件沿实际模板 selector 追溯嵌入组件，逐入口核对继承的 scope 登记。"""
    sources = {p.resolve(): p.read_text(encoding='utf-8') for p in source_files(frontend / 'src', {'.ts'})}
    aliases = {alias: scope for text in sources.values() for alias, scope in scope_bindings(text, text).items()}
    components, selectors = {}, {}
    for path, text in sources.items():
        selector = re.search(r"\bselector:\s*'([^']+)'", text)
        if '@Component' not in text:
            continue
        template = '\n'.join(m[1] for m in INLINE_TEMPLATE.finditer(text))
        external = re.search(r"\btemplateUrl:\s*'([^']+)'", text)
        if external:
            template += (path.parent / external[1]).read_text(encoding='utf-8')
        refs = [full for _, full in template_refs(template)]
        refs += [m[1] for pattern in (TS_CALL, OBJECT, PIPE) for m in pattern.finditer(TABLE.sub('', text))]
        components[path] = (template, {aliases.get(key.split('.')[0], key.split('.')[0]) for key in refs} & scopes)
        if selector:
            selectors[selector[1]] = path

    def needs(path, visited=None):
        visited = set() if visited is None else visited
        if path in visited or path not in components:
            return set()
        visited.add(path)
        template, required = components[path]
        required = set(required)
        for tag in re.findall(r'<([\w-]+)\b', template):
            if tag in selectors:
                required |= needs(selectors[tag], visited)
        return required

    def resolve(path, module):
        target = path.parent / module
        return next((p.resolve() for p in (Path(str(target) + '.ts'), target / 'index.ts') if p.is_file()), None)

    def route_objects(text):
        # 顶层路由数组；其子路由通过 children 递归，不把 provider 对象误认成路由。
        for array in re.finditer(r'(?:Routes\s*=|export\s+default)\s*\[', text):
            for part in source_parts(text[array.end():], separator=','):
                part = re.sub(r'(?m)^\s*//[^\n]*', '', part).strip()
                if part.startswith('{'):
                    yield part

    def properties(route):
        # 去掉首个路由对象外壳，后面的 as Routes 不属于属性。
        closing = source_parts(route[1:], separator='}')[0]
        props = {}
        for part in source_parts(closing):
            part = re.sub(r'(?m)^\s*//[^\n]*', '', part).strip()
            match = re.match(r'(\w+)\s*:\s*(.*)', part, re.S)
            if match:
                props[match[1]] = match[2]
        return props

    route_files = {p for p in sources if p.name.endswith('.routes.ts')}
    incoming = set()
    for path in route_files:
        for child in re.finditer(r"loadChildren:\s*\(\)\s*=>\s*import\('([^']+)'\)", sources[path]):
            incoming.add(resolve(path, child[1]))
    errors = []

    def walk(path, routes, inherited, chain):
        for route in routes:
            props = properties(route)
            registered = set(inherited)
            registered.update(scope_bindings(props.get('providers', ''), sources[path]).values())
            target = None
            dynamic = re.search(r"import\('([^']+)'\)", props.get('loadComponent', ''))
            if dynamic:
                target = resolve(path, dynamic[1])
            elif 'component' in props:
                name = props['component'].strip()
                for imported in re.finditer(r"import\s*\{([^}]+)\}\s*from\s*'([^']+)'", sources[path]):
                    if re.search(r'\b' + re.escape(name) + r'\b', imported[1]) and imported[2].startswith('.'):
                        target = resolve(path, imported[2])
                        break
            missing = needs(target) - registered
            if missing:
                errors.append(f'{path.name}: host route {props.get("path", "?")} missing scope registration {sorted(missing)} for {target.name}')
            children = props.get('children', '').strip()
            if children.startswith('['):
                walk(path, source_parts(children[1:-1]), registered, chain)
            child_module = re.search(r"import\('([^']+)'\)", props.get('loadChildren', ''))
            if child_module:
                child = resolve(path, child_module[1])
                if child in route_files and child not in chain:
                    walk(child, route_objects(sources[child]), registered, chain | {child})

    for path in route_files - incoming:
        walk(path, route_objects(sources[path]), set(), {path})
    return errors


def load_frontend_catalog(frontend, locales):
    """读取全局与 scope 词条，返回 (英文完整键→值（含 alias 展开）, scope 名集合, 问题)。"""
    errors = []
    resources = frontend / 'public/i18n'
    if not resources.is_dir():
        return {}, set(), [f'{relative(resources, frontend.parent)}: missing translation directory']
    en = {}
    dirs = [resources, *sorted(p for p in resources.rglob('*') if p.is_dir())]
    for directory in dirs:
        label = directory.relative_to(resources).as_posix()
        trees = {}
        for lang in locales:
            path = directory / f'{lang}.json'
            try:
                trees[lang] = flatten(source_json(path.read_text(encoding='utf-8')))
            except (OSError, ValueError) as error:
                errors.append(f'{label}/{lang}: invalid or missing JSON: {error}')
        if len(trees) != len(locales):
            continue
        resource = 'global' if directory == resources else label
        for lang in locales:
            if lang == SOURCE:
                continue
            if trees[SOURCE].keys() != trees[lang].keys():
                errors.append(f'{label}: scope key sets differ ({lang}): {sorted(trees[SOURCE].keys() ^ trees[lang].keys())}')
            errors += untranslated(lang, resource, trees[lang], trees[SOURCE], FRONTEND_PLACEHOLDER)
        prefix = '' if directory == resources else label.replace('/', '.') + '.'
        for lang, entries in trees.items():
            for key, value in entries.items():
                if not isinstance(value, str) or not value.strip():
                    errors.append(f'{label}/{lang}:{key}: empty or non-string translation')
                    continue
                stripped = re.sub(r'\{\{[^}]+\}\}', '', value)
                if re.search(r'\{[A-Za-z]\w*\}', stripped):
                    errors.append(f'{label}/{lang}:{key}: invalid Transloco interpolation')
        for key, value in trees[SOURCE].items():
            full = prefix + key
            if full in en:
                errors.append(f'{full}: duplicate global/scope key')
            en[full] = value
            for lang in locales:
                other = trees[lang].get(key)
                if lang != SOURCE and isinstance(value, str) and isinstance(other, str):
                    if set(re.findall(r'\{\{(\w+)\}\}', value)) != set(re.findall(r'\{\{(\w+)\}\}', other)):
                        errors.append(f'{full}: placeholders differ ({lang})')
    for path in source_files(frontend / 'src', {'.ts'}):
        text = path.read_text(encoding='utf-8')
        for alias, scope in scope_bindings(text, text).items():
            if alias != scope:
                en.update({alias + key[len(scope):]: value for key, value in list(en.items()) if key.startswith(scope + '.')})
    return en, {p.name for p in dirs if p != resources}, errors


def check_frontend(frontend, locales):
    en, scopes, errors = load_frontend_catalog(frontend, locales)
    if not en and errors:
        return errors
    for path in source_files(frontend / 'src', {'.ts', '.html'}):
        text = path.read_text(encoding='utf-8')
        code = TABLE.sub('', text)
        if path.suffix == '.html':
            refs = template_refs(text)
        else:
            refs = [ref for inline in INLINE_TEMPLATE.finditer(code) for ref in template_refs(inline[1])]
            refs += [(m[1], m[1]) for m in CALL.finditer(INLINE_TEMPLATE.sub('', code))]
        refs += [(m[1], m[1]) for pattern in (TS_CALL, PIPE) for m in pattern.finditer(code)]
        for _, key in refs:
            if key not in en:
                errors.append(f'{path.name}: missing reference {key}')
        for m in OBJECT.finditer(text):
            if not any(k.startswith(m[1] + '.') for k in en):
                errors.append(f'{path.name}: empty object prefix {m[1]}')
        if path.suffix == '.ts' and "from '@angular/forms/signals'" in text:
            kinds = set(re.findall(r"\bkind:\s*'(\w+)'", text))
            imported = re.search(r"import \{([^}]*)\} from '@angular/forms/signals'", text)
            if imported:
                kinds.update(re.findall(r'\b(required|minLength|maxLength|email|min|max)\b', imported[1]))
            if any('error:' not in m[1] for m in re.finditer(r'\bpattern\((.*?)\);', text, re.S)):
                kinds.add('pattern')
            for kind in kinds:
                if f'validation.{kind}' not in en:
                    errors.append(f'{path.name}: missing validation.{kind}')
    return errors + check_route_scopes(frontend, scopes)


# ---------------------------------------------------------------- 后端

CONSTANT = re.compile(r'public\s+const\s+string\s+([A-Za-z]\w*)\s*=\s*"([^"]+)"')
BACKEND_PLACEHOLDER = re.compile(r'\{[A-Za-z0-9_]+\}')
ANNOTATION_KEYS = (re.compile(r'Display\(Name\s*=\s*"([^"]+)"'), re.compile(r'ErrorMessage\s*=\s*"([^"]+)"'))
VALIDATION_ATTRIBUTE = re.compile(
    r'\[(Required|StringLength|MaxLength|MinLength|Range|RegularExpression|EmailAddress|Phone|Url|Compare|Length)\b(\([^\]]*\))?\]')


def without_doc_comments(text):
    # XML 文档注释里的示例不是真实引用。
    return '\n'.join(line for line in text.split('\n') if not line.lstrip().startswith('///'))


def read_resource(path, lang):
    """后端资源：顶层 culture + texts 两段。返回 (texts, 问题)。"""
    try:
        data = source_json(path.read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        return None, [f'{path}: invalid or missing JSON: {error}']
    errors = []
    if data.get('culture') != lang:
        errors.append(f'{path}: culture {data.get("culture")!r} does not match file name {lang}')
    texts = data.get('texts')
    if not isinstance(texts, dict):
        return None, errors + [f'{path}: missing texts object']
    return flatten(texts), errors


def compare_resources(label, directory, locales):
    """同一资源目录各语言的 culture、键集合与 `{Name}` 占位符，均与英文比较。返回 (英文 texts, 问题)。"""
    errors, trees = [], {}
    for lang in locales:
        texts, problems = read_resource(directory / f'{lang}.json', lang)
        errors += [f'{label}: {p}' for p in problems]
        if texts is not None:
            trees[lang] = texts
    if len(trees) != len(locales):
        return None, errors
    en = trees[SOURCE]
    for lang, other in trees.items():
        if lang == SOURCE:
            continue
        if en.keys() != other.keys():
            errors.append(f'{label}: key sets differ ({lang}): {sorted(en.keys() ^ other.keys())}')
        for key in sorted(en.keys() & other.keys()):
            if isinstance(en[key], str) and isinstance(other[key], str):
                names = [set(re.findall(r'\{([A-Za-z0-9_]+)\}', text)) for text in (en[key], other[key])]
                if names[0] != names[1]:
                    errors.append(f'{label}: placeholders differ for {key}: en{sorted(names[0])} {lang}{sorted(names[1])}')
        errors += untranslated(lang, 'backend', other, en, BACKEND_PLACEHOLDER)
    return en, errors


def error_code_constants(directory):
    """`*ErrorCodes.cs` 里的 (文件, 成员名, 码)。"""
    return [(path, m[1], m[2])
            for path in source_files(directory, {'.cs'}) if path.name.endswith('ErrorCodes.cs')
            for m in CONSTANT.finditer(without_doc_comments(path.read_text(encoding='utf-8')))]


def single(pattern_root, pattern, what):
    matches = sorted(pattern_root.glob(pattern)) if pattern_root.is_dir() else []
    if len(matches) != 1:
        return None, [f'{what}: expected one {pattern}, found {len(matches)}']
    return matches[0], []


def check_backend(root, locales):
    src = root / 'backend/src'
    resources, errors = single(src, '*.Api/Resources', 'backend resources')
    if resources is None:
        return errors
    texts, errors = compare_resources('backend resources', resources, locales)
    if texts is None:
        return errors

    for layer in ('*.Domain', '*.Application'):
        for project in sorted(src.glob(layer)):
            for path, member, code in error_code_constants(project):
                if code not in texts:
                    errors.append(f'{relative(path, root)}: error code {code} has no resource entry')

    for path in source_files(src, {'.cs'}):
        name = relative(path, root)
        text = path.read_text(encoding='utf-8')
        code = without_doc_comments(text)
        for pattern in ANNOTATION_KEYS:
            for m in pattern.finditer(code):
                if m[1] not in texts:
                    errors.append(f'{name}: DataAnnotations key has no resource entry: {m[1]}')
        if 'Dtos' in path.relative_to(src).parts:
            for number, line in enumerate(text.split('\n'), 1):
                if line.lstrip().startswith('//'):
                    continue
                for m in VALIDATION_ATTRIBUTE.finditer(line):
                    if not re.search(r'ErrorMessage\s*=', m[0]):
                        errors.append(f'{name}:{number}: validation attribute without ErrorMessage: {m[0]}')
    return errors


# ---------------------------------------------------------------- 两端

CJK = re.compile(r'[\u4e00-\u9fff]')


def hardcoded_cjk(root, directory, suffixes):
    found = []
    for path in source_files(directory, suffixes):
        name = relative(path, root)
        if name in HARDCODED_CJK_ALLOWED:
            continue
        # 块注释按原行数换成空行，行号才对得上。
        text = re.sub(r'/\*[\s\S]*?\*/|<!--[\s\S]*?-->', lambda m: '\n' * m[0].count('\n'),
                      path.read_text(encoding='utf-8'))
        for number, line in enumerate(text.split('\n'), 1):
            code = re.sub(r'(^|\s)//.*$', '', line)
            if CJK.search(code):
                found.append(f'{name}:{number}: hard-coded Chinese text (move it to the language resources): {code.strip()}')
    return found


def check_project(root):
    locales, errors = declared_locales(root)
    if SOURCE not in locales:
        return errors
    frontend = root / 'frontend'
    if frontend.is_dir():
        errors += check_frontend(frontend, locales)
        errors += hardcoded_cjk(root, frontend / 'src', {'.ts', '.html'})
    errors += check_backend(root, locales)
    errors += hardcoded_cjk(root, root / 'backend/src', {'.cs'})
    return errors


# ---------------------------------------------------------------- 自检

# 本文件随模板经过生成引擎，条件记号写成字面量会被当成真指令吞掉后续内容，因此拼出来。
IF, ELSE, ENDIF = ('//' + '#' + word for word in ('if', 'else', 'endif'))


def write_fixture(root):
    """最小项目：全局与两个 scope 词条、路由登记、嵌入组件、后端资源与错误码。"""
    files = {
        'frontend/public/i18n/en.json': json.dumps({'validation': {'required': 'Required'}, 'common': {'save': 'Save'}}),
        'frontend/public/i18n/zh-CN.json': json.dumps({'validation': {'required': '必填'}, 'common': {'save': '保存'}}),
        # 条件指令行在模板源码里出现，生成后消失；两种形态都要能解析。
        'frontend/public/i18n/users/en.json': f'{{\n{IF} (Impersonation)\n"title": "Users",\n{ENDIF}\n"nested": {{"label": "Label {{{{name}}}}"}}\n}}',
        'frontend/public/i18n/users/zh-CN.json': json.dumps({'title': '用户', 'nested': {'label': '标签 {{name}}'}}),
        # 两级 scope：标签与键前缀都按 `/` 拼，Windows 上也一样
        'frontend/public/i18n/admin/en.json': json.dumps({'title': 'Admin'}),
        'frontend/public/i18n/admin/zh-CN.json': json.dumps({'title': '管理'}),
        'frontend/public/i18n/admin/audit/en.json': json.dumps({'title': 'Audit'}),
        'frontend/public/i18n/admin/audit/zh-CN.json': json.dumps({'title': '审计'}),
        'frontend/public/i18n/permissions/en.json': json.dumps({'title': 'Grant'}),
        'frontend/public/i18n/permissions/zh-CN.json': json.dumps({'title': '授权'}),
        'frontend/src/page.html': """<ng-container *transloco="let t; prefix: 'users'">{{ t('title') }}<ng-container *transloco="let t">{{ t('common.save') }}</ng-container></ng-container><example-grant />""",
        'frontend/src/page.ts': """// 注释里的中文不算展示文案
const ENGLISH: Record<string, string> = {
  'title': 'Users',
  'common.save': 'Save',
};
const obj = translateObjectSignal('users.nested', {}, { scope: 'users' });
import { required } from '@angular/forms/signals';
@Component({ selector: 'example-page', templateUrl: './page.html' }) class Page {}
""",
        'frontend/src/grant.ts': """@Component({ selector: 'example-grant', template: `<div *transloco="let t">{{ t('permissions.title') }}</div>` }) class Grant {}""",
        'frontend/src/app.routes.ts': """export const routes: Routes = [{ path: 'page', providers: [provideTranslocoScope('users', 'permissions')], loadComponent: () => import('./page').then(m => m.Page) }];""",
        'frontend/src/app/core/services/language-service.ts': "export const LANG_OPTIONS = [\n  { id: 'en', label: 'English', short: 'EN' },\n  { id: 'zh-CN', label: '中文', short: '中' },\n] as const satisfies readonly LangMeta[];\n",
        'backend/src/Demo.Application/Settings/Provider/SettingConstant.cs': 'public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "zh-CN"];\n',
        'backend/src/Demo.Api/Resources/en.json': '{\n  "culture": "en",\n  "texts": {\n'
                                                  '    "User:NotFound": "User {Id} was not found.",\n'
                                                  f'{IF} (LocalIdentity)\n    "Name": "Name",\n{ELSE}\n    "Name": "Display name",\n{ENDIF}\n'
                                                  '    "{0} is required.": "{0} is required."\n  }\n}',
        'backend/src/Demo.Api/Resources/zh-CN.json': json.dumps({'culture': 'zh-CN', 'texts': {
            'User:NotFound': '未找到用户 {Id}。', 'Name': '名称', '{0} is required.': '{0}不能为空。'}}),
        'backend/src/Demo.Domain/Users/Errors/UserErrorCodes.cs': """public static class UserErrorCodes
{
    /// <summary>示例：<c>public const string Example = "Absent:Key";</c></summary>
    public const string NotFound = "User:NotFound";
}
""",
        'backend/src/Demo.Application/Users/Dtos/CreateUserInputDto.cs': """public record CreateUserInputDto
{
    // [Required] 注释里的示例不算
    [Display(Name = "Name")]
    [Required(ErrorMessage = "{0} is required.")]
    public string Name { get; init; } = "";
}
""",
        'backend/src/Demo.Application/Users/UserAppService.cs': 'throw new BusinessException(UserErrorCodes.NotFound, "User was not found.");\n',
    }
    for name, text in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')


def self_test():
    def edit(name, old, new):
        def apply(root):
            path = root / name
            text = path.read_text(encoding='utf-8')
            assert old in text, (name, old)
            path.write_text(text.replace(old, new), encoding='utf-8')
        return apply

    def write(name, text):
        def apply(root):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8')
        return apply

    def chain(*steps):
        def apply(root):
            for step in steps:
                step(root)
        return apply

    users_zh = 'frontend/public/i18n/users/zh-CN.json'
    users_en = 'frontend/public/i18n/users/en.json'
    routes = 'frontend/src/app.routes.ts'
    page_ts = 'frontend/src/page.ts'
    grant = 'frontend/src/grant.ts'
    backend_en = 'backend/src/Demo.Api/Resources/en.json'
    backend_zh = 'backend/src/Demo.Api/Resources/zh-CN.json'
    dto = 'backend/src/Demo.Application/Users/Dtos/CreateUserInputDto.cs'
    codes = 'backend/src/Demo.Domain/Users/Errors/UserErrorCodes.cs'
    aliased = edit(routes, "'users', 'permissions'", "'users', { scope: 'permissions', alias: 'grants' }")
    use_alias = edit(grant, 'permissions.title', 'grants.title')
    language_service = 'frontend/src/app/core/services/language-service.ts'
    setting_constant = 'backend/src/Demo.Application/Settings/Provider/SettingConstant.cs'

    def allow(entries, *steps):
        """登记例外的用例：在这个用例里临时替换 UNTRANSLATED_ALLOWED。"""
        def apply(root):
            for step in steps:
                step(root)
            return entries
        return apply

    def add_japanese(root):
        edit(language_service, "  { id: 'zh-CN'", "  { id: 'ja', label: '日本語', short: 'JA' },\n  { id: 'zh-CN'")(root)
        edit(setting_constant, '"en", "zh-CN"', '"en", "ja", "zh-CN"')(root)
        for directory in [root / 'frontend/public/i18n', *(p for p in (root / 'frontend/public/i18n').rglob('*') if p.is_dir())]:
            (directory / 'ja.json').write_text((directory / 'zh-CN.json').read_text(encoding='utf-8'), encoding='utf-8')
        text = (root / backend_zh).read_text(encoding='utf-8').replace('"culture": "zh-CN"', '"culture": "ja"')
        (root / 'backend/src/Demo.Api/Resources/ja.json').write_text(text, encoding='utf-8')

    copied_title = write(users_zh, json.dumps({'title': 'Users', 'nested': {'label': '标签 {{name}}'}}))
    selectorless = chain(edit(page_ts, "selector: 'example-page', templateUrl: './page.html'",
                              "template: `<div *transloco=\"let t\">{{ t('permissions.title') }}</div>`"))

    valid = [
        ('fixture as written', lambda root: None),
        ('scope alias', chain(aliased, use_alias)),
        ('scope registered on parent route', write(routes, """export const routes: Routes = [{ path: '', providers: [provideTranslocoScope('users', 'permissions')], children: [{ path: 'page', loadComponent: () => import('./page').then(m => m.Page) }] }];""")),
        ('scope registered through constants', chain(write(routes, "const USERS_SCOPE: ProviderScope = { scope: 'users' };\nconst PERMISSIONS_SCOPE: ProviderScope = { scope: 'permissions', alias: 'grants' };\nexport const routes: Routes = [{ path: 'page', providers: [provideTranslocoScope(USERS_SCOPE, PERMISSIONS_SCOPE)], loadComponent: () => import('./page').then(m => m.Page) }];"), use_alias)),
        ('selector-less routed component', selectorless),
        ('backend-only project', lambda root: __import__('shutil').rmtree(root / 'frontend')),
        ('generated JSON without directives', write(users_en, json.dumps({'title': 'Users', 'nested': {'label': 'Label {{name}}'}}))),
        ('third language with every resource', add_japanese),
        ('interpolation only is not text', chain(
            write(users_en, json.dumps({'title': 'Users', 'nested': {'label': '{{name}}'}})),
            write(users_zh, json.dumps({'title': '用户', 'nested': {'label': '{{name}}'}})))),
        ('registered exact exception', allow({('zh-CN', 'users', 'title'): 'brand name'}, copied_title)),
    ]
    cases = [
        # 前端词条
        ('ordinary JSON comment remains invalid', write(users_en, '// not a template directive\n{"title": "Users", "nested": {"label": "Label {{name}}"}}'), 'invalid or missing JSON'),
        ('conditional malformed JSON remains invalid', write(users_en, f'{{\n{IF} (Impersonation)\n"title": "Users"\n{ENDIF}\n"nested": {{"label": "Label {{{{name}}}}"}}\n}}'), 'invalid or missing JSON'),
        ('missing language file', lambda root: (root / users_zh).unlink(), 'invalid or missing JSON'),
        ('scope key sets', write(users_zh, '{}'), 'key sets differ'),
        ('two-level scope label', write('frontend/public/i18n/admin/audit/zh-CN.json', '{}'), 'admin/audit: scope key sets differ'),
        ('two-level scope key prefix', write('frontend/public/i18n/en.json', json.dumps({'validation': {'required': 'Required'}, 'common': {'save': 'Save'}, 'admin': {'audit': {'title': 'Audit'}}})), 'admin.audit.title: duplicate global/scope key'),
        ('empty value', write(users_zh, json.dumps({'title': '', 'nested': {'label': '标签 {{name}}'}})), 'empty or non-string translation'),
        ('non-string value', write(users_zh, json.dumps({'title': 1, 'nested': {'label': '标签 {{name}}'}})), 'empty or non-string translation'),
        ('single-brace interpolation', write(users_zh, json.dumps({'title': '用户', 'nested': {'label': '标签 {name}'}})), 'zh-CN:nested.label: invalid Transloco interpolation'),
        ('placeholders differ', write(users_zh, json.dumps({'title': '用户', 'nested': {'label': '标签'}})), 'placeholders differ'),
        ('global and scope key collide', write('frontend/public/i18n/en.json', json.dumps({'validation': {'required': 'Required'}, 'common': {'save': 'Save'}, 'users': {'title': 'Users'}})), 'duplicate global/scope key'),
        # 语言清单与未翻译
        ('language lists differ', edit(setting_constant, '"en", "zh-CN"', '"zh-CN", "en"'), 'language lists differ'),
        ('frontend list missing', write(language_service, 'export const OTHER = 1;'), 'LANG_OPTIONS not found'),
        ('source language missing', chain(edit(setting_constant, '"en", "zh-CN"', '"zh-CN"'),
                                          edit(language_service, "  { id: 'en', label: 'English', short: 'EN' },\n", '')),
         'must include the source language en'),
        ('new language without resources', chain(
            edit(language_service, "  { id: 'zh-CN'", "  { id: 'ja', label: '日本語', short: 'JA' },\n  { id: 'zh-CN'"),
            edit(setting_constant, '"en", "zh-CN"', '"en", "ja", "zh-CN"')), 'users/ja: invalid or missing JSON'),
        ('new language copied from English', chain(add_japanese, lambda root: (root / 'frontend/public/i18n/users/ja.json').write_text(
            (root / users_en).read_text(encoding='utf-8'), encoding='utf-8')), 'users/ja:nested.label: same as English'),
        ('frontend copy of English', copied_title, 'users/zh-CN:title: same as English'),
        ('backend copy of English', edit(backend_zh, '"User:NotFound": "\\u672a\\u627e\\u5230\\u7528\\u6237 {Id}\\u3002"', '"User:NotFound": "User {Id} was not found."'),
         'backend/zh-CN:User:NotFound: same as English'),
        ('exception is per language', allow({('ja', 'users', 'title'): 'brand name'}, copied_title), 'users/zh-CN:title: same as English'),
        ('exception needs a reason', allow({('zh-CN', 'users', 'title'): ' '}, copied_title), 'give the reason'),
        ('stale exception', allow({('zh-CN', 'users', 'title'): 'brand name'}), 'stale UNTRANSLATED_ALLOWED entry'),
        # 前端引用
        ('prefixed reference', edit('frontend/src/page.html', "t('title')", "t('absent')"), 'missing reference users.absent'),
        ('attribute reference', edit('frontend/src/page.html', "{{ t('title') }}", "<span [title]=\"t('absent')\"></span>"), 'missing reference users.absent'),
        ('inline template reference', edit(page_ts, 'class Page {}', "class Page {}\nconst c = { template: `<div *transloco=\"let t; prefix: 'users'\">{{ t('absent') }}</div>` };"), 'missing reference users.absent'),
        ('TS translate reference', edit(page_ts, 'class Page {}', "class Page {}\nconst s = translateSignal('users.absent');"), 'missing reference users.absent'),
        ('pipe reference', edit('frontend/src/page.html', '<example-grant />', "{{ 'users.absent' | transloco }}<example-grant />"), 'missing reference users.absent'),
        ('object prefix', edit(page_ts, "'users.nested'", "'users.absent'"), 'empty object prefix users.absent'),
        ('validation kind', edit(page_ts, 'class Page {}', "class Page {}\nconst e = { kind: 'absent' };"), 'missing validation.absent'),
        ('imported validator', edit(page_ts, '{ required }', '{ required, email }'), 'missing validation.email'),
        # 路由 scope 登记
        ('embedded scope registration', edit(routes, "'users', 'permissions'", "'users'"), "missing scope registration ['permissions'] for page.ts"),
        ('second host registration', edit(routes, '}];', "}, { path: 'other', providers: [provideTranslocoScope('users')], loadComponent: () => import('./page').then(m => m.Page) }];"), 'missing scope registration'),
        ('aliased second host registration', chain(aliased, use_alias, edit(routes, '}];', "}, { path: 'other', providers: [provideTranslocoScope('users')], loadComponent: () => import('./page').then(m => m.Page) }];")), 'missing scope registration'),
        ('selector-less route scope registration', chain(selectorless, edit(routes, "'users', 'permissions'", "'users'")), "missing scope registration ['permissions'] for page.ts"),
        # 后端资源
        ('backend invalid JSON', edit(backend_zh, '{', '{,'), 'invalid or missing JSON'),
        ('backend culture', edit(backend_zh, '"culture": "zh-CN"', '"culture": "en"'), "does not match file name zh-CN"),
        ('backend key sets', edit(backend_zh, '"Name": "\\u540d\\u79f0", ', ''), 'key sets differ'),
        ('backend placeholders', edit(backend_zh, '{Id}', '{UserId}'), 'placeholders differ for User:NotFound'),
        # 错误码（形态与引用见 check-error-codes.py）
        ('error code without resource', edit(codes, 'public const string NotFound = "User:NotFound";', 'public const string NotFound = "User:NotFound";\n    public const string Locked = "User:Locked";'), 'error code User:Locked has no resource entry'),
        # DataAnnotations
        ('display key', edit(dto, 'Display(Name = "Name")', 'Display(Name = "Full name")'), 'DataAnnotations key has no resource entry: Full name'),
        ('error message key', edit(dto, 'ErrorMessage = "{0} is required."', 'ErrorMessage = "{0} is mandatory."'), 'DataAnnotations key has no resource entry: {0} is mandatory.'),
        ('bare validation attribute', edit(dto, '[Required(ErrorMessage = "{0} is required.")]', '[Required]'), 'validation attribute without ErrorMessage: [Required]'),
        # 写死的中文
        ('Chinese in TS', edit(page_ts, 'class Page {}', "class Page { label = '保存'; }"), 'frontend/src/page.ts:8: hard-coded Chinese'),
        ('Chinese in template', edit('frontend/src/page.html', '<example-grant />', '<span>保存</span><example-grant />'), 'frontend/src/page.html:1: hard-coded Chinese'),
        ('Chinese in backend', write('backend/src/Demo.Domain/Users/Greeting.cs', '/* 块注释\n里的中文 */\nvar text = "你好";'), 'backend/src/Demo.Domain/Users/Greeting.cs:3: hard-coded Chinese'),
        ('allow-list is per file', write('frontend/src/app/core/services/other-service.ts', "const LABELS = { 'zh-CN': '中文' };"), 'other-service.ts:1: hard-coded Chinese'),
    ]

    global UNTRANSLATED_ALLOWED
    registered = UNTRANSLATED_ALLOWED
    failures = []
    for name, mutate, *expected in [(n, m) for n, m in valid] + cases:
        with tempfile.TemporaryDirectory(prefix='check-i18n-') as tmp:
            root = Path(tmp)
            write_fixture(root)
            UNTRANSLATED_ALLOWED = mutate(root) or {}
            try:
                errors = check_project(root)
            finally:
                UNTRANSLATED_ALLOWED = registered
        if expected and not any(expected[0] in e for e in errors):
            failures.append(f'{name}: expected "{expected[0]}", got {errors}')
        if not expected and errors:
            failures.append(f'{name}: expected no problem, got {errors}')
    assert template_refs("""<div *transloco="let t; read: 'users'">{{ t('title') }}</div>""") == [('title', 'users.title')]
    assert template_refs("""<div *transloco="let t; prefix: 'users'"><input [title]="t('title')" /></div>""") == [('title', 'users.title')]
    assert scope_bindings('provideTranslocoScope(SCOPE)', "const SCOPE = { scope: 'users', alias: 'people' };") == {'people': 'users'}
    if failures:
        print('FAIL: check-i18n self-test')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print(f'PASS: check-i18n self-test ({len(valid)} valid fixtures, {len(cases)} independent rule mutations)')
    return 0


def main():
    # Windows 上管道输出默认按本地代码页编码，打印 ✅ 与中文会崩溃
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8')
    if '--self-test' in sys.argv[1:]:
        return self_test()
    problems = check_project(ROOT)
    if problems:
        print(f'FAIL: i18n check found {len(problems)} problem(s):')
        for problem in problems:
            print(f'  - {problem}')
        return 1
    parts = ['backend resources, error codes and DataAnnotations keys']
    if (ROOT / 'frontend').is_dir():
        parts.insert(0, 'frontend scope resources, references and route registrations')
    print(f'PASS: {"; ".join(parts)}; no hard-coded Chinese text')
    return 0


if __name__ == '__main__':
    sys.exit(main())
