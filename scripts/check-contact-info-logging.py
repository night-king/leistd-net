#!/usr/bin/env python3
"""联系方式不能以原文进入日志。

邮箱、手机号这类联系方式一旦写进日志，就同时留在集中采集的存储里：保留更久、可见范围更大，
还会被前端错误上报之类的旁路带走。**症状是静默的**——代码照常工作，日志照常有内容，
只有事后审计或外部反馈才会发现。DEF-04 就是这么被发现的。

单测覆盖不到这一类：日志调用点通常在要连外部服务的方法里（`SmtpEmailSender.SendAsync`
要真连 SMTP），为它造接缝的成本高于收益，而**把调用点改回原文不会让任何用例变红**。
所以判据放在这里：扫日志调用的实参，凡是明显承载联系方式的表达式，必须经
`Leistd.Redaction.TextRedactor` 处理，或者压根不出现在日志里。

业务异常的默认文案是第二条路：`BusinessException` 的 message 会随异常对象进日志（5xx 的
异常链、宿主自己记录的异常），所以构造实参同样不能插入联系方式；运行时的值交给 `.WithData`，
它在构造调用的括号之外，不在扫描范围内。

判据是**实参表达式**而不是占位符名：占位符叫什么由写日志的人定，`{To}` 里塞原文一样泄露。

自测：`python3 scripts/check-contact-info-logging.py --self-test`
"""
import io, os, re, sys, tempfile

ROOT = os.path.join(os.path.dirname(__file__), '..')

# 禁区：会写日志的生产代码
ZONES = [
    'framework/components',
    'framework/ddd-struct',
    'template/backend/src',
]

# 日志调用：ILogger 的扩展方法与源生成方法都以 Log 开头
LOG_CALL_RE = re.compile(r'\bLog(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(', re.I)

# 业务异常构造：message 会随异常对象进日志
BUSINESS_EXCEPTION_RE = re.compile(r'\bnew\s+BusinessException\s*\(')

# 明显承载联系方式的实参表达式。只认"名字就说明它是联系方式"的形态，
# 不做类型推断——推断不了的由组合规范与评审兜，不在这里猜。
CONTACT_ARG_RE = re.compile(
    r'\b(?:'
    r'[A-Za-z_][A-Za-z0-9_.]*\.(?:Email|EmailAddress|PhoneNumber|Mobile)\b'      # x.Email / dto.PhoneNumber
    r'|(?:email|emailAddress|phoneNumber|mobile)\b'                              # 局部变量
    r'|[A-Za-z_][A-Za-z0-9_.]*\.To\b'                                            # message.To / input.To
    r')',
    re.I)

# 已经脱敏的写法：实参被 TextRedactor 包住就放行
REDACTED_RE = re.compile(r'TextRedactor\s*\.\s*Redact[A-Za-z]*\s*\(')

# 判定前先抠掉字符串字面量的文字部分：消息模板本身常含 "email"（"Email verification is
# unavailable"、"the notification email to user {UserId}"），连模板一起扫会把它们全判成违规。
# 插值字符串（$"…"）保留洞内表达式——把联系方式插进模板同样是泄露，那种写法要留在判定范围内。

# 插值字符串只看洞里的表达式：文字部分写 "this email" 不是泄露，`{email}` 才是。
# 洞里还能再套字符串甚至插值字符串（`{email ?? "missing"}`、`{($"{email}")}`），正则配不平，
# 所以逐字符扫：普通字面量只留空串，插值字面量只留洞内表达式（递归处理洞里的字符串）。
# 普通串用反斜杠转义；逐字串（@"…"、$@"…"）里反斜杠是普通字符，`""` 才表示引号。

_STRING_PREFIXES = ('$@"', '@$"', '$"', '@"', '"')


def _string_at(text, i):
    """i 处是字符串开头时返回前缀，否则 None。"""
    for prefix in _STRING_PREFIXES:
        if text.startswith(prefix, i):
            return prefix
    return None


def _scan_string(text, i, prefix):
    """i 指向前缀起点，返回 (洞内表达式, 闭引号之后的位置)。"""
    interpolated, verbatim = '$' in prefix, '@' in prefix
    i += len(prefix)
    holes = []
    while i < len(text):
        c = text[i]
        if c == '"':
            if verbatim and text.startswith('""', i):
                i += 2
                continue
            return ' '.join(holes), i + 1
        if c == '\\' and not verbatim:
            i += 2
            continue
        if interpolated and c == '{':
            if text.startswith('{{', i):
                i += 2
                continue
            hole, i = _scan_hole(text, i + 1)
            holes.append(hole)
            continue
        i += 1
    return ' '.join(holes), i


def _scan_hole(text, i):
    """i 指向洞内第一个字符，返回 (洞内表达式, 闭花括号之后的位置)。"""
    out, depth = [], 0
    while i < len(text):
        c = text[i]
        prefix = _string_at(text, i)
        if prefix:
            inner, i = _scan_string(text, i, prefix)
            out.append(' ' + inner + ' ')
            continue
        if c == '{':
            depth += 1
        elif c == '}':
            if depth == 0:
                return ''.join(out), i + 1
            depth -= 1
        out.append(c)
        i += 1
    return ''.join(out), i


def expressions(args):
    """抠掉字面量文字，只留实参表达式与插值洞里的表达式。"""
    out, i = [], 0
    while i < len(args):
        if args[i] == "'":
            # 字符字面量（如 '"'）整段跳过，免得其中的引号被当成字符串开头
            end = args.find("'", i + 2 if args.startswith("'\\", i) else i + 1)
            i = end + 1 if end >= 0 else len(args)
            continue
        prefix = _string_at(args, i)
        if prefix:
            inner, i = _scan_string(args, i, prefix)
            out.append(' ' + inner + ' ' if '$' in prefix else '""')
            continue
        out.append(args[i])
        i += 1
    return ''.join(out)

# 豁免：路径 → (被豁免的整条语句片段, 期望出现次数, 理由)。
# 加新条目前先问"这是不是说明规则该改"。
WAIVERS = {}


def calls(text, call_re):
    """产出每个调用的实参串（按括号配平截取，跨行也能取全）。"""
    for match in call_re.finditer(text):
        start = match.end()
        depth, i = 1, start
        while i < len(text) and depth:
            if text[i] == '(':
                depth += 1
            elif text[i] == ')':
                depth -= 1
            i += 1
        yield text[:match.start()].count('\n') + 1, text[start:i - 1]


def run(zones, waivers, root):
    problems, checked, waived = [], 0, {}
    for zone in zones:
        zone_root = os.path.join(root, zone)
        for dirpath, dirnames, filenames in os.walk(zone_root):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            for fname in filenames:
                if not fname.endswith('.cs'):
                    continue
                path = os.path.join(dirpath, fname)
                rel = os.path.relpath(path, root).replace(os.sep, '/')
                text = io.open(path, encoding='utf-8').read()
                checked += 1
                for kind, call_re in (('日志实参', LOG_CALL_RE), ('业务异常文案', BUSINESS_EXCEPTION_RE)):
                    for line, args in calls(text, call_re):
                        hit = CONTACT_ARG_RE.search(expressions(args))
                        if not hit or REDACTED_RE.search(args):
                            continue
                        snippet = ' '.join(args.split())[:120]
                        entry = waivers.get(rel)
                        if entry and entry[0] in snippet:
                            waived[rel] = waived.get(rel, 0) + 1
                            continue
                        problems.append(
                            f'❌ {rel}:{line} {kind} `{hit.group(0)}` 看起来是联系方式，'
                            f'会以原文进入日志：\n     {snippet}')
    for rel, (frag, expected, reason) in waivers.items():
        got = waived.get(rel, 0)
        if got != expected:
            problems.append(
                f'❌ 豁免与实际不符：{rel} 期望命中 {expected} 次 `{frag}`，实际 {got} 次（{reason}）')
    if problems:
        return 1, ['发现联系方式以原文进入日志：', *problems, '',
                   '改法：实参用 TextRedactor.RedactEmail / RedactPartially 包一层，'
                   '或把该字段从日志里去掉（用标识符代替）；业务异常文案写固定句子，值交给 .WithData。']
    return 0, [f'✅ 联系方式日志检查通过（{checked} 个文件，{len(zones)} 个禁区，'
               f'{sum(waived.values())} 处豁免全部命中）。']


SELF_TEST_CASES = [
    ('原文进日志应拦下', 1, {
        'Bad.cs': 'logger.LogInformation("Sent to {To}", message.To);\n'}),
    ('脱敏后放行', 0, {
        'Good.cs': 'logger.LogInformation("Sent to {To}", TextRedactor.RedactEmail(message.To));\n'}),
    ('局部变量原文应拦下', 1, {
        'Bad2.cs': 'logger.LogWarning("Creating {Email}", email);\n'}),
    ('跨行调用也要取全实参', 1, {
        'Bad3.cs': 'logger.LogInformation(\n    "Sent to {To}",\n    input.Email);\n'}),
    ('不含联系方式的日志不误报', 0, {
        'Fine.cs': 'logger.LogInformation("Created {UserId}", user.Id);\n'}),
    # 占位符叫别的名字照样要拦：判据是实参不是占位符
    ('占位符改名不能绕过', 1, {
        'Bad4.cs': 'logger.LogInformation("Target {Who}", user.Email);\n'}),
    # 消息模板里出现 email 字样不算违规——这是加这条抠字面量规则前的误报形态
    ('模板文本含 email 不误报', 0, {
        'Fine2.cs': 'logger.LogInformation("Email verification unavailable: {Section}", section);\n'}),
    ('模板文本含 email 且实参无关也不误报', 0, {
        'Fine3.cs': 'logger.LogWarning("the notification email to user {UserId} was dropped", userId);\n'}),
    # 插值字符串不抠：把联系方式插进模板同样要拦
    ('插值把联系方式塞进模板要拦', 1, {
        'Bad5.cs': 'logger.LogInformation($"Sent to {user.Email}");\n'}),
    # 业务异常的默认文案会进日志
    ('业务异常文案插入邮箱要拦', 1, {
        'Bad6.cs': 'throw new BusinessException(UserErrorCodes.EmailTaken, $"Email \'{email}\' is already in use.")\n'
                   '    .WithData("Email", email);\n'}),
    ('跨行的业务异常文案也要拦', 1, {
        'Bad7.cs': 'throw new BusinessException(\n    UserErrorCodes.EmailTaken,\n    $"Email {input.Email} taken.");\n'}),
    ('固定文案加 WithData 放行', 0, {
        'Fine4.cs': 'throw new BusinessException(UserErrorCodes.EmailTaken, "Email is already in use.")\n'
                    '    .WithData("Email", email);\n'}),
    # 错误码常量里含 Email 字样不算联系方式
    ('错误码常量含 Email 不误报', 0, {
        'Fine5.cs': 'throw new BusinessException(AppSettingErrorCodes.EmailAddressInvalid, "Invalid.");\n'}),
    ('插值文字部分写 email 不误报', 0, {
        'Fine7.cs': 'throw new BusinessException(ExternalAuthErrorCodes.AccountExists, $"An account with this email exists. Link {provider}.");\n'}),
    # 洞里套字符串时不能把洞吞掉：这是曾经出现过的回归形态
    ('洞内带空值回落的日志要拦', 1, {
        'Bad8.cs': 'logger.LogWarning($"Contact {email ?? "missing"}");\n'}),
    ('洞内带空值回落的业务异常文案要拦', 1, {
        'Bad9.cs': 'throw new BusinessException(Codes.Rejected, $"Contact {email ?? "missing"}");\n'}),
    ('逐字插值字符串也要拦', 1, {
        'Bad10.cs': 'logger.LogWarning($@"Contact {user.Email}");\n'}),
    # 逐字插值串里反斜杠是普通字符、`""` 是引号；嵌套插值串里的洞也要看
    ('逐字串含转义引号仍要拦', 1, {
        'Bad11.cs': 'logger.LogWarning($@"Contact ""{email}""");\n'}),
    ('逐字串含反斜杠仍要拦', 1, {
        'Bad12.cs': 'logger.LogWarning($@"Contact C:\\{email}");\n'}),
    ('嵌套插值串仍要拦', 1, {
        'Bad13.cs': 'throw new BusinessException(Codes.Rejected, $"Contact {($"{email}")}");\n'}),
    ('逐字串与嵌套串不含联系方式不误报', 0, {
        'Fine8.cs': 'logger.LogWarning($@"Path C:\\{folder} ""{($"{userId}")}""");\n'}),
    ('用户名、时区等插值不误报', 0, {
        'Fine6.cs': 'throw new BusinessException(UserErrorCodes.UsernameTaken, $"Username \'{username}\' already exists.");\n'}),
]


def self_test():
    failures = 0
    print('联系方式日志规则自检：')
    for name, expected, files in SELF_TEST_CASES:
        with tempfile.TemporaryDirectory() as tmp:
            zone = os.path.join(tmp, 'zone')
            os.makedirs(zone)
            for fname, content in files.items():
                io.open(os.path.join(zone, fname), 'w', encoding='utf-8').write(content)
            code, msgs = run(['zone'], {}, tmp)
            ok = code == expected
            print(f"  {'✅' if ok else '❌'} {name}（期望退出 {expected}，实际 {code}）")
            if not ok:
                failures += 1
                print('     ' + '\n     '.join(msgs))
    if failures:
        print(f'\n❌ 自测失败 {failures} 例。')
        return 1
    print(f'\n✅ 自测通过（{len(SELF_TEST_CASES)} 例）。')
    return 0


def main():
    if '--self-test' in sys.argv:
        return self_test()
    code, messages = run(ZONES, WAIVERS, ROOT)
    print('\n'.join(messages))
    return code


if __name__ == '__main__':
    sys.exit(main())
