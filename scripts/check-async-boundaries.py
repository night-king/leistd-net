#!/usr/bin/env python3
"""连接解析与工作单元路径上的 sync-over-async 检查。

基线验收标准第 6 条要求：动态连接路径不存在 `.Result`、`.Wait()` 或
`GetAwaiter().GetResult()`。这条声明此前没有任何自动检查兜着——本脚本把它变成门禁。

**为什么只管这几条路径，而不是全仓禁用。** sync-over-async 在别处是判断题：
`IAsyncDisposable` 桥到 `IDisposable`、EF 同步 `SaveChanges` 里发本地事件，
都是没有异步出口的既定折中（后者还打了 Warning 日志）。一刀切会产出必须逐条豁免的噪声，
而需要豁免清单的门禁最后都会被关掉。

连接解析路径不一样：它在**每次取 DbContext 时**执行，阻塞的后果是线程池饥饿下的
请求排队，而排队又拉长阻塞——高负载下会自我放大。这里没有"权衡",只有错。
"""
import io, os, re, sys

ROOT = os.path.join(os.path.dirname(__file__), '..')

# 禁区：连接解析与工作单元的动态连接实现
ZONES = [
    'framework/components/unit-of-work',
    'framework/components/multi-tenancy',
    'framework/components/data/Leistd.Data',
    'template/backend/src/CompanyName.ProjectName.Infrastructure/TenantConnections',
]

PATTERNS = [
    (re.compile(r'\.GetAwaiter\(\)\s*\.\s*GetResult\(\)'), 'GetAwaiter().GetResult()'),
    (re.compile(r'\.Wait\(\)'), '.Wait()'),
    # .Result 只在 Task/ValueTask 上是阻塞；MVC 的 context.Result 之类同名属性要排除，
    # 因此要求前面是一个调用（...Async(...).Result 形态）。
    # 已知漏报：先把 Task 存进局部变量再取 `localVar.Result` 不会命中本正则——
    # 精确判定需要语义分析，这里刻意接受该盲区，靠代码审查兜底，不为它引入 Roslyn 依赖
    (re.compile(r'\)\s*\.\s*Result\b'), '.Result'),
]

# 测试替身与测试自身允许阻塞：它们不在请求路径上
SKIP_DIR = ('obj', 'bin', 'node_modules')


def main():
    findings = []
    scanned = 0
    for zone in ZONES:
        base = os.path.join(ROOT, zone)
        if not os.path.isdir(base):
            print(f'⚠️  禁区路径不存在，可能已被重命名：{zone}')
            return 1
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d not in SKIP_DIR]
            for name in fn:
                if not name.endswith('.cs'):
                    continue
                path = os.path.join(dp, name)
                scanned += 1
                for i, line in enumerate(
                        io.open(path, encoding='utf-8', errors='replace').read().split('\n'), 1):
                    code = line.split('//', 1)[0]
                    for pattern, label in PATTERNS:
                        if pattern.search(code):
                            findings.append((os.path.relpath(path, ROOT), i, label, line.strip()))

    if not findings:
        print(f'✅ 动态连接路径无 sync-over-async（{scanned} 个文件，{len(ZONES)} 个禁区）。')
        return 0

    for path, line, label, text in findings:
        print(f'❌ {path}:{line} 出现 {label}')
        print(f'   {text}')
    print(f'\n共 {len(findings)} 处。连接解析在每次取 DbContext 时执行，'
          f'阻塞会在线程池饥饿下自我放大——这条路径必须全程异步。')
    return 1


if __name__ == '__main__':
    sys.exit(main())
