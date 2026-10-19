#!/usr/bin/env python3
"""文档引用存在性：Markdown 链接、反引号路径与（生成模式下的）命令必须指向真实存在的目标。

两种模式：

- 源码模式（无参数，check-all 执行）：扫描仓库根 `README.md`、`docs/`、`framework/docs/`、
  `.agents/skills/`、`skills/`、`template/` 下的 `.md`，只检查路径与链接。模板的
  `package.json`、`angular.json` 带条件指令，不是合法 JSON；命令一律交给生成模式，
  源码模式不解析配置，也不检查命令。
- 生成模式（`--root <生成项目>`，矩阵文档检查步骤执行）：扫描生成项目全部 `.md`，
  检查路径、链接与命令。条件裁剪后的配置是完整 JSON，命令只存在于未启用分支时，
  在对应场景中失败。

扫描排除 node_modules、libs/ui、bin、obj、dist、.angular（另排除 .git、.tmp）。源码模式另按目录
排除在途工作文档（SOURCE_EXCLUDED_DIRS）：按 docs/README.md 的分类，它们随工作结束删除，
引用的多是本次运行产物。

路径解析：

- Markdown 链接：相对链接按文档所在目录解析；以 `/` 开头的按交付根解析
  （模板文档的交付根是 `template/`，生成模式是生成项目根，其余是仓库根）。
  外链（任何 URI scheme）、纯锚点、含 `{` `}` `*` 的占位链接不检查；锚点由
  check-markdown-anchors.py 负责。
- 反引号路径：一律按交付根解析；上下文根不同的文档在 CONTEXT_ROOTS 中逐个登记。
  不做“尝试多个目录，找到就通过”。
  只有形如路径的行内代码才算：含 `/`、不以 `/` 或 `@` 开头、无空白与占位符（允许中文等非 ASCII
  字符），且满足其一：末段带已登记的文件扩展名；多段且以 `/` 结尾；首段是交付根登记的
  顶层目录（KNOWN_ROOTS）。单段的 `name/` 只有 name 在 KNOWN_ROOTS 中才算引用，其余单段
  （`widgets/`、`Dtos/`、`architecture/` 这类正文里的目录名）一律视为名字、不检查。
  是否算引用只看写法与写死的清单，不看目标是否存在：登记的顶层目录消失、或拼错的首段带着
  第二段（`scrpits/x.py`）仍会失败。已知局限：拼错的单段目录（`scrpits/`）不是引用，不会被拦下。
  `:行号` 后缀先去掉再解析；作为链接文字的行内代码不重复检查（链接目标已检查）。
- 存在性按精确大小写判定，越出交付根视为缺失（分发后读者拿不到）。

命令解析（仅生成模式）：只看行内代码与 shell 类围栏代码块。

- `npm run <脚本>`、`npm test|start|stop|restart` 对照 `frontend/package.json` 的 scripts；
  带 `--prefix <目录>` 时对照交付根下该目录的 package.json。
- `ng <执行目标>` 对照 `frontend/angular.json` 的 architect；`ng generate`、`ng new`
  等 generator 与内置命令不检查。
- `python3|python|py <脚本>.py`、`pwsh <脚本>.ps1` 按交付根解析。

失败条件：目标缺失、配置解析失败、所属项目无法确定、扫描到的文档或引用数量为零；
源码模式下另有白名单陈旧项。

用法：
  python3 scripts/check-doc-references.py                 源码模式
  python3 scripts/check-doc-references.py --root <目录>   生成模式
  python3 scripts/check-doc-references.py --self-test     规则自检
"""
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import unquote

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))

SOURCE_SCAN = ['README.md', 'AGENTS.md', 'docs', 'framework/docs', '.agents/skills', 'skills', 'template']
# 源码模式按目录排除的在途工作文档（仓库相对目录，整棵排除；不按路径内容匹配）。
SOURCE_EXCLUDED_DIRS = (
    'docs/plans',        # 实施计划：任务全部完成后删除
    'docs/assessments',  # 现状诊断与候选比较：选定方案后删除
    'docs/reports',      # 跨层验证结果：引用的是当次运行的 .tmp 产物
)
TEMPLATE_PREFIX = 'template/'
# 反引号路径带 `#锚点` 时核对锚点的文档：Skill 与 AGENTS.md（仓库层与模板层各一套）。
ANCHOR_SCOPE_PREFIXES = ('.agents/skills/', 'template/.agents/skills/')
ANCHOR_SCOPE_FILES = ('AGENTS.md', 'template/AGENTS.md')

# 交付根的顶层目录：单段 `name/` 与无扩展名、不以 / 结尾的多段路径，只有首段是这些名字之一才算引用。
# 写死而不读文件系统，是为了让“算不算引用”与“目标在不在”无关。
KNOWN_ROOTS = {
    'repository': {'framework', 'template', 'docs', 'scripts', 'skills', '.agents', '.github'},
    'template': {'backend', 'frontend', 'docs', 'scripts', 'deploy', '.agents'},
}
SKIPPED_DIR_NAMES = {'node_modules', 'bin', 'obj', 'dist', '.angular', '.git', '.tmp'}
SKIPPED_DIR_SUFFIXES = ('libs/ui',)

# 文档 → 反引号路径的解析根。键是仓库相对路径（生成模式下文档键为 `template/` + 项目相对路径），
# 值相对于该文档的交付根。只登记确实以别处为上下文的文档，其余文档写交付根相对路径。
CONTEXT_ROOTS = {
    # 子项目 README 以所在子项目为上下文（命令都在该目录下执行）
    'template/backend/README.md': 'backend',
    'template/frontend/README.md': 'frontend',
    # Spartan 是上游 Skill（仓库与模板各一份，内容一致），以 Skill 目录为上下文
    '.agents/skills/spartan/SKILL.md': '.agents/skills/spartan',
    '.agents/skills/spartan/customization.md': '.agents/skills/spartan',
    '.agents/skills/spartan/rules/brain-vs-helm.md': '.agents/skills/spartan',
    'template/.agents/skills/spartan/SKILL.md': '.agents/skills/spartan',
    'template/.agents/skills/spartan/customization.md': '.agents/skills/spartan',
    'template/.agents/skills/spartan/rules/brain-vs-helm.md': '.agents/skills/spartan',
}

# 精确匹配：文档键、引用原文与模式都相同才豁免。模式：
#   source    只豁免源码模式（如生成后才出现的文件），生成模式仍须存在；
#   generated 只豁免生成模式；
#   both      两种模式都豁免。
WHITELIST = [
    {'source': 'template/docs/deploy/README.md', 'ref': 'deploy/.env', 'mode': 'both',
     'reason': '使用者按 deploy/.env.example 自建的机密文件，不随模板分发'},
    {'source': 'template/README.md', 'ref': '.claude/skills/', 'mode': 'both',
     'reason': '使用者在项目根自建的目录链接，指向 .agents/skills'},
    {'source': 'template/docs/standards/project-structure.md', 'ref': 'docs/modules/', 'mode': 'both',
     'reason': '项目按需创建的文档目录，生成时不存在'},
    {'source': 'template/docs/standards/project-structure.md', 'ref': 'docs/requirements/', 'mode': 'both',
     'reason': '项目按需创建的文档目录，生成时不存在'},
    {'source': 'template/docs/standards/project-structure.md', 'ref': 'docs/project/', 'mode': 'both',
     'reason': '项目按需创建的文档目录，生成时不存在'},
    {'source': 'template/.agents/skills/leistd-project-workflow/references/documentation.md', 'ref': 'docs/project/', 'mode': 'both',
     'reason': '项目按需创建的文档目录，生成时不存在'},
    {'source': 'docs/framework/development-guide.md', 'ref': 'framework/ddd-struct/Leistd.Ddd.Xxx/', 'mode': 'source',
     'reason': '命名占位：Xxx 代表具体包名'},
    {'source': 'docs/framework/development-guide.md', 'ref': 'framework/artifacts', 'mode': 'source',
     'reason': 'CI 构建输出目录，不入库'},
    {'source': 'docs/framework/development-guide.md', 'ref': '.tmp/package-consumer/', 'mode': 'source',
     'reason': '包消费检查运行时创建的工作目录，不入库'},
    {'source': 'docs/framework/versioning.md', 'ref': 'framework/artifacts', 'mode': 'source',
     'reason': 'CI 构建输出目录，不入库'},
    {'source': 'docs/template/development-guide.md', 'ref': 'framework/artifacts', 'mode': 'source',
     'reason': 'CI 构建输出目录，不入库'},
    {'source': 'docs/template/quality-assurance.md', 'ref': '.cache/lint/', 'mode': 'source',
     'reason': '生成项目前端运行 lint 时创建的缓存目录，不入库'},
    {'source': 'skills/leistd-net-framework/SKILL.md', 'ref': 'obj/project.assets.json', 'mode': 'source',
     'reason': '消费方项目 restore 生成的构建产物'},
]

PATH_EXTENSIONS = {
    'md', 'ps1', 'psm1', 'py', 'mjs', 'cjs', 'js', 'ts', 'json', 'jsonc', 'yml', 'yaml', 'cs', 'csproj',
    'props', 'targets', 'sln', 'slnx', 'html', 'scss', 'css', 'xml', 'sh', 'txt', 'editorconfig',
    'toml', 'ini', 'env', 'example', 'sql', 'svg', 'png', 'ico',
}
SHELL_FENCE_INFOS = {'', 'bash', 'sh', 'shell', 'zsh', 'console', 'powershell', 'pwsh', 'ps1', 'ps', 'cmd', 'bat',
                     'text', 'txt'}

FENCE_RE = re.compile(r'^ {0,3}(`{3,}|~{3,})\s*([^`\s]*)')
LINK_RE = re.compile(r'!?\[(?P<text>[^\]]*)\]\((?P<target>[^)]+)\)')
INLINE_CODE_RE = re.compile(r'(`+)(.+?)\1')
SCHEME_RE = re.compile(r'^[A-Za-z][A-Za-z0-9+.-]*:')
PATH_SPAN_RE = re.compile(r'^(?:\.{1,2}/)*[\w.+-]+(?:/[\w.+-]+)*/?$')
# Skill 与 AGENTS.md 中带锚点的单段 Markdown 文件名（`README.md#章节`）按交付根解析；
# 不带锚点的单段名（`SKILL.md`、`quality.md`）多是 Skill 内部文件名，仍只是名字。
SINGLE_MARKDOWN_RE = re.compile(r'^[\w.+-]+\.md$')
LINE_SUFFIX_RE = re.compile(r'^(?P<path>.+?)(?::\d+(?:[-,]\d+)*|#L\d+(?:-L?\d+)?)$')

NPM_RE = re.compile(r'(?<![\w-])npm\s+(?:--prefix(?:=|\s+)(?P<prefix>[^\s|;&`]+)\s+)?'
                    r'(?:(?:run|run-script)\s+(?P<script>[A-Za-z0-9:_.-]+)|(?P<lifecycle>test|start|stop|restart)\b)')
NG_RE = re.compile(r'(?<![\w./-])ng\s+(?P<command>[a-z][a-z0-9-]*)(?:\s+(?P<arg>[^\s|;&`]+))?')
SCRIPT_RE = re.compile(r'(?<![\w-])(?P<runner>python3|python|py|pwsh)(?:\s+-[^\s]+)*\s+'
                       r'(?P<script>[^\s|;&`]+\.(?:py|ps1))(?![\w.])')

NG_NON_TARGET_COMMANDS = {'add', 'analytics', 'cache', 'completion', 'config', 'doc', 'd', 'generate', 'g', 'new', 'n',
                          'update', 'version', 'v', 'help'}
NG_TARGET_ALIASES = {'b': 'build', 's': 'serve', 'dev': 'serve', 't': 'test', 'e': 'e2e', 'xi18n': 'extract-i18n',
                     'i18n-extract': 'extract-i18n', 'deploy': 'deploy', 'build': 'build', 'serve': 'serve',
                     'test': 'test', 'lint': 'lint', 'e2e': 'e2e', 'extract-i18n': 'extract-i18n'}


class Problem:
    def __init__(self, doc_key, display, line, raw, message):
        self.doc_key = doc_key
        self.display = display
        self.line = line
        self.raw = raw
        self.message = message

    def __str__(self):
        return f'{self.display}:{self.line} `{self.raw}` —— {self.message}'


class Delivery:
    """一棵交付树：解析根与精确大小写的存在性缓存。

    tracked 给出时（源码模式），存在性只认版本库文件清单：被忽略的本地产物（如 .tmp/）在干净检出与 CI 中不存在，
    不能让它们掩盖失效引用。生成模式的产物是全新生成的目录，按文件系统判断。
    """

    def __init__(self, root, known_roots, tracked=None):
        self.root = os.path.abspath(root)
        self.known_roots = known_roots
        self._listing = {}
        self._tracked = None
        if tracked is not None:
            self._tracked = set(tracked)
            for path in tracked:
                parts = path.split('/')
                for index in range(1, len(parts)):
                    self._tracked.add('/'.join(parts[:index]))

    def _entries(self, directory):
        if directory not in self._listing:
            try:
                self._listing[directory] = set(os.listdir(directory))
            except OSError:
                self._listing[directory] = None
        return self._listing[directory]

    def relative(self, absolute):
        """交付根相对路径；越出交付根返回 None。"""
        relative = os.path.relpath(os.path.normpath(absolute), self.root)
        if relative == os.curdir:
            return ''
        if relative == os.pardir or relative.startswith(os.pardir + os.sep) or os.path.isabs(relative):
            return None
        return relative

    def exists(self, absolute):
        """按精确大小写判断存在：macOS/Windows 的文件系统不区分大小写，Linux CI 区分。"""
        relative = self.relative(absolute)
        if relative is None:
            return False
        if self._tracked is not None:
            return relative == '' or relative.replace(os.sep, '/') in self._tracked
        current = self.root
        for part in relative.split(os.sep) if relative else []:
            entries = self._entries(current)
            if entries is None or part not in entries:
                return False
            current = os.path.join(current, part)
        return True


def iter_markdown(base, relative_dirs, excluded_dirs=()):
    """产出 (绝对路径) ，按排除口径遍历。relative_dirs 中的文件直接产出；excluded_dirs 为整棵排除的相对目录。"""
    for entry in relative_dirs:
        path = os.path.join(base, entry)
        if os.path.isfile(path):
            if path.lower().endswith('.md'):
                yield path
            continue
        for directory, subdirectories, files in os.walk(path):
            relative_dir = os.path.relpath(directory, base).replace(os.sep, '/')
            prefix = '' if relative_dir == '.' else relative_dir + '/'
            subdirectories[:] = sorted(
                d for d in subdirectories
                if d not in SKIPPED_DIR_NAMES and not f'{prefix}{d}'.endswith(SKIPPED_DIR_SUFFIXES)
                and f'{prefix}{d}' not in excluded_dirs)
            for name in sorted(files):
                if name.lower().endswith('.md'):
                    yield os.path.join(directory, name)


def read_text(path):
    with open(path, encoding='utf-8') as handle:
        return handle.read()


def scan_lines(text):
    """逐行产出 (行号, 文本, 围栏信息或 None)：围栏外的行 info 为 None，围栏内为围栏语言。"""
    lines = text.splitlines()
    start = 0
    if lines and lines[0].strip() == '---':
        for closing in range(1, len(lines)):
            if lines[closing].strip() == '---':
                start = closing + 1
                break
    fence = None
    fence_info = None
    for index in range(start, len(lines)):
        line = lines[index]
        match = FENCE_RE.match(line)
        if fence:
            if match and match.group(1)[0] == fence[0] and len(match.group(1)) >= len(fence) \
                    and not line.strip().lstrip(fence[0]):
                fence = None
                continue
            yield index + 1, line, fence_info
            continue
        if match:
            fence = match.group(1)
            fence_info = match.group(2).lower()
            continue
        yield index + 1, line, None


def link_path(raw_target):
    """取出链接的路径部分（尖括号、标题、查询串、百分号编码）；不需检查时返回 None。"""
    target = raw_target.strip()
    if target.startswith('#') or SCHEME_RE.match(target):
        return None
    if target.startswith('<') and '>' in target:
        path = target[1:target.index('>')].split('#', 1)[0]
    else:
        path = target.split('#', 1)[0].strip()
        path = path.split()[0] if path.split() else ''
    path = path.split('?', 1)[0]
    if not path.strip() or re.search(r'[{}*]', path):
        return None
    return unquote(path)


def path_span(span):
    """行内代码若形如路径，返回去掉行号后缀的路径；否则 None。"""
    text = span.strip()
    suffix = LINE_SUFFIX_RE.match(text)
    if suffix and '.' in suffix.group('path').rsplit('/', 1)[-1]:
        text = suffix.group('path')
    if '/' not in text or text.startswith('/') or not PATH_SPAN_RE.match(text):
        return None
    return text


def _load_anchor_rules():
    spec = importlib.util.spec_from_file_location(
        'check_markdown_anchors', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'check-markdown-anchors.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ANCHOR_RULES = _load_anchor_rules()


def in_anchor_scope(doc_key):
    return doc_key.startswith(ANCHOR_SCOPE_PREFIXES) or doc_key in ANCHOR_SCOPE_FILES


def split_anchor(span):
    """`x.md#章节` → (`x.md`, `章节`)；其余写法原样返回、无锚点。"""
    text = span.strip()
    if '#' in text:
        path, fragment = text.split('#', 1)
        if path.lower().endswith('.md') and fragment:
            return path, unquote(fragment)
    return text, None


def is_checked_path(path, resolution_root, delivery):
    segments = [s for s in path.split('/') if s not in ('', '.')]
    if not segments:
        return False
    last = path.rstrip('/').rsplit('/', 1)[-1]
    if '.' in last.lstrip('.') or last.startswith('.'):
        extension = last.rsplit('.', 1)[-1].lower()
        if extension in PATH_EXTENSIONS:
            return True
    if path.endswith('/') and len(segments) >= 2:
        return True
    if segments[0] == '..':
        return True
    return os.path.normpath(resolution_root) == delivery.root and segments[0] in delivery.known_roots


class Config:
    """生成模式下按需加载的前端配置；缺失或解析失败时记下原因。"""

    def __init__(self, delivery):
        self.delivery = delivery
        self._cache = {}

    def load(self, relative):
        if relative not in self._cache:
            path = os.path.join(self.delivery.root, relative)
            if not self.delivery.exists(path):
                self._cache[relative] = (None, f'所属项目无法确定：{relative} 不存在')
            else:
                try:
                    self._cache[relative] = (json.loads(read_text(path)), None)
                except (ValueError, OSError) as error:
                    self._cache[relative] = (None, f'配置解析失败：{relative}：{error}')
        return self._cache[relative]

    def npm_problem(self, script, prefix=None):
        relative = 'frontend/package.json'
        if prefix:
            directory = self.delivery.relative(os.path.join(self.delivery.root, prefix))
            if directory is None:
                return f'所属项目无法确定：--prefix {prefix} 越出交付根'
            relative = (directory.replace(os.sep, '/') + '/' if directory else '') + 'package.json'
        package, error = self.load(relative)
        if error:
            return error
        scripts = package.get('scripts') if isinstance(package, dict) else None
        if not isinstance(scripts, dict):
            return f'配置解析失败：{relative} 没有 scripts 对象'
        if script not in scripts:
            return f'{relative} 没有脚本 "{script}"'
        return None

    def ng_problem(self, command, argument):
        if command in NG_NON_TARGET_COMMANDS:
            return None
        workspace, error = self.load('frontend/angular.json')
        if error:
            return error
        projects = workspace.get('projects') if isinstance(workspace, dict) else None
        if not isinstance(projects, dict) or not projects:
            return '配置解析失败：frontend/angular.json 没有 projects'
        if command == 'run':
            if not argument or ':' not in argument:
                return '所属项目无法确定：ng run 缺少 <项目>:<目标>'
            project_name, target = argument.split(':', 2)[:2]
            project = projects.get(project_name)
            if not isinstance(project, dict):
                return f'frontend/angular.json 没有项目 "{project_name}"'
        else:
            target = NG_TARGET_ALIASES.get(command)
            if target is None:
                return f'不是 Angular CLI 命令或执行目标：ng {command}'
            if len(projects) != 1:
                return f'所属项目无法确定：frontend/angular.json 有 {len(projects)} 个项目'
            project = next(iter(projects.values()))
        architect = project.get('architect') if isinstance(project, dict) else None
        if not isinstance(architect, dict) or target not in architect:
            return f'frontend/angular.json 没有执行目标 "{target}"'
        return None


def repository_files(repo_root):
    """版本库文件清单：已跟踪与未跟踪但未被忽略的文件（新建未提交的文件照样算），不含被忽略的本地产物。"""
    result = subprocess.run(['git', '-C', repo_root, 'ls-files', '-z', '--cached', '--others', '--exclude-standard'],
                            capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(f'无法列出版本库文件（git ls-files 退出码 {result.returncode}）：源码模式须在 git 工作树中运行')
    return [path for path in result.stdout.decode('utf-8').split('\0') if path]


def collect(repo_root, generated_root, context_roots, whitelist):
    """返回 (问题列表, 文档数, 引用数, 命中的白名单下标集合, 扫描到的文档键集合)。"""
    mode = 'generated' if generated_root else 'source'
    if generated_root:
        project = Delivery(generated_root, KNOWN_ROOTS['template'])
        docs = [(path, project, TEMPLATE_PREFIX + os.path.relpath(path, project.root).replace(os.sep, '/'))
                for path in iter_markdown(project.root, ['.'])]
        config = Config(project)
    else:
        tracked = repository_files(repo_root)
        repository = Delivery(repo_root, KNOWN_ROOTS['repository'], tracked)
        template = Delivery(os.path.join(repo_root, 'template'), KNOWN_ROOTS['template'],
                            [path[len(TEMPLATE_PREFIX):] for path in tracked if path.startswith(TEMPLATE_PREFIX)])
        docs = []
        for path in iter_markdown(repository.root, SOURCE_SCAN, SOURCE_EXCLUDED_DIRS):
            key = os.path.relpath(path, repository.root).replace(os.sep, '/')
            docs.append((path, template if key.startswith(TEMPLATE_PREFIX) else repository, key))
        config = None

    problems = []
    references = 0
    used = set()
    scanned = set()
    anchor_cache = {}

    def whitelisted(doc_key, raw):
        for index, entry in enumerate(whitelist):
            if entry['source'] == doc_key and entry['ref'] == raw and entry['mode'] in (mode, 'both'):
                used.add(index)
                return True
        return False

    def report(doc_key, display, line, raw, message):
        if not whitelisted(doc_key, raw):
            problems.append(Problem(doc_key, display, line, raw, message))

    for path, delivery, doc_key in docs:
        scanned.add(doc_key)
        display = doc_key[len(TEMPLATE_PREFIX):] if generated_root else doc_key
        directory = os.path.dirname(path)
        context = context_roots.get(doc_key)
        resolution_root = os.path.join(delivery.root, context) if context else delivery.root
        for number, line, fence_info in scan_lines(read_text(path)):
            if fence_info is None:
                link_texts = []
                visible = INLINE_CODE_RE.sub(lambda m: ' ' * len(m.group(0)), line)
                for match in LINK_RE.finditer(visible):
                    link_texts.append((match.start('text'), match.end('text')))
                    target = link_path(line[match.start('target'):match.end('target')])
                    if target is None:
                        continue
                    references += 1
                    absolute = os.path.join(delivery.root, target.lstrip('/')) if target.startswith('/') \
                        else os.path.join(directory, target)
                    if not delivery.exists(absolute):
                        report(doc_key, display, number, line[match.start('target'):match.end('target')].strip(),
                               '链接目标不存在' if delivery.relative(absolute) is not None else '链接越出交付根')
                spans = []
                for match in INLINE_CODE_RE.finditer(line):
                    inside_link = any(start <= match.start() < end for start, end in link_texts)
                    spans.append((match.group(2), inside_link))
                for span, inside_link in spans:
                    anchor = None
                    candidate_text = span
                    if in_anchor_scope(doc_key):
                        candidate_text, anchor = split_anchor(span)
                    candidate = None if inside_link else path_span(candidate_text)
                    explicit_root_file = False
                    if candidate is None and not inside_link and anchor is not None \
                            and SINGLE_MARKDOWN_RE.match(candidate_text.strip()):
                        candidate = candidate_text.strip()
                        explicit_root_file = True
                    if candidate and (explicit_root_file or is_checked_path(candidate, resolution_root, delivery)):
                        references += 1
                        absolute = os.path.join(resolution_root, candidate)
                        if not delivery.exists(absolute):
                            where = f'按 {context}/ 解析' if context else '按交付根解析'
                            report(doc_key, display, number, span.strip(), f'路径不存在（{where}）')
                        elif anchor is not None:
                            anchors = anchor_cache.get(absolute)
                            if anchors is None:
                                anchors = anchor_cache[absolute] = ANCHOR_RULES.collect_anchors(
                                    read_text(absolute).splitlines())
                            if anchor not in anchors:
                                report(doc_key, display, number, span.strip(), f'锚点不存在：#{anchor}')
                    if config:
                        references += check_commands(span, config, delivery, doc_key, display, number, report)
            elif config and fence_info in SHELL_FENCE_INFOS:
                references += check_commands(line, config, delivery, doc_key, display, number, report)
    return problems, len(docs), references, used, scanned


def check_commands(text, config, delivery, doc_key, display, number, report):
    count = 0
    for match in NPM_RE.finditer(text):
        count += 1
        script = match.group('script') or match.group('lifecycle')
        problem = config.npm_problem(script, match.group('prefix'))
        if problem:
            report(doc_key, display, number, match.group(0), problem)
    for match in NG_RE.finditer(text):
        command = match.group('command')
        if command in NG_NON_TARGET_COMMANDS:
            continue
        count += 1
        problem = config.ng_problem(command, match.group('arg'))
        if problem:
            report(doc_key, display, number, match.group(0).strip(), problem)
    for match in SCRIPT_RE.finditer(text):
        script = match.group('script')
        if re.search(r'[{}*<>$]', script):
            continue
        count += 1
        if not delivery.exists(os.path.join(delivery.root, script.lstrip('/'))):
            report(doc_key, display, number, match.group(0), f'脚本不存在（按交付根解析）：{script}')
    return count


def validate_whitelist(whitelist):
    errors = []
    for index, entry in enumerate(whitelist):
        if set(entry) != {'source', 'ref', 'mode', 'reason'}:
            errors.append(f'白名单第 {index + 1} 项字段必须恰好是 source/ref/mode/reason')
            continue
        if entry['mode'] not in ('source', 'generated', 'both'):
            errors.append(f'白名单第 {index + 1} 项 mode 无效：{entry["mode"]}')
        if not str(entry['reason']).strip():
            errors.append(f'白名单第 {index + 1} 项缺少理由')
        if entry['mode'] != 'source' and not entry['source'].startswith(TEMPLATE_PREFIX):
            errors.append(f'白名单第 {index + 1} 项只有模板文档会进入生成模式：{entry["source"]}')
    return errors


def run(repo_root, generated_root, context_roots, whitelist):
    """返回 (失败行列表, 摘要)。"""
    failures = validate_whitelist(whitelist)
    for key in context_roots:
        if generated_root is None and not os.path.isfile(os.path.join(repo_root, key)):
            failures.append(f'上下文根映射表登记的文档不存在：{key}')
    problems, doc_count, reference_count, used, scanned = collect(repo_root, generated_root, context_roots, whitelist)
    failures += [str(p) for p in problems]
    if doc_count == 0:
        failures.append('扫描到的文档数量为零：扫描范围或排除口径失效')
    elif reference_count == 0:
        failures.append('扫描到的引用数量为零：提取规则失效')
    if generated_root is None:
        for index, entry in enumerate(whitelist):
            if entry['mode'] in ('source', 'both') and index not in used:
                state = '未命中' if entry['source'] in scanned else '来源文档不在扫描范围'
                failures.append(f'白名单陈旧项（{state}）：{entry["source"]} `{entry["ref"]}`')
    summary = f'{doc_count} 份文档、{reference_count} 条引用'
    return failures, summary


# ---------------------------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------------------------

SOURCE_FIXTURE = {
    'README.md': '\n'.join([
        '# 仓库',
        '[有效](docs/guide.md)',
        '[有效根链接](/docs/guide.md#章节)',
        '[有效尖括号与标题](<docs/guide.md> "标题")',
        '[有效查询串](docs/guide.md?plain=1)',
        '[有效百分号编码](docs/%E6%8C%87%E5%8D%97.md)',
        '![有效图片](docs/logo.png)',
        '[外链不检查](https://example.com/missing.md)',
        '[其他 scheme 不检查](vscode://file/missing.md)',
        '[纯锚点不检查](#nope)',
        '[占位不检查](docs/{topic}.md)',
        '[缺失链接](docs/missing.md)',
        '[大小写不符](docs/GUIDE.md)',
        '有效反引号 `docs/guide.md`、`docs/guide.md:12`、`scripts/run.ps1`、`docs/`。',
        '缺失反引号 `docs/absent.md`。',
        '算不算引用与目标是否存在无关：登记的顶层目录已删 `skills/`、`.github/workflows`；'
        '目标已删 `docs/removed`、`docs/removed/`；拼错的首段带第二段 `scrpits/run.ps1`。',
        '单段目录有效 `scripts/`；未登记的单段是目录名，不检查 `widgets/`、`Dtos/`、`architecture/`；'
        '已知局限：拼错的单段 `scrpits/` 同样只是名字。',
        '中文文件名 `docs/指南.md` 有效，`docs/缺失.md` 缺失。',
        '非路径不检查 `Asia/Shanghai`、`try/catch`、`@scope/pkg`、`/api/v1/x`、`bg-black/25`、`npm run nope`、'
        '`Path=/`、`sub/email_verified`。',
        '占位路径不检查 `{缓存根}/docs/missing.md`、`lib/{tfm}/x.xml`、`docs/<topic>.md`、`docs/*.md`、`docs/…/x.md`。',
        '链接文字中的路径不重复检查 [`guide.md`](docs/guide.md)。',
        '`[行内代码里的链接不检查](docs/nope.md)`',
        '```bash',
        '[围栏里的链接不检查](docs/nope.md)',
        'npm run nope',
        '```',
    ]),
    'docs/guide.md': '# 指南\n## 章节\n',
    # Skill 与 AGENTS.md 的反引号路径带锚点时核对锚点；其他文档带 # 的写法不是路径、不检查
    'AGENTS.md': '有效 `docs/guide.md#章节`；锚点失效 `docs/guide.md#已改名`。\n',
    '.agents/skills/s/SKILL.md': '\n'.join([
        '---', 'name: s', '---', '# S',
        '有效 `docs/guide.md#章节`、`docs/指南.md#中文文件名`、无锚点的 `docs/guide.md`。',
        '锚点失效 `docs/guide.md#不存在`；路径失效 `docs/gone.md#章节`。',
        '根文件有效 `README.md#仓库`、`README.md`；锚点失效 `README.md#不存在`；文件失效 `MISSING.md#仓库`。',
        '占位不检查 `docs/{topic}.md#x`、`docs/<topic>.md#x`；按需新建的目录 `docs/project/`。',
    ]),
    'template/.agents/skills/p/SKILL.md': '\n'.join([
        '# P',
        '有效 `docs/standards/api.md#api`、`scripts/check.py`。',
        '锚点失效 `docs/standards/api.md#不存在`；仓库根才有的文档，模板里没有 `docs/guide.md#章节`。',
    ]),
    'docs/指南.md': '# 中文文件名\n',
    'docs/logo.png': '',
    'docs/notes.md': '近似拼写不豁免：`deploy/.ENV`、`./deploy/.env`；精确匹配豁免：`deploy/.env`。\n',
    # 在途工作文档按目录整棵排除；其余 docs/ 文档照常检查，目录名相近也不排除
    'docs/plans/p.md': '[被排除](missing.md) `.tmp/runs/x/`\n',
    'docs/assessments/a.md': '[被排除](missing.md)\n',
    'docs/reports/r.md': '`.tmp/runs/x/result.json`\n',
    'docs/architecture/a.md': '[未排除的断链](missing.md)\n',
    'docs/planning/a.md': '[目录名相近不排除](missing.md)\n',
    'scripts/run.ps1': '',
    'node_modules/pkg/README.md': '[被排除](missing.md)\n',
    'template/README.md': '\n'.join([
        '# 模板',
        '[模板内有效](docs/standards/api.md)',
        '[越出交付根](../docs/guide.md)',
        '交付根解析 `frontend/package.json`、`frontend/src/app/core/a.ts`。',
        '映射表外的上下文相对路径 `src/a.cs`。',
        '只在源码存在 `frontend/src/app/i18n-only.ts`。',
        '生成后才出现 `deploy/.env`。',
        '命令不在源码模式检查 `npm run i18n:check`、`ng e2e`、`python3 scripts/missing.py`。',
    ]),
    'template/docs/standards/api.md': '# API\n',
    'template/backend/README.md': '映射表内的上下文相对路径 `src/a.cs`、`../deploy/compose.yml`。\n',
    'template/backend/src/a.cs': '',
    'template/deploy/compose.yml': '',
    # 合法的条件模板：不是 JSON，源码模式不得因此误报
    'template/frontend/package.json': '\n'.join([
        '{',
        '  "scripts": {',
        '    //#if (IncludeLocalization)',
        '    "i18n:check": "node check.js",',
        '    //#endif',
        '    "build": "ng build"',
        '  }',
        '}',
    ]),
    'template/frontend/angular.json': '{ //#if (X)\n}\n',
    'template/frontend/src/app/core/a.ts': '',
    'template/frontend/src/app/i18n-only.ts': '',
    'template/frontend/node_modules/x/README.md': '[被排除](missing.md)\n',
    'template/frontend/libs/ui/x/README.md': '[被排除](missing.md)\n',
    'template/scripts/check.py': '',
}

SOURCE_EXPECTED = {
    ('README.md', 'docs/missing.md'),
    ('README.md', 'docs/GUIDE.md'),
    ('README.md', 'docs/absent.md'),
    ('README.md', 'scrpits/run.ps1'),
    ('README.md', 'skills/'),
    ('README.md', 'docs/removed'),
    ('README.md', 'docs/removed/'),
    ('README.md', '.github/workflows'),
    ('README.md', 'docs/缺失.md'),
    ('AGENTS.md', 'docs/guide.md#已改名'),
    ('.agents/skills/s/SKILL.md', 'docs/guide.md#不存在'),
    ('.agents/skills/s/SKILL.md', 'docs/gone.md#章节'),
    ('.agents/skills/s/SKILL.md', 'README.md#不存在'),
    ('.agents/skills/s/SKILL.md', 'MISSING.md#仓库'),
    ('template/.agents/skills/p/SKILL.md', 'docs/standards/api.md#不存在'),
    ('template/.agents/skills/p/SKILL.md', 'docs/guide.md#章节'),
    ('docs/notes.md', 'deploy/.ENV'),
    ('docs/notes.md', './deploy/.env'),
    ('docs/architecture/a.md', 'missing.md'),
    ('docs/planning/a.md', 'missing.md'),
    ('template/README.md', '../docs/guide.md'),
    ('template/README.md', 'src/a.cs'),
}

GENERATED_PACKAGE = {'scripts': {'build': 'ng build', 'test': 'ng test', 'lint': 'eslint .'}}
GENERATED_ANGULAR = {'projects': {'app': {'architect': {'build': {}, 'serve': {}, 'test': {}, 'lint': {}}}}}

GENERATED_FIXTURE = {
    'README.md': '\n'.join([
        '# 生成项目',
        '## AI 协作',
        '[有效](docs/standards/api.md)',
        '交付根解析 `frontend/package.json`、`frontend/src/app/core/a.ts`。',
        '映射表外的上下文相对路径 `src/a.cs`。',
        '源码存在、生成后缺失 `frontend/src/app/i18n-only.ts`。',
        '生成后才出现但只豁免源码模式 `deploy/.env`。',
        '有效命令 `npm run build`、`npm test`、`ng build`、`ng t`、`ng run app:lint`、`python3 scripts/check.py`。',
        '缺 npm 脚本 `npm run nope`；只在未启用分支 `npm run i18n:check`；缺 Angular 目标 `ng e2e`。',
        'generator 不检查 `ng generate component x`、`ng g @spartan-ng/cli:ui`、`ng new x`、`ng version`。',
        '非命令不检查 `string username`、`npm ci`、`npm install`。',
        '指定项目目录 `npm --prefix frontend run build`、`npm --prefix frontend test`；'
        '缺脚本 `npm --prefix frontend run nope`；目录不是 npm 项目 `npm --prefix backend run build`；'
        '越出交付根 `npm --prefix=../x run build`。',
        '```bash',
        'cd frontend && npm run lint && npm run missing-in-fence',
        'pwsh scripts/missing.ps1',
        'py -3 scripts/check.py',
        '```',
        '```csharp',
        '// npm run ignored-in-code',
        '```',
    ]),
    'docs/standards/api.md': '# API\n',
    'AGENTS.md': '锚点失效 `docs/standards/api.md#已改名`。\n',
    '.agents/skills/s/SKILL.md': '有效 `docs/standards/api.md#api`；锚点失效 `docs/standards/api.md#不存在`；'
                                 '路径失效 `docs/standards/gone.md#api`。\n'
                                 '根文件有效 `README.md#ai-协作`；锚点失效 `README.md#不存在`；文件失效 `MISSING.md#ai-协作`。\n',
    'backend/README.md': '映射表内的上下文相对路径 `src/a.cs`、`../deploy/compose.yml`。\n',
    'backend/src/a.cs': '',
    'deploy/compose.yml': '',
    'frontend/package.json': json.dumps(GENERATED_PACKAGE),
    'frontend/angular.json': json.dumps(GENERATED_ANGULAR),
    'frontend/src/app/core/a.ts': '',
    'scripts/check.py': '',
}

GENERATED_EXPECTED = {
    ('AGENTS.md', 'docs/standards/api.md#已改名'),
    ('.agents/skills/s/SKILL.md', 'docs/standards/api.md#不存在'),
    ('.agents/skills/s/SKILL.md', 'docs/standards/gone.md#api'),
    ('.agents/skills/s/SKILL.md', 'README.md#不存在'),
    ('.agents/skills/s/SKILL.md', 'MISSING.md#ai-协作'),
    ('README.md', 'src/a.cs'),
    ('README.md', 'frontend/src/app/i18n-only.ts'),
    ('README.md', 'deploy/.env'),
    ('README.md', 'npm run nope'),
    ('README.md', 'npm --prefix frontend run nope'),
    ('README.md', 'npm --prefix backend run build'),
    ('README.md', 'npm --prefix=../x run build'),
    ('README.md', 'npm run i18n:check'),
    ('README.md', 'ng e2e'),
    ('README.md', 'npm run missing-in-fence'),
    ('README.md', 'pwsh scripts/missing.ps1'),
}

SELF_TEST_CONTEXT_ROOTS = {
    'template/backend/README.md': 'backend',
}

SELF_TEST_WHITELIST = [
    {'source': 'docs/notes.md', 'ref': 'deploy/.env', 'mode': 'source', 'reason': '自检：精确匹配才豁免'},
    {'source': 'template/README.md', 'ref': 'deploy/.env', 'mode': 'source', 'reason': '自检：生成后才出现'},
    {'source': '.agents/skills/s/SKILL.md', 'ref': 'docs/project/', 'mode': 'source', 'reason': '自检：按需新建的目录'},
]


def write_tree(base, files):
    for relative, content in files.items():
        path = os.path.join(base, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(content if content.endswith('\n') or not content else content + '\n')


def problem_keys(failures_or_problems):
    return {(p.display, p.raw) for p in failures_or_problems}


def self_test():
    failures = []
    workspace = tempfile.mkdtemp(prefix='doc-references-')
    try:
        source_root = os.path.join(workspace, 'repo')
        write_tree(source_root, SOURCE_FIXTURE)
        subprocess.run(['git', 'init', '-q', source_root], check=True)
        problems, docs, references, used, _ = collect(source_root, None, SELF_TEST_CONTEXT_ROOTS,
                                                      SELF_TEST_WHITELIST)
        actual = problem_keys(problems)
        if actual != SOURCE_EXPECTED:
            failures.append(f'源码模式命中 {sorted(actual)}，期望 {sorted(SOURCE_EXPECTED)}')
        if docs != 12:
            failures.append(f'源码模式扫描了 {docs} 份文档，期望 12（排除口径有变）')
        if used != {0, 1, 2}:
            failures.append(f'源码模式命中的白名单项为 {sorted(used)}，期望三项都命中')
        stale, _ = run(source_root, None, SELF_TEST_CONTEXT_ROOTS,
                       SELF_TEST_WHITELIST + [{'source': 'docs/notes.md', 'ref': 'deploy/.env2', 'mode': 'source',
                                               'reason': '自检：陈旧项'}])
        if not any('白名单陈旧项' in line and 'deploy/.env2' in line for line in stale):
            failures.append('源码模式未报告未命中的白名单项')
        # 被忽略的本地产物在磁盘上存在，也不能让引用通过：干净检出与 CI 里没有它
        ignored_root = os.path.join(workspace, 'ignored')
        write_tree(ignored_root, {'README.md': '构建输出在 `docs/out/report.txt`。', '.gitignore': 'docs/out/',
                                  'docs/out/report.txt': 'x', 'docs/guide.md': '# 指南'})
        subprocess.run(['git', 'init', '-q', ignored_root], check=True)
        ignored_problems, _, _, _, _ = collect(ignored_root, None, {}, [])
        if not any('docs/out/report.txt' in str(problem) for problem in ignored_problems):
            failures.append('源码模式让被 .gitignore 忽略、仅在本地存在的产物通过了存在性检查')
        invalid = validate_whitelist([{'source': 'docs/a.md', 'ref': 'x', 'mode': 'both', 'reason': ''},
                                      {'source': 'a', 'ref': 'b', 'mode': 'prefix', 'reason': 'r'}])
        if len(invalid) != 4:
            failures.append(f'白名单格式校验应报 4 项，实际 {invalid}')

        generated_root = os.path.join(workspace, 'generated')
        write_tree(generated_root, GENERATED_FIXTURE)
        problems, _, _, _, _ = collect(None, generated_root, SELF_TEST_CONTEXT_ROOTS, SELF_TEST_WHITELIST)
        actual = problem_keys(problems)
        if actual != GENERATED_EXPECTED:
            failures.append(f'生成模式命中 {sorted(actual)}，期望 {sorted(GENERATED_EXPECTED)}')

        broken_root = os.path.join(workspace, 'broken')
        write_tree(broken_root, {
            'README.md': '`npm run build`、`ng build`',
            'frontend/package.json': '{ "scripts": { //#if (X)\n } }',
            'frontend/angular.json': '{ "projects": ',
        })
        problems, _, _, _, _ = collect(None, broken_root, {}, [])
        if len(problems) != 2 or not all(p.message.startswith('配置解析失败') for p in problems):
            failures.append(f'配置解析失败应各报一次，实际 {[str(p) for p in problems]}')

        backend_only = os.path.join(workspace, 'backend-only')
        write_tree(backend_only, {'README.md': '`npm run build`、`ng serve`', 'backend/x.cs': ''})
        problems, _, _, _, _ = collect(None, backend_only, {}, [])
        if len(problems) != 2 or not all(p.message.startswith('所属项目无法确定') for p in problems):
            failures.append(f'无前端时应报所属项目无法确定，实际 {[str(p) for p in problems]}')

        empty_root = os.path.join(workspace, 'empty')
        write_tree(empty_root, {'backend/x.cs': ''})
        lines, _ = run(None, empty_root, {}, [])
        if not any('文档数量为零' in line for line in lines):
            failures.append(f'扫描为空未失败：{lines}')
        no_reference_root = os.path.join(workspace, 'no-reference')
        write_tree(no_reference_root, {'README.md': '# 只有标题'})
        lines, _ = run(None, no_reference_root, {}, [])
        if not any('引用数量为零' in line for line in lines):
            failures.append(f'引用为零未失败：{lines}')
        empty_repo = os.path.join(workspace, 'empty-repo')
        os.makedirs(empty_repo)
        subprocess.run(['git', 'init', '-q', empty_repo], check=True)
        lines, _ = run(empty_repo, None, {}, [])
        if not any('文档数量为零' in line for line in lines):
            failures.append(f'源码模式扫描为空未失败：{lines}')
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    own = validate_whitelist(WHITELIST)
    if own:
        failures += own

    if failures:
        print('❌ 文档引用规则自检失败：')
        print('\n'.join(f'  {line}' for line in failures))
        return 1
    print(f'✅ 文档引用规则自检通过（源码夹具 {len(SOURCE_EXPECTED)} 处、生成夹具 {len(GENERATED_EXPECTED)} 处预期失败，'
          f'另验配置解析失败、所属项目无法确定、扫描为空、白名单精确与陈旧）。')
    return 0


def main(argv):
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except (AttributeError, ValueError):
        pass
    if '--self-test' in argv:
        return self_test()
    generated_root = None
    if argv:
        if len(argv) != 2 or argv[0] != '--root':
            print('用法：check-doc-references.py [--root <生成项目>] | --self-test', file=sys.stderr)
            return 2
        generated_root = os.path.abspath(argv[1])
        if not os.path.isdir(generated_root):
            print(f'❌ 目录不存在：{generated_root}', file=sys.stderr)
            return 2

    failures, summary = run(REPO_ROOT, generated_root, CONTEXT_ROOTS, WHITELIST)
    label = '生成项目' if generated_root else '源码'
    if failures:
        print(f'❌ {label}文档引用失效（{len(failures)} 处，{summary}）：')
        for line in failures:
            print(f'  {line}')
        return 1
    print(f'✅ {label}文档引用有效（{summary}）。')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
