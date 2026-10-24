#!/usr/bin/env python3
"""抽取组件文档 `## 注册`、`## 使用` 段的 C# 代码块，生成只编译的包消费项目（`docs/framework/development-guide.md` §5.1、§6.3）。

组件文档的示例按契约维护：必须能编译、只用本组件闭包里的类型。示例写错的 using、
少了参数的接口实现、跨出闭包的类型，读者照抄才会发现。本脚本把示例变成真实编译输入，
由 `framework/build/test-package-consumption.ps1` 用本地包源还原、构建：

- **`## 注册` 段**进同一个引用全部包的项目：宿主组合行可以调用兄弟组件的 `Add*`/`Use*`/`Map*`（§5.1 例外）。
  注册行常引用 `## 使用` 段声明的类型（处理器、Provider），所以该篇的 `## 使用` 代码块也一并编入。
- **`## 使用` 段**按家族各进一个项目，只引用该家族的包（依赖经 NuGet 传递）：
  示例用了闭包外的类型，编译器直接报错。
- 每个代码块是一个独立文件，`#line` 把诊断定位回 `文档:行`。using 有两种写法，二选一：
  **省略** Leistd 的 using（片段默认读者在本组件的命名空间下书写）——此时补上本家族各包
  及其传递依赖的公共命名空间，`## 注册` 段另补各组件 `Add*`/`Use*`/`Map*` 入口所在的命名空间；
  **写出** 任意一条 `using Leistd.*` 则视为完整文件，不再补任何 Leistd 命名空间，缺一条就报 CS0246。
  非 Leistd 的命名空间只有 Web SDK 的隐式 using，其余（EF Core、授权、Mapster 等）由代码块自己写。
- 每个项目另引用 EF Core：§5.1 允许组件示例使用原生 .NET 与 EF Core 类型。
- 片段的外形按 C# 语法归位：顶层语句放进一个异步方法；成员片段（如 `OnModelCreating`、控制器动作）
  放进它所属的类（DbContext / ControllerBase），并补上该类文件的常规 using；类型声明保持原样。
- 示例里没有声明的变量与业务类型由下方 `CONTEXTS` 补齐：变量一律用 `global::` 全名声明，
  不借 using 掩盖示例缺的 using；桩只声明业务示例类型，与框架公共类型同名即报错——
  桩不能替不存在的框架 API 兜底。
- 不能编译的示意片段在代码块前一行写 `<!-- no-compile: 理由 -->` 显式豁免；理由为空、
  或标记后面不是受检段里的 C# 代码块，都算错误。

每次运行先在进程内执行解析自检，再抽取；同时生成编译期反例（`selftest-*` 项目），
由调用方断言它们以指定诊断失败：P4-5～P4-7 的原始错误片段，以及从真实代码块删掉 using 的注入反例。

用法：python3 scripts/extract-doc-snippets.py --output <目录>
"""
import argparse
import json
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / 'framework' / 'docs' / 'components'
COMPONENTS = ROOT / 'framework' / 'components'
COMPILED_SECTIONS = ('注册', '使用')
REGISTRATION_PROJECT = 'registration'

FENCE_RE = re.compile(r'^\s*(`{3,}|~{3,})\s*([^\s`]*)')
H2_RE = re.compile(r'^##\s+(.+?)\s*$')
MARKER_RE = re.compile(r'^<!--\s*no-compile\s*:(.*?)-->\s*$')
USING_RE = re.compile(r'^using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?[A-Za-z_][\w.<>, ]*\s*;\s*(?://.*)?$')
NAMESPACE_RE = re.compile(r'^namespace\s+([A-Za-z_][\w.]*)\s*;\s*$')
MODIFIERS = r'(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new|override|virtual|async|extern|required)\s+)*'
TYPE_RE = re.compile(r'^' + MODIFIERS + r'(?:class|record|struct|interface|enum|delegate)\b')
MEMBER_RE = re.compile(r'^(?:public|internal|private|protected|static|override|virtual|async|sealed|abstract|required)\b')
DECLARED_TYPE_RE = re.compile(r'\b(?:class|record|struct|interface|enum)\s+([A-Za-z_]\w*)')
FRAMEWORK_TYPE_RE = re.compile(r'(?m)^\s*public\s+' + MODIFIERS + r'(?:class|record(?:\s+(?:class|struct))?|struct|interface|enum)\s+([A-Za-z_]\w*)')

# 宿主 Program.cs 的环境变量：每个片段都可直接使用。
COMMON_AMBIENT = '''
    static global::Microsoft.AspNetCore.Builder.WebApplicationBuilder builder = default!;
    static global::Microsoft.AspNetCore.Builder.WebApplication app = default!;
    static global::Microsoft.Extensions.DependencyInjection.IServiceCollection services = default!;
    static global::System.Threading.CancellationToken ct = default;
    static global::System.Threading.CancellationToken cancellationToken = default;
'''

# 各篇示例引用而未声明的上下文：
#   vars    片段可见的变量：名称 → 类型。Leistd 类型写短名，按源码索引展开成 global:: 全名；
#           桩类型展开到 DocStubs.<文档>；其余写 global:: 全名或关键字。
#   helpers 片段调用的业务辅助方法（静态，类型写全名）。
#   stubs   业务示例类型（命名空间 DocStubs.<文档>，自带 using）；不得与框架公共类型同名。
EF_CONTEXT = '''
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
'''
CONTEXTS = {
    'aspnetcore-signalr': dict(
        vars={'options': 'global::Microsoft.AspNetCore.Authorization.AuthorizationOptions'},
        stubs='''
using Microsoft.AspNetCore.Authorization;

public class AccountStillActiveRequirement : IAuthorizationRequirement;
'''),
    'auditing': dict(vars={'dbContext': 'AppDbContext'}, stubs=EF_CONTEXT),
    'authorization-data-scope': dict(
        vars={'dataScope': 'IDataScopeApplier', 'dbContext': 'ScopeDbContext', 'keyword': 'string?',
              'offset': 'int', 'limit': 'int', 'ids': 'global::System.Collections.Generic.List<global::System.Guid>'},
        stubs='''
using Microsoft.EntityFrameworkCore;

public class Order
{
    public Guid Id { get; set; }
    public string? OwnerId { get; set; }
    public string Code { get; set; } = "";
    public void Approve() { }
}

public class RoleDataScope
{
    public Guid RoleId { get; set; }
    public string ResourceName { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ScopeName { get; set; } = "";
    public string? ScopeValue { get; set; }
}

public class ScopeDbContext(DbContextOptions<ScopeDbContext> options) : DbContext(options);
'''),
    'authorization-resource': dict(
        vars={'orders': 'global::Microsoft.EntityFrameworkCore.DbSet<Order>', 'id': 'global::System.Guid',
              'authorization': 'IResourceAuthorizationService', 'subjectProvider': 'IPermissionSubjectProvider',
              'grantStore': 'IResourceGrantStore', 'grantManager': 'IResourceGrantManager',
              'permissionGrantManager': 'IPermissionGrantManager', 'dbContext': 'AppDbContext',
              'order': 'Order', 'targetUserId': 'string', 'userId': 'string'},
        stubs=EF_CONTEXT + '''
public enum OrderStatus { Active, Archived }
'''),
    'authorization': dict(
        vars={'permissionChecker': 'IPermissionChecker', 'principal': 'global::System.Security.Claims.ClaimsPrincipal',
              'grantManager': 'IPermissionGrantManager', 'grantStore': 'IPermissionGrantStore', 'roleId': 'string',
              'grantSeeder': 'IPermissionGrantSeeder', 'adminRoleId': 'string', 'orderService': 'OrderService'},
        stubs=EF_CONTEXT + '''
using Microsoft.AspNetCore.Mvc;

using System.Security.Claims;
using Leistd.Authorization.Subjects;

public class OrderDto;

public class CurrentPermissionSubjectProvider : IPermissionSubjectProvider
{
    public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

public class RoleSubjectDirectory : IPermissionSubjectDirectory
{
    public Task<PermissionSubjectInfo?> FindAsync(string providerName, string providerKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

public class OrderService
{
    public Task<IReadOnlyList<OrderDto>> GetListAsync() => throw new NotImplementedException();
    public Task<FileResult> ExportAsync() => throw new NotImplementedException();
}
'''),
    'background-jobs': dict(
        vars={'redisConnectionString': 'string', 'queue': 'IBackgroundTaskQueue', 'userId': 'string',
              'logger': 'global::Microsoft.Extensions.Logging.ILogger', 'statusReader': 'IRecurringJobStatusReader'},
        stubs=EF_CONTEXT + '''
public interface IJobRunStore
{
    Task InsertAsync(Guid id, global::Leistd.BackgroundJobs.Recurring.RecurringJobRun run, string? traceId, CancellationToken cancellationToken);
    Task SaveAsync(Guid id, global::Leistd.BackgroundJobs.Recurring.RecurringJobRun run,
        global::Leistd.BackgroundJobs.Recurring.RecurringJobRunResult result, CancellationToken cancellationToken);
}

public class LeadService
{
    public Task RecycleStaleAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ArchiveClosedAsync(TimeSpan closedFor, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class LeadOptions
{
    public TimeOnly RecycleAt { get; set; }

    public bool ArchiveEnabled { get; set; }

    public TimeSpan ArchiveAfter { get; set; }
}

public class WelcomeMailer
{
    public ValueTask SendAsync(string userId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
'''),
    'core': dict(),
    'data': dict(vars={'dbContext': 'OrderDbContext'}, stubs='''
using Microsoft.EntityFrameworkCore;

public class Order
{
    public DateTime CreatedAt { get; set; }
}

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
'''),
    'dependency-injection': dict(stubs='''
using Castle.DynamicProxy;

public interface IMyConvention;
public interface IAuditable;
public interface IOrderService;
public class OrderService : IOrderService;

public class AuditInterceptor : IInterceptor
{
    public void Intercept(IInvocation invocation) => invocation.Proceed();
}
'''),
    'email': dict(vars={'emailSender': 'IEmailSender', 'customer': 'Customer', 'html': 'string'}, stubs='''
public class Customer
{
    public string Email { get; set; } = "";
}
'''),
    'exception-handling': dict(vars={'order': 'Order'}, stubs='''
public enum OrderStatus { Pending, Shipped }

public class Order
{
    public Guid Id { get; set; }
    public OrderStatus Status { get; set; }
}
'''),
    'localization': dict(stubs='''
public class Program;
public class MyResourceMarker;
public class OrderService;
'''),
    'multi-tenancy': dict(
        # TenantSeeder 等由宿主实现，注册行只需要它们满足服务类型。
        vars={'currentTenant': 'ICurrentTenant', 'tenantId': 'global::System.Guid', 'db': 'AppDbContext', 'id': 'global::System.Guid',
              'tenantManager': 'ITenantManager', 'name': 'string', 'displayName': 'string',
              'connectionManager': 'ITenantConnectionConfigurationManager', 'expectedVersion': 'long'},
        helpers='''
    static global::System.Threading.Tasks.Task SeedAsync(object tenant) => global::System.Threading.Tasks.Task.CompletedTask;
''',
        stubs='''
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Provisioning;
using Leistd.MultiTenancy.Stores;
using Microsoft.EntityFrameworkCore;

public class Order
{
    public Guid Id { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public class IdentityControlDbContext(DbContextOptions<IdentityControlDbContext> options) : DbContext(options);

public record CreateTenantWithAdminInputDto : CreateTenantInputDto;

public class TenantSeeder : ITenantProvisioner
{
    public Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class TenantHasUsersGuard : ITenantActivationGuard
{
    public Task EnsureCanActivateAsync(TenantConfiguration tenant, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
'''),
    'notifications': dict(stubs='''
using Leistd.Notifications.Email.Recipients;
using Microsoft.EntityFrameworkCore;

public class MyProjectDbContext(DbContextOptions<MyProjectDbContext> options) : DbContext(options);

public class UserEmailRecipientResolver : INotificationRecipientResolver
{
    public Task<string?> ResolveEmailAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
'''),
    'object-mapping': dict(vars={'orders': 'global::System.Linq.IQueryable<Order>'}, stubs='''
public class Customer
{
    public string Name { get; set; } = "";
}

public class Order
{
    public Guid Id { get; set; }
    public bool IsPaid { get; set; }
    public decimal Total { get; set; }
    public Customer Customer { get; set; } = new();
}

public class OrderDto
{
    public Guid Id { get; set; }
    public decimal Total { get; set; }
    public string CustomerName { get; set; } = "";
}

public class UpdateOrderDto;
'''),
    'operation-records': dict(vars={'recorder': 'IOperationRecorder', 'currentUser': 'ICurrentUser'}, stubs=EF_CONTEXT),
    'realtime': dict(stubs='''
using Leistd.RealTime.Subscriptions;

public class ProductProfileDto;

public class MyResourceAuthorizer : IRealTimeSubscriptionAuthorizer
{
    public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
'''),
    'response': dict(vars={'bytes': 'byte[]'}, stubs='''
public class CreateOrderInput;
public class OrderDto;

public interface IUserService
{
    Task<object> GetAsync(long id);
    byte[] ExportCsv();
}

public interface IOrderService
{
    OrderDto Create(CreateOrderInput input);
}

public class OrderService
{
    public Task<OrderDto> GetAsync(long id) => throw new NotImplementedException();
}
'''),
    'security': dict(),
    'service-client': dict(vars={'httpClient': 'global::System.Net.Http.HttpClient', 'id': 'global::System.Guid'}, stubs='''
using Leistd.ServiceClient.Options;

public class OrderDto;
public interface IIdentityApi;
public interface IBillingApi;
public class IdentityOptions : ServiceClientOptions;
public class BillingOptions : ServiceClientOptions;
'''),
    'settings': dict(
        vars={'settingManager': 'ISettingManager', 'currentUser': 'ICurrentUser', 'userId': 'string',
              'settings': 'ISettingProvider', 'recipientId': 'string'},
        stubs=EF_CONTEXT + '''
public class AppResource;

public class ExportOptions
{
    public const string SectionName = "Export";
    public string? Endpoint { get; set; }
}
'''),
    'tracing': dict(
        vars={'ambientContext': 'IAmbientContext', 'principal': 'global::System.Security.Claims.ClaimsPrincipal',
              'message': 'IncomingMessage', 'handler': 'IncomingMessageHandler'},
        stubs='''
public class Order
{
    public string CorrelationId { get; set; } = "";
}

public class IncomingMessage
{
    public string CorrelationId { get; set; } = "";
}

public class IncomingMessageHandler
{
    public Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class ThirdPartyClient(HttpClient httpClient);

public class OrderSubmitter
{
    public Task SubmitAsync(Order order, CancellationToken cancellationToken) => Task.CompletedTask;
}
'''),
    'unit-of-work': dict(
        vars={'unitOfWorkManager': 'IUnitOfWorkManager', 'dbContext': 'AppDbContext', 'input': 'PlaceOrderInput',
              'dbContextProvider': 'IDbContextProvider<AppDbContext>', 'order': 'Order'},
        helpers='''
    static global::System.Threading.Tasks.Task ImportAsync() => global::System.Threading.Tasks.Task.CompletedTask;
    static global::System.Collections.Generic.IEnumerable<global::DocStubs.UnitOfWork.OrderLine> CreateLines(global::System.Guid orderId) => [];
''',
        stubs='''
using Leistd.EventBus.Events;
using Microsoft.EntityFrameworkCore;

public class PlaceOrderInput
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

public class Order(PlaceOrderInput input)
{
    public Guid Id { get; set; }
}

public class OrderLine;

public class Stocks
{
    public void Deduct(Guid productId, int quantity) { }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public Stocks Stocks { get; } = new();
}

public class OrderCreatedEvent : LocalEvent;

public class OrderValidator
{
    public Task ValidateAsync(OrderCreatedEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}
'''),
}


def pascal(name):
    return ''.join(part[:1].upper() + part[1:] for part in re.split(r'[^A-Za-z0-9]+', name) if part)


def iter_blocks(text):
    """返回 (代码块, 悬空标记)。代码块含所在二级标题、起始行（围栏后第一行，从 1 计）、正文与豁免理由。"""
    lines = text.splitlines()
    section = None
    pending = None  # (行号, 理由)
    blocks, dangling = [], []
    i = 0
    while i < len(lines):
        line = lines[i]
        fence = FENCE_RE.match(line)
        if fence:
            mark, lang = fence.group(1), fence.group(2).lower()
            j = i + 1
            while j < len(lines) and not lines[j].strip().startswith(mark):
                j += 1
            is_csharp = lang in ('csharp', 'cs', 'c#')
            if pending and not (is_csharp and section in COMPILED_SECTIONS):
                dangling.append(pending[0])
            if is_csharp:
                blocks.append(dict(section=section, line=i + 2, body=lines[i + 1:j],
                                   exempt=pending[1] if pending else None))
            pending = None
            i = j + 1
            continue
        stripped = line.strip()
        marker = MARKER_RE.match(stripped)
        if marker:
            if pending:
                dangling.append(pending[0])
            pending = (i + 1, marker.group(1).strip())
        elif stripped:
            if pending:
                dangling.append(pending[0])
                pending = None
            heading = H2_RE.match(line)
            if heading:
                section = heading.group(1)
        i += 1
    if pending:
        dangling.append(pending[0])
    return blocks, dangling


def brace_extent(lines, start):
    """从 start 起的声明结束行：首个 `{` 之后括号回到 0，或在任何 `{` 之前遇到顶层 `;`。"""
    depth, opened = 0, False
    for index in range(start, len(lines)):
        code = re.sub(r'"(?:\\.|[^"\\])*"', '""', lines[index].split('//')[0])
        for char in code:
            if char == '{':
                depth += 1
                opened = True
            elif char == '}':
                depth -= 1
            elif char == ';' and depth == 0 and not opened:
                return index
        if opened and depth <= 0:
            return index
    return len(lines) - 1


def split_snippet(body):
    """把代码块拆成 using、命名空间、顶层语句、成员片段与类型声明（各带相对行号）。"""
    usings, namespace = [], None
    index = 0
    while index < len(body):
        stripped = body[index].strip()
        if USING_RE.match(body[index]):
            usings.append((index, body[index]))
        elif stripped and not stripped.startswith('//'):
            break
        index += 1
    chunks = []  # (kind, first_line, lines)
    while index < len(body):
        line = body[index]
        if NAMESPACE_RE.match(line):
            namespace = NAMESPACE_RE.match(line).group(1)
            index += 1
            continue
        kind = None
        if line and not line[0].isspace():
            probe = index
            while probe < len(body) and (body[probe].startswith('[') or body[probe].startswith('///')):
                probe += 1
            head = body[probe] if probe < len(body) else ''
            if TYPE_RE.match(head):
                kind = 'type'
            elif MEMBER_RE.match(head):
                kind = 'member'
        if kind:
            end = brace_extent(body, index)
            chunks.append((kind, index, body[index:end + 1]))
            index = end + 1
            continue
        if chunks and chunks[-1][0] == 'statement':
            chunks[-1][2].append(line)
        else:
            chunks.append(('statement', index, [line]))
        index += 1
    return usings, namespace, chunks


def member_base(lines):
    text = '\n'.join(lines)
    if re.search(r'\boverride\s+void\s+OnModelCreating\b', text):
        return ' : global::Microsoft.EntityFrameworkCore.DbContext', ['Microsoft.EntityFrameworkCore']
    if re.search(r'^\[(?:Http\w+|Route|Authorize)\b', text, re.M):
        return ' : global::Microsoft.AspNetCore.Mvc.ControllerBase', ['Microsoft.AspNetCore.Mvc', 'Microsoft.AspNetCore.Authorization']
    return '', []


def framework_type_index():
    """短类型名 → 全名集合（public 类型，按所在文件的命名空间）。"""
    index = {}
    for root in (COMPONENTS, ROOT / 'framework' / 'ddd-struct'):
        for path in root.rglob('*.cs'):
            if {'bin', 'obj'} & set(path.parts):
                continue
            text = path.read_text(encoding='utf-8')
            namespace = PUBLIC_NAMESPACE_RE.search(text)
            if namespace:
                for name in FRAMEWORK_TYPE_RE.findall(text):
                    index.setdefault(name, set()).add(f'{namespace.group(1)}.{name}')
    return index


TYPE_INDEX = {}


DOC_TYPES: dict[str, set] = {}


def ambient_code(doc_key, context):
    """把上下文变量的类型写成 global:: 全名：桩类型归 DocStubs，Leistd 类型按源码索引，其余须已是全名或关键字。"""
    stub_names = set(DECLARED_TYPE_RE.findall(context.get('stubs', '')))
    lines = []
    for name, declared in context.get('vars', {}).items():
        def qualify(match):
            word = match.group(0)
            if word in DOC_TYPES.get(doc_key, set()):
                return f'global::DocSnippets.{pascal(doc_key)}.{word}'
            if word in stub_names:
                return f'global::DocStubs.{pascal(doc_key)}.{word}'
            candidates = TYPE_INDEX.get(word, set())
            if len(candidates) > 1:
                raise SystemExit(f'{doc_key}: 上下文变量 {name} 的类型 {word} 在框架中不唯一：{sorted(candidates)}')
            return f'global::{next(iter(candidates))}' if candidates else word
        resolved = re.sub(r'(?<![.:\w])[A-Z]\w*', qualify, declared)
        lines.append(f'    static {resolved} {name} = default!;')
    return '\n'.join(lines) + '\n' + context.get('helpers', '')


def render(source_path, block, doc_key, context, has_stubs, implied=()):
    usings, namespace, chunks = split_snippet(block['body'])
    origin = block['line']
    out = []

    def mapped(relative, lines):
        out.append(f'#line {origin + relative} "{source_path}"')
        out.extend(lines)
        out.append('#line default')

    for relative, line in usings:
        mapped(relative, [line])
    members = [chunk for chunk in chunks if chunk[0] == 'member']
    statements = [chunk for chunk in chunks if chunk[0] == 'statement' and any(l.strip() for l in chunk[2])]
    base, context_usings = member_base([l for chunk in members for l in chunk[2]]) if members else ('', [])
    for name in [*context_usings, *implied]:
        out.append(f'using {name};')
    if has_stubs:
        out.append(f'using DocStubs.{pascal(doc_key)};')
    out.append(f'namespace {namespace or "DocSnippets." + pascal(doc_key)};')
    ambient = COMMON_AMBIENT + context.get('ambient', '') + ambient_code(doc_key, context)
    suffix = f'L{origin}'
    if statements:
        out.append(f'internal static partial class Snippet{suffix}\n{{{ambient}')
        out.append('    internal static async global::System.Threading.Tasks.Task RunAsync()\n    {')
        for _, relative, lines in statements:
            mapped(relative, lines)
        out.append('    }\n}')
    if members:
        out.append(f'internal partial class SnippetMembers{suffix}{base}\n{{{ambient}')
        for _, relative, lines in members:
            mapped(relative, lines)
        out.append('}')
    for kind, relative, lines in chunks:
        if kind == 'type':
            mapped(relative, lines)
    return '\n'.join(out) + '\n'


PUBLIC_NAMESPACE_RE = re.compile(r'(?m)^namespace\s+(Leistd(?:\.\w+)*)\s*;')
ENTRY_RE = re.compile(r'public\s+static\s+[^=;{]*?\b(?:Add|Use|Map)\w*\s*(?:<[^>]*>)?\s*\(\s*this\b')


def namespace_index():
    """(包 → 含公共类型的命名空间, 含 Add*/Use*/Map* 扩展入口的命名空间)。"""
    by_package, entries = {}, set()
    for project in COMPONENTS.glob('*/Leistd.*/Leistd.*.csproj'):
        names = by_package.setdefault(project.stem, set())
        for path in project.parent.rglob('*.cs'):
            if {'bin', 'obj'} & set(path.relative_to(project.parent).parts):
                continue
            text = path.read_text(encoding='utf-8')
            namespace = PUBLIC_NAMESPACE_RE.search(text)
            if not namespace or not re.search(r'(?m)^public\s', text):
                continue
            names.add(namespace.group(1))
            if ENTRY_RE.search(text):
                entries.add(namespace.group(1))
    return by_package, entries


def package_closure(packages):
    """包及其经 ProjectReference 传递依赖的全部 Leistd 包。"""
    projects = {p.stem: p for p in (ROOT / 'framework').rglob('Leistd.*.csproj')
                if not {'bin', 'obj', 'tests'} & set(p.relative_to(ROOT).parts)}
    closure, pending = set(), list(packages)
    while pending:
        name = pending.pop()
        if name in closure or name not in projects:
            continue
        closure.add(name)
        text = projects[name].read_text(encoding='utf-8')
        pending += [Path(ref.replace('\\', '/')).stem for ref in re.findall(r'<ProjectReference\s+Include="([^"]+)"', text)]
    return closure


def implied_usings(body, packages, index, registration):
    """省略 Leistd using 的片段补上本家族包闭包的命名空间；写了任意一条 `using Leistd.*` 的不补。"""
    if any(re.match(r'^using\s+(?:static\s+)?Leistd\.', line) for line in body):
        return []
    by_package, entries = index
    names = set().union(*(by_package.get(package, set()) for package in package_closure(packages)))
    if registration:
        names |= entries
    return sorted(names)


def extra_packages():
    """§5.1 允许所有组件示例使用 EF Core 类型：每个编译项目都引用它（版本取框架的中央包版本）。"""
    props = (ROOT / 'framework' / 'Directory.Packages.props').read_text(encoding='utf-8')
    version = re.search(r'<PackageVersion\s+Include="Microsoft\.EntityFrameworkCore"\s+Version="([^"]+)"', props)
    if not version:
        raise SystemExit('framework/Directory.Packages.props 中找不到 Microsoft.EntityFrameworkCore 的版本。')
    return [dict(id='Microsoft.EntityFrameworkCore', version=version.group(1))]


def family_packages(family):
    directory = COMPONENTS / family
    return sorted(p.stem for p in directory.glob('*/Leistd.*.csproj')) if directory.is_dir() else []


def framework_type_names():
    names = set()
    for root in (COMPONENTS, ROOT / 'framework' / 'ddd-struct'):
        for path in root.rglob('*.cs'):
            if {'bin', 'obj'} & set(path.parts):
                continue
            names.update(FRAMEWORK_TYPE_RE.findall(path.read_text(encoding='utf-8')))
    return names


def check_stubs(doc_key, context, framework_types):
    stubs = context.get('stubs', '')
    shadowed = sorted(set(DECLARED_TYPE_RE.findall(stubs)) & framework_types)
    if shadowed:
        raise SystemExit(f'{doc_key}: 桩声明了与框架公共类型同名的类型 {shadowed}；桩只补业务示例类型。')
    if re.search(r'\bnamespace\b', stubs):
        raise SystemExit(f'{doc_key}: 桩不得自带 namespace（统一放在 DocStubs.{pascal(doc_key)}）。')


def stub_file(doc_key, context):
    stubs = context.get('stubs', '').strip('\n')
    head = [line for line in stubs.splitlines() if USING_RE.match(line)]
    rest = [line for line in stubs.splitlines() if not USING_RE.match(line)]
    return '\n'.join(head + [f'namespace DocStubs.{pascal(doc_key)};'] + rest) + '\n'


class Project:
    def __init__(self, name, packages, expect='pass', diagnostics=None):
        self.name, self.packages, self.expect = name, packages, expect
        self.diagnostics = diagnostics or []
        self.files = {}


def add_snippet(project, doc_key, source_path, block, context, framework_types, index, family=None):
    check_stubs(doc_key, context, framework_types)
    registration = block['section'] == '注册'
    implied = implied_usings(block['body'], family_packages(family or doc_key), index, registration)
    has_stubs = bool(context.get('stubs', '').strip())
    if has_stubs:
        project.files[f'stubs-{doc_key}.cs'] = stub_file(doc_key, context)
    project.files[f'{doc_key}-L{block["line"]}.cs'] = render(source_path, block, doc_key, context, has_stubs, implied)


# ---- 编译期反例：P4-5～P4-7 原始错误片段（fac8a15e 版文档原文） ----
FIXTURES = [
    dict(name='P4-5', project='registration', family=None, codes=['CS0246'], doc='''## 注册

```csharp
using Leistd.DependencyInjection.Extensions;

builder.Host.UseServiceProviderFactory(new ServiceRegistrationCallbackFactory());
```
'''),
    # 组件示例用了 ddd 仓储：把原文的 repository 按它实际指代的仓储类型声明，
    # 在 unit-of-work 家族闭包内该命名空间根本不存在（诊断落在这条上下文声明上）。
    dict(name='P4-6-closure', project='use', family='unit-of-work', codes=['CS0234'],
         path='selftest-p4-6-closure/selftest-L4.cs',
         ambient='''
    static global::Leistd.Ddd.Domain.Repositories.IRepository<global::Leistd.Ddd.Domain.Entities.Entity> repository = default!;
''', doc='''## 使用

```csharp
// 对：先冲刷，冲刷才是抛出点
repository.InsertAsync(entity, ct);
try { await unitOfWorkManager.Current!.SaveChangesAsync(ct); }
catch (DbUpdateException) { /* 这里才捕获得到 */ }
```
'''),
    # 同一片段在全部包可见时：漏写的 await 由 CS4014（按错误处理）抓住。
    dict(name='P4-6-await', project='registration', family=None, codes=['CS4014'],
         ambient='''
    static global::Leistd.Ddd.Domain.Repositories.IRepository<global::Leistd.Ddd.Domain.Entities.Entity> repository = default!;
    static global::Leistd.Ddd.Domain.Entities.Entity entity = default!;
    static global::Leistd.UnitOfWork.IUnitOfWorkManager unitOfWorkManager = default!;
''', doc='''## 使用

```csharp
using Microsoft.EntityFrameworkCore;

// 对：先冲刷，冲刷才是抛出点
repository.InsertAsync(entity, ct);
try { await unitOfWorkManager.Current!.SaveChangesAsync(ct); }
catch (DbUpdateException) { /* 这里才捕获得到 */ }
```
'''),
    # 原文省略 using；补上两条真实命名空间后，少了 CancellationToken 参数的实现报 CS0535。
    dict(name='P4-7', project='use', family='unit-of-work', codes=['CS0535'],
         stubs='''
using Leistd.EventBus.Events;

public class OrderCreatedEvent : LocalEvent;
''', doc='''## 使用

```csharp
using Leistd.EventBus.EventHandlers;
using Leistd.UnitOfWork.Events;

[UnitOfWorkEventHandler(UnitOfWorkPhase.BeforeCommit)]
public class ValidateOrderHandler : IEventHandler<OrderCreatedEvent>
{
    public Task HandleAsync(OrderCreatedEvent @event) => ValidateAsync(@event);
}
```
'''),
]


def build_projects(framework_types, index):
    projects = {REGISTRATION_PROJECT: Project(REGISTRATION_PROJECT, '*')}
    exempt, total = [], 0
    errors = []
    for doc in sorted(DOCS.glob('*.md')):
        if doc.name == 'README.md':
            continue
        doc_key = doc.stem
        source_path = doc.relative_to(ROOT).as_posix()
        blocks, dangling = iter_blocks(doc.read_text(encoding='utf-8'))
        errors += [f'{source_path}:{line}: no-compile 标记后面不是 `## 注册`/`## 使用` 段的 C# 代码块' for line in dangling]
        context = CONTEXTS.get(doc_key, {})
        # 正文代码块自己声明的类型优先于桩：上下文变量指向它们。
        DOC_TYPES[doc_key] = {DECLARED_TYPE_RE.search(next(line for line in lines if TYPE_RE.match(line))).group(1)
                              for block in blocks if block['section'] in COMPILED_SECTIONS and block['exempt'] is None
                              for kind, _, lines in split_snippet(block['body'])[2] if kind == 'type'
                              and DECLARED_TYPE_RE.search(next(line for line in lines if TYPE_RE.match(line)))}
        for block in blocks:
            if block['section'] not in COMPILED_SECTIONS:
                continue
            if block['exempt'] is not None:
                if not block['exempt']:
                    errors.append(f'{source_path}:{block["line"] - 1}: no-compile 标记必须写明理由')
                exempt.append(f'{source_path}:{block["line"]} {block["exempt"]}')
                continue
            total += 1
            add_snippet(projects[REGISTRATION_PROJECT], doc_key, source_path, block, context, framework_types, index)
            if block['section'] == '使用':
                packages = family_packages(doc_key)
                if not packages:
                    errors.append(f'{source_path}: 找不到家族 framework/components/{doc_key} 的包')
                    continue
                project = projects.setdefault(f'use-{doc_key}', Project(f'use-{doc_key}', packages))
                add_snippet(project, doc_key, source_path, block, context, framework_types, index)
    if errors:
        raise SystemExit('\n'.join(errors))

    # 编译期反例。
    for fixture in FIXTURES:
        key = 'selftest-' + fixture['name'].lower()
        packages = '*' if fixture['project'] == 'registration' else family_packages(fixture['family'])
        source_path = f'selftest/{fixture["name"]}.md'
        project = Project(key, packages, 'fail', [dict(path=fixture.get('path', source_path), codes=fixture['codes'])])
        blocks, _ = iter_blocks(fixture['doc'])
        context = dict(ambient=fixture.get('ambient', ''), stubs=fixture.get('stubs', ''))
        add_snippet(project, 'selftest', source_path, blocks[0], context, framework_types, index,
                    fixture['family'] or 'dependency-injection')
        projects[key] = project

    # 注入反例：取第一个写出两条以上 `using Leistd.*`（完整文件写法）的受检代码块，
    # 每次删掉其中一条，都必须编译失败——写出的 using 不会被补齐掩盖。
    leistd_using = re.compile(r'^using\s+(?:static\s+)?Leistd\.')
    injected = next(((doc, block) for doc in sorted(DOCS.glob('*.md'))
                     for block in iter_blocks(doc.read_text(encoding='utf-8'))[0]
                     if block['section'] in COMPILED_SECTIONS and block['exempt'] is None
                     and sum(bool(leistd_using.match(line)) for line in block['body']) >= 2), None)
    if not injected:
        raise SystemExit('找不到写出两条以上 `using Leistd.*` 的受检代码块，无法构造缺 using 的注入反例。')
    doc, block = injected
    for number, dropped in enumerate(line for line in block['body'] if leistd_using.match(line)):
        source_path = f'selftest/missing-using-{doc.stem}-{number + 1}.md'
        variant = dict(block, body=[line for line in block['body'] if line != dropped])
        packages = '*' if block['section'] == '注册' else family_packages(doc.stem)
        project = Project(f'selftest-missing-using-{number + 1}', packages, 'fail',
                          [dict(path=source_path, codes=['CS0246', 'CS0103', 'CS1061', 'CS0234'])])
        add_snippet(project, doc.stem, source_path, variant, CONTEXTS.get(doc.stem, {}), framework_types, index)
        projects[project.name] = project
    return projects, total, exempt


def self_test():
    doc = '''## 注册

```csharp
using Leistd.Foo;
using A = Leistd.Bar;

builder.Services.AddFoo();

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ConfigureFoo();
}
```

## 配置项

```csharp
config.Ignored();
```

## 使用

### 子标题仍属使用

<!-- no-compile: 省略号示意 -->

```csharp
public Task X(...) => ...;
```

```csharp
[Attr]
public class Order(string id) : IFoo;

var order = new Order("1");
```
'''
    blocks, dangling = iter_blocks(doc)
    failures = []
    expect = lambda cond, label: None if cond else failures.append(label)
    expect([b['section'] for b in blocks] == ['注册', '配置项', '使用', '使用'], '段落归属')
    expect(blocks[2]['exempt'] == '省略号示意' and blocks[3]['exempt'] is None, '豁免标记只作用于紧随的代码块')
    expect(not dangling, '无悬空标记')
    usings, _, chunks = split_snippet(blocks[0]['body'])
    expect([u[0] for u in usings] == [0, 1], 'using 与别名 using 识别')
    expect([c[0] for c in chunks] == ['statement', 'member'], '语句与成员片段拆分')
    usings, _, chunks = split_snippet(blocks[3]['body'])
    expect([(c[0], c[1]) for c in chunks] == [('type', 0), ('statement', 2)], '特性归属类型声明、类型后的语句')
    rendered = render('x.md', blocks[0], 'demo', {}, False)
    expect('#line 4 "x.md"\nusing Leistd.Foo;' in rendered, 'using 映射回文档行')
    expect('#line 9 "x.md"\nprotected override void OnModelCreating' in rendered, '成员映射回文档行')
    expect(': global::Microsoft.EntityFrameworkCore.DbContext' in rendered, 'OnModelCreating 放进 DbContext')
    _, dangling = iter_blocks('<!-- no-compile: x -->\n\n## 使用\n\n```csharp\nx();\n```\n')
    expect(dangling == [1], '标记与代码块之间隔着标题算悬空')
    blocks, dangling = iter_blocks('## 配置项\n\n<!-- no-compile: x -->\n```csharp\nx();\n```\n')
    expect(dangling == [3], '非受检段的标记算悬空')
    blocks, _ = iter_blocks('## 使用\n\n<!-- no-compile: -->\n```csharp\nx();\n```\n')
    expect(blocks[0]['exempt'] == '', '空理由可被识别并报错')
    try:
        check_stubs('demo', dict(stubs='public interface IClock;'), {'IClock'})
        failures.append('桩与框架类型同名应报错')
    except SystemExit:
        pass
    if failures:
        raise SystemExit('代码块抽取规则自检失败: ' + ', '.join(failures))
    print('代码块抽取规则自检通过。')


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    self_test()
    TYPE_INDEX.update(framework_type_index())
    projects, total, exempt = build_projects(framework_type_names(), namespace_index())
    output = args.output.resolve()
    if output.exists():
        shutil.rmtree(output)
    manifest = []
    for project in projects.values():
        directory = output / project.name
        directory.mkdir(parents=True)
        for name, content in project.files.items():
            (directory / name).write_text(content, encoding='utf-8')
        manifest.append(dict(name=project.name, packages=project.packages, expect=project.expect,
                             diagnostics=project.diagnostics))
    (output / 'manifest.json').write_text(json.dumps(dict(projects=manifest, snippets=total, exempt=exempt,
                                                          extraPackages=extra_packages()),
                                                     ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'抽取 {total} 个代码块，豁免 {len(exempt)} 个；生成 {len(manifest)} 个编译项目。')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
