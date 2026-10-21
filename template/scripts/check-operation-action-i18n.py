#!/usr/bin/env python3
"""每个已登记的操作动作码都要有中英句子模板。

界面把动作码渲染成一句话（「删除了角色 管理员」），靠的是 `operationRecords.actions.<码>`
这条词条。**漏配不会报错**：未登记/无词条的码按降级规则原样显示裸码，页面照常能用，
只是那一行仍是 `role.deleted` 这种机器码——没有任何红灯，只有"有些行看不懂"。
这道闸门就是把这种静默失配变成构建期失败。

判据：
  1. 从 `OperationRecordActions` 类中提取全部 `public const string X = "y.z";` 的**字面值**；
     同文件里的 `OperationRecordAuthorizations` 不在射程内——那是授权依据，不进句子模板。
  2. 每个动作码在 `en.json` 与 `zh-CN.json` 的 `operationRecords/{lang}.json` 的 `actions` 下都必须有非空词条。

**刻意只做单向校验，不报"多余词条"。** 词条文件里允许存在本项目暂时用不到的键：
前端词条是 JSON，没有条件编译，某个功能没启用时它对应的组件不在了、键却还留着。
反向校验会把这些既有键一并判红，而它们并不是缺陷。

自测：`python3 scripts/check-operation-action-i18n.py --self-test`
"""
import glob
import json
import os
import re
import sys

# 脚本所在目录的上一级就是被检查的根：
#   - 随模板分发时位于 <生成项目>/scripts/，根即生成项目；
#   - 本仓直接跑 template/scripts/ 这一份，根即 template/。
# 一份实现同时服务两边，不留会漂移的第二份拷贝。
ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..'))

# 项目名在生成时被替换，因此按 glob 定位而不是写死程序集名。
# 命中多个说明仓库结构已变，宁可报错也不猜。
def _single(pattern, what):
    matches = sorted(glob.glob(os.path.join(ROOT, pattern)))
    if len(matches) == 1:
        return matches[0]
    return None if not matches else f'__AMBIGUOUS__{what}:{len(matches)}'


# 模板源码里条件尚未求值，两档句子来源同时存在，都要校；
# 生成项目里按本项目的实际形态判：有词条文件就不需要内联表。
IS_TEMPLATE_SOURCE = os.path.isdir(os.path.join(ROOT, '.template.config'))

ACTIONS_SOURCE = _single(
    os.path.join('backend', 'src', '*.Application', 'OperationRecords', 'Provider',
                 'OperationRecordActions.cs'), '动作码定义')
I18N_DIR = os.path.join(ROOT, 'frontend', 'public', 'i18n')
LOCALES = ('en', 'zh-CN')

# 不启用多语言的场景没有词条文件（template.json 在 !IncludeLocalization 时整个排除
# public/i18n/**），句子只能内联在组件里。于是同一批英文句子有了**两个事实源**，
# 而注释拦不住漂移——这道校验就是拦它的。
TABLE_SOURCE = os.path.join(
    ROOT, 'frontend', 'src', 'app', 'features', 'platform', 'components',
    'operation-records', 'widgets', 'operation-record-table', 'operation-action-sentence.ts')
INLINE_TABLE_NAME = 'ACTION_SENTENCES'

# 表体按名字定位到第一个独立的 `};`。键的值有两种形态（箭头函数与字符串字面量），
# 所以按**键**匹配而不是按值，否则要为两种写法各维护一条正则。
#
# 类型标注用 `[^\n]*?` 跳过，**不能用 `[^=]*`**：标注本身含箭头
# （`Record<string, (target: string) => string>`），`[^=]*` 会在 `=>` 的第一个等号处停住，
# 于是整条正则失配、闸门报"定位不到表"。这个坑由 self_test 的"类型标注含箭头"用例守住。
INLINE_TABLE_RE = re.compile(
    r'^const\s+' + INLINE_TABLE_NAME + r'\s*(?::[^\n]*?)?=\s*\{(?P<body>.*?)^\};',
    re.DOTALL | re.MULTILINE)
INLINE_KEY_RE = re.compile(r"^\s*'(?P<key>[A-Za-z][\w.-]*)'\s*:", re.MULTILINE)

# 只取 OperationRecordActions 类体内的常量。两个类在同一文件里，按类名切段而不是全文扫，
# 否则 4 个授权依据常量（AuthenticatedSelf 等）会被当成缺词条的动作码。
ACTIONS_CLASS_RE = re.compile(
    r'public\s+static\s+class\s+OperationRecordActions\b(?P<body>.*?)(?=\npublic\s+static\s+class\b|\Z)',
    re.DOTALL)
CONST_RE = re.compile(r'public\s+const\s+string\s+\w+\s*=\s*"(?P<value>[^"]+)"\s*;')


def extract_action_codes(text):
    match = ACTIONS_CLASS_RE.search(text)
    if not match:
        return None
    return [m.group('value') for m in CONST_RE.finditer(match.group('body'))]


def read_action_entries(path):
    with open(path, encoding='utf-8') as handle:
        data = json.load(handle)
    node = data.get('actions', {})
    return node if isinstance(node, dict) else {}


def extract_inline_sentence_keys(text):
    """取组件里内联英文句子表的键集合；定位不到表返回 None。"""
    match = INLINE_TABLE_RE.search(text)
    if not match:
        return None
    return [m.group('key') for m in INLINE_KEY_RE.finditer(match.group('body'))]


def check(actions_source, i18n_dir, locales, table_source=None, require_inline=True):
    problems = []

    if isinstance(actions_source, str) and actions_source.startswith('__AMBIGUOUS__'):
        return [f'定位动作码定义时命中了多个 *.Application 项目（{actions_source.split(":")[-1]} 个）；'
                '仓库结构已变，闸门不猜，请更新本脚本的定位方式。']
    if not actions_source or not os.path.exists(actions_source):
        return ['找不到动作码定义（backend/src/*.Application/OperationRecords/Provider/'
                'OperationRecordActions.cs）。']

    with open(actions_source, encoding='utf-8') as handle:
        codes = extract_action_codes(handle.read())

    if codes is None:
        return ['未能在源码中定位 OperationRecordActions 类；闸门无法判定，视为失败。']
    if not codes:
        return ['OperationRecordActions 类中没有解析到任何动作码；正则可能已与源码漂移。']

    # 不启用多语言的项目没有 public/i18n（整个目录在生成时被排除），
    # 句子只内联在组件里——那一档由下面的内联表校验兜住，不是缺陷。
    localized = os.path.isdir(i18n_dir)

    for locale in locales if localized else ():
        path = os.path.join(i18n_dir, 'operationRecords', f'{locale}.json')
        if not os.path.exists(path):
            problems.append(f'{locale}：缺少词条文件 {path}')
            continue
        try:
            entries = read_action_entries(path)
        except json.JSONDecodeError as error:
            problems.append(f'{locale}：词条文件不是合法 JSON — {error}')
            continue

        for code in codes:
            value = entries.get(code)
            if value is None:
                problems.append(f'{locale}：动作码 `{code}` 没有句子模板（operationRecords.actions）')
            elif not str(value).strip():
                problems.append(f'{locale}：动作码 `{code}` 的句子模板是空串')

    # 内联英文句子表与词条文件是**互补**的两档，不是都要有：
    #   - 启用多语言：句子在词条文件里，组件里那张内联表被条件裁掉——不存在是正常的；
    #   - 不启用多语言：没有词条文件，句子只剩内联表，那时它必须在、且必须全覆盖。
    # 模板源码里两者同时存在（条件尚未求值），因此两档都会被校到。
    # 内联表在不启用多语言的形态里是唯一的句子来源，必须在、必须全覆盖；
    # 启用多语言的生成项目里它被整块裁掉，不存在是设计如此。
    # require_inline 由调用方定案，不靠"找不到就当被裁掉了"去猜——
    # 那种猜法会让真正的正则漂移也被静默放行，闸门失效之后还一直报绿。
    if table_source is not None and require_inline:
        problems.extend(check_inline_table(table_source, codes))

    return problems


def check_inline_table(table_source, codes):
    """内联英文句子表必须与动作码全集一一对应。

    这里是**双向**校验，与词条那边的单向不同：内联表是 `en.json` 的镜像，多一条就是漂移
    （某个动作码被删了而内联表没跟上），不像词条文件那样存在"某些场景用不到但保留"的既有惯例。

    刻意**不**校验 `ACTION_SENTENCES_NO_TARGET` 与 `FAILURE_REASONS`：前者只服务
    "创建类端点在授权阶段被拒"这个子集，后者只覆盖实际会产生的失败码。
    强求它们全覆盖，只会逼出一堆永远用不上的条目。
    """
    if not os.path.exists(table_source):
        return [f'找不到内联句子表所在的组件：{table_source}']

    with open(table_source, encoding='utf-8') as handle:
        keys = extract_inline_sentence_keys(handle.read())

    if keys is None:
        return [f'未能在 {os.path.basename(table_source)} 中定位 {INLINE_TABLE_NAME}；'
                '正则可能已与源码漂移，闸门无法判定，视为失败。']

    problems = []
    expected = set(codes)
    actual = set(keys)
    for missing in sorted(expected - actual):
        problems.append(f'内联句子表：动作码 `{missing}` 没有英文句子（不启用多语言的场景会显示裸码）')
    for extra in sorted(actual - expected):
        problems.append(f'内联句子表：`{extra}` 不是已登记的动作码（动作码删了而内联表没跟上？）')
    return problems


SELF_TEST_SOURCE = '''
public static class OperationRecordActions
{
    public const string UserCreated = "user.created";
    public const string RoleDeleted = "role.deleted";
}

public static class OperationRecordAuthorizations
{
    public const string AuthenticatedSelf = "AuthenticatedSelf";
}
'''


def self_test():
    import tempfile

    failures = []

    codes = extract_action_codes(SELF_TEST_SOURCE)
    if codes != ['user.created', 'role.deleted']:
        failures.append(f'应只提取 OperationRecordActions 类内的动作码，实际得到 {codes}')

    with tempfile.TemporaryDirectory() as tmp:
        source = os.path.join(tmp, 'OperationRecordActions.cs')
        with open(source, 'w', encoding='utf-8') as handle:
            handle.write(SELF_TEST_SOURCE)

        i18n = os.path.join(tmp, 'i18n')
        os.makedirs(os.path.join(i18n, 'operationRecords'))

        def write(locale, actions):
            with open(os.path.join(i18n, 'operationRecords', f'{locale}.json'), 'w', encoding='utf-8') as handle:
                json.dump({'actions': actions}, handle)

        write('en', {'user.created': 'Created user {{target}}', 'role.deleted': 'Deleted role {{target}}'})
        if check(source, i18n, ('en',)):
            failures.append('词条齐全时不应报错')

        write('en', {'user.created': 'Created user {{target}}'})
        if len(check(source, i18n, ('en',))) != 1:
            failures.append('缺一条词条时应恰好报一项')

        write('en', {'user.created': 'Created user {{target}}', 'role.deleted': '   '})
        if len(check(source, i18n, ('en',))) != 1:
            failures.append('空白词条应被判为缺失')

        write('en', {
            'user.created': 'Created user {{target}}',
            'role.deleted': 'Deleted role {{target}}',
            'never.registered': '多余的词条',
        })
        if check(source, i18n, ('en',)):
            failures.append('多余词条不该报错——本闸门刻意只做单向校验')

        # 内联表：双向校验
        write('en', {'user.created': 'Created user {{target}}', 'role.deleted': 'Deleted role {{target}}'})
        table = os.path.join(tmp, 'table.ts')

        def write_table(entries):
            body = '\n'.join(f"  '{k}': (t) => `x ${{t}}`," for k in entries)
            with open(table, 'w', encoding='utf-8') as handle:
                handle.write(
                    f'const {INLINE_TABLE_NAME}: Record<string, (t: string) => string> = {{\n'
                    f'{body}\n}};\n')

        write_table(['user.created', 'role.deleted'])
        if check(source, i18n, ('en',), table):
            failures.append('内联表齐全时不应报错')

        write_table(['user.created'])
        if len(check(source, i18n, ('en',), table)) != 1:
            failures.append('内联表缺一条时应恰好报一项')

        write_table(['user.created', 'role.deleted', 'never.registered'])
        if len(check(source, i18n, ('en',), table)) != 1:
            failures.append('内联表多一条时应报一项——它是镜像，多余即漂移')

        with open(table, 'w', encoding='utf-8') as handle:
            handle.write('// 表被改名或删除\n')
        if not check(source, i18n, ('en',), table):
            failures.append('定位不到内联表时必须失败，不能静默放行')

        # 类型标注里含箭头（`=> string`）时仍要定位得到。
        # 这一条不是凑数：早先的正则用 `[^=]*` 跳类型标注，在 `=>` 的第一个等号处就停住，
        # 整条失配、闸门报"定位不到表"——而当时的自检也被同一个 bug 连带打红，
        # 并没有独立覆盖这个形态。修好正则后若没有本用例，这个坑就再也没人守。
        with open(table, 'w', encoding='utf-8') as handle:
            handle.write(
                f'const {INLINE_TABLE_NAME}: Record<string, (target: string) => string> = {{\n'
                "  'user.created': (t) => `Created user ${t}`,\n"
                "  'role.deleted': (t) => `Deleted role ${t}`,\n"
                '};\n')
        if check(source, i18n, ('en',), table):
            failures.append('类型标注含箭头（=>）时应仍能定位到内联表')

    if failures:
        print('❌ 规则自检失败：')
        for failure in failures:
            print(f'  - {failure}')
        return 1

    print('✅ 规则自检通过（10 例）。')
    return 0


def main():
    # Windows 上管道输出默认按本地代码页编码，打印 ✅ 与中文会崩溃
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8')
    if '--self-test' in sys.argv:
        return self_test()

    require_inline = IS_TEMPLATE_SOURCE or not os.path.isdir(I18N_DIR)
    problems = check(ACTIONS_SOURCE, I18N_DIR, LOCALES, TABLE_SOURCE, require_inline)
    if problems:
        print(f'❌ 动作码句子模板检查失败（共 {len(problems)} 项）：')
        for problem in sorted(problems):
            print(f'  - {problem}')
        print('\n漏配不会报错，只会让那一行显示成裸动作码。词条补 '
              'frontend/public/i18n/*.json 的 operationRecords.actions；'
              f'英文句子补 operation-action-sentence.ts 的 {INLINE_TABLE_NAME}（两者必须同步）。')
        return 1

    with open(ACTIONS_SOURCE, encoding='utf-8') as handle:
        codes = extract_action_codes(handle.read())
    if not os.path.isdir(I18N_DIR):
        scope = '仅内联英文句子表：本项目未启用多语言'
    elif require_inline:
        scope = f'{len(LOCALES)} 种语言，并与内联英文句子表一一对应'
    else:
        scope = f'{len(LOCALES)} 种语言'
    print(f'✅ 动作码句子模板检查通过（{len(codes)} 个动作码 × {scope}）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
