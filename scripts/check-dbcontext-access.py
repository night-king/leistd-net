#!/usr/bin/env python3
"""framework 的 EF 存储与管理器不得直接注入 DbContext。

只有 `IDbContextProvider<TDbContext>` 会设置 `DbContextCreationContext.Current`，
宿主的 `AddDbContext` 回调据此拿到本工作单元已解析的连接。直接注入 `TDbContext`
（或具体 DbContext 类型）会让 DI 走宿主回调的**回落分支**，于是：

  - 登记了自己连接的租户，其数据落到宿主配置的默认连接上；
  - 写入脱离工作单元的事务与 `UnitOfWorkConnectionBinding` 的归属/目标校验；
  - 返回 `IQueryable` 的入口（资源 ACL 的两个集合查询）与调用方的业务查询出自不同实例，
    `Contains` 无法翻译进同一条 SQL。

三种后果**都是静默的**：不报错、日志里也看不出来，只能从数据反推。这条规则一旦被
重新违反，代价与当初一样高，因此做成机械闸门而不是靠注释与审查。

覆盖范围：`framework/components/**` 与 `framework/ddd-struct/**` 下形如 `EfCore*Store` /
`EfCore*Manager` 的类型——那正是"组件替宿主访问数据库"的全部落点。泛型与非泛型、
主构造函数与显式构造函数都检查。测试替身、宿主自身的 DbContext 派生类，
以及 `IDbContextProvider` 自己的管道件（EfCoreDatabaseApi 等）都不在范围内。

判定按**每个构造入口**独立进行：某个构造函数只拿到裸 DbContext、没有 provider，就是违规——
哪怕同一个类的另一个构造函数是合规的。此前是"整个类里出现过 provider 就放行"，
于是"合规主构造函数 + 违规显式构造函数"这种混合形态整体逃过检查，
而它与脚本自称的"检查全部构造函数"直接矛盾。

**已知边界（刻意接受）**：按文本形态判定，不做语义分析。因此
"同一个构造函数里同时注入 provider 与裸 DbContext、而代码用错了那一个" 判不出来——
判它需要数据流分析，得引入 Roslyn 分析器，与这道闸门要防的单一回归不成比例，留给代码审查。
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(__file__), '..')

ZONES = [
    'framework/components',
    'framework/ddd-struct',
]

SKIP_DIR = ('obj', 'bin', 'node_modules')

# 只认真正"替宿主访问数据库"的落点：EfCore*Store / EfCore*Manager。
#
# 刻意不匹配全部 `EfCore*`：同目录下的 EfCoreDatabaseApi / EfCoreTransactionApi 是
# IDbContextProvider **自己的**管道件，它们持有的正是刚解析出来的那个上下文，
# 按定义不可能再经提供器。把它们纳入范围就必须配豁免清单，而需要豁免清单的闸门迟早被关掉。
# 泛型与非泛型都要匹配：`class EfCoreFooStore<T>(` 与 `class EfCoreFooStore :` / `class EfCoreFooStore`
CLASS_DECL = re.compile(
    r'\b(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+'
    r'(EfCore\w*(?:Store|Manager))\b',
)

# 构造参数里出现裸 DbContext 形态：`TDbContext dbContext` / `SomeDbContext ctx`
BAD_PARAM = re.compile(r'\b(TDbContext|\w*DbContext)\s+(\w+)\s*(?:,|\))')

ALLOWED = re.compile(r'IDbContextProvider\s*<')


def class_body_end(text, class_start):
    """取该类型声明到下一个 EfCore*Store/Manager 声明（或文件末）之间的整段。"""
    nxt = CLASS_DECL.search(text, class_start + 1)
    return nxt.start() if nxt else len(text)


def constructor_regions(text, class_start, type_name):
    """产出该类型的全部构造函数参数列表文本：主构造函数 + 每个显式构造函数。

    只看主构造函数是不够的——显式构造函数的参数列表在类体里，
    而主构造函数区域到第一个 `{` 就结束了，于是
    `class EfCoreFooStore { public EfCoreFooStore(TDbContext db) {...} }` 会整体逃过检查。
    """
    end = class_body_end(text, class_start)
    body = text[class_start:end]

    # 主构造函数 + 基类列表：声明之后、第一个 `{` 之前
    brace = body.find('{')
    yield body[:brace if brace != -1 else len(body)], class_start

    # 显式构造函数：`修饰符 TypeName(` 到配对的 `)`
    explicit = re.compile(
        r'\b(?:public|internal|protected|private)\s+' + re.escape(type_name) + r'\s*\(')
    for match in explicit.finditer(body):
        close = body.find(')', match.end())
        if close != -1:
            yield body[match.start():close + 1], class_start + match.start()


def main():
    findings = []
    scanned = 0

    for zone in ZONES:
        base = os.path.join(ROOT, zone)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIR]
            for name in filenames:
                if not name.endswith('.cs'):
                    continue
                path = os.path.join(dirpath, name)
                with open(path, encoding='utf-8') as handle:
                    text = handle.read()
                scanned += 1

                for match in CLASS_DECL.finditer(text):
                    type_name = match.group(1)
                    for region, offset in constructor_regions(text, match.start(), type_name):
                        # 逐个构造入口判定：本入口自己拿到 provider 才算合规。
                        # 用"整个类里出现过 provider"放行会漏掉混合形态（见文件头）
                        if ALLOWED.search(region):
                            continue

                        bad = BAD_PARAM.search(region)
                        if not bad:
                            continue

                        line = text.count('\n', 0, offset + bad.start()) + 1
                        rel = os.path.relpath(path, ROOT)
                        findings.append(
                            f'{rel}:{line}: {type_name} 的这个构造函数直接注入 '
                            f'`{bad.group(1)} {bad.group(2)}`；应改为 `IDbContextProvider<{bad.group(1)}>`'
                        )

    if findings:
        print('EF 存储/管理器直接注入 DbContext（应经 IDbContextProvider）：', file=sys.stderr)
        for finding in findings:
            print(f'  {finding}', file=sys.stderr)
        print(file=sys.stderr)
        print('理由见 scripts/check-dbcontext-access.py 顶部说明。', file=sys.stderr)
        return 1

    print(f'DbContext 访问口径检查通过（扫描 {scanned} 个文件）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
