# 头像缓存按版本判定，服务间调用补 Client 拆分与 scope 默认策略

状态：已完成 · 风险档：中（模板缓存缺陷修复，加上服务间授权约定） · 关联：—

## 1. 需求

### 问题与背景

CRM V2 三仓（abroker）在头像对接中发现了缓存问题，并整理了多服务对接的写法，反馈给框架。审视结论见协调目录 `/Users/quincy/orca/workspaces/.crm-v2-adaptation/avatar-sync-20261020/framework-feedback.md` 的「框架结论」一节。用户 2026-10-24 授权按最佳实践终局决策并实施。

**缺陷（当前行为）**：
- `UserController.GetAvatarAsync`（`template/backend/src/CompanyName.ProjectName.Api/Controllers/UserController.cs`）不看请求里的 `v`，一律返回 `private, max-age=31536000, immutable`。
- `v` 是头像内容摘要的前 12 位（`AvatarUrls.For`）。
- 失败场景：
  1. 用户先把头像从 X 换成 Y；
  2. 某个旧页面按 `?v=X` 请求，服务器返回 Y，浏览器把 Y 按不可变资源缓存在 X 的地址下；
  3. 用户以后换回 X，地址又回到 `?v=X`，此后一年内看到的都是 Y。

**第二个缺陷**：清除头像后，旧地址返回 404，但没有写 `Cache-Control`。RFC 9110 §15.5.5 允许 404 被启发式缓存。用户再上传同一张图时，地址与原来完全相同，浏览器可能在启发式新鲜期内复用那个 404。

**期望行为**：
- 只有请求的 `v` 等于当前版本，才按不可变资源缓存；否则要求每次重新验证；
- 本端点的 404 不被存储。

**不应改变的行为**：
- 头像地址的形状与版本算法；
- ETag 与 304 再验证；
- 外部地址的头像直接给原地址；
- 已登录即可访问，查询仍受租户过滤器约束；
- 没有头像仍返回 404，状态码与既有 Problem Details 管道不变。

**文档缺口**：
- 框架已经强制"一个命名客户端只挂一种认证"（`Leistd.ServiceClient.OAuth` 的 `RegisterAuthentication`，第二种认证会抛 `InvalidOperationException`）。但模板的 Client 包只有一个接口、一个固定名称，没有说明服务同时提供机器端点与用户端点时 Client 包该怎么组织。
- 委托调用与机器调用的 scope 粒度没有成文规则。模板实际做法是委托用服务级（`TokenExchange: { Audience: billing-api, Scope: billing-api }`）、机器按能力细分（`tenant-routing.read`、`tenant-migration.read`），下游项目需要一条统一规则。

### 目标与非目标

- 目标：
  - 修正头像成功响应与 404 的缓存头；
  - `template/docs/standards/service-invocation.md` 补两段：Client 包按认证方式拆分、scope 默认策略。
- 非目标：
  - 不加 `X-Avatar-Version` 响应头：模板唯一的消费者是 `<img>`，读不到它；
  - 不做全仓的错误响应缓存治理，只改本端点的 404；
  - **头像不加 `loading="lazy"`**（Codex 阶段 1 意见 2）：`hlm-avatar` 只在 `BrnAvatarImage` 加载成功（`canShow()`）后才投影图片，而原生 lazy 要等图片进入可见布局才开始加载，两者互相等待，可能一直只显示占位。分页表格的图片本来就少，收益不值得为它改共享的 Helm 组件；
  - 不加 `decoding="async"`；
  - 不改模板 Client 包的示例代码；
  - 不加调用方白名单规则：签发方的登记已经控制了哪些客户端能换出目标 audience 与 scope，业务特有的限制由项目自行加。模板生成的调用方声明是 `act.sub`（`ClientSubject.Format(clientId)`），文档不写业务仓的 `act.client_id`；
  - 不承诺清掉修复前已经污染、且仍在新鲜期内的浏览器缓存：新缓存头只防止修复后继续污染，地址形状不变；
  - 不改框架。

### 功能要求

1. **缓存头**：
   - 请求的 `v` 等于当前头像版本时，返回 `private, max-age=31536000, immutable`；
   - `v` 不一致或缺失时，返回 `private, no-cache`；
   - 两种情况都带 ETag。条件请求在校验器匹配当前内容时返回 304，内容已变时返回 200 和新内容；
   - 本端点的 404 加 `private, no-store`。
2. **版本计算只有一份**：
   - 当前版本必须与 `AvatarUrls.For` 生成地址时用的是同一个计算：完整存储 data URL 的 UTF-8 字节取 SHA-256，取前 12 个十六进制字符并转小写。
   - 不能截取 ETag 当版本：ETag 来自解码后的图片字节，是前 16 位大写并加引号。
   - 在 `AvatarUrls` 内抽出共用方法；应用服务从同一次用户读取里同时返回内容与版本，不为取版本再查一次。
   - 序数精确比较。
3. **文档**（都放在 `template/docs/standards/service-invocation.md`，写短，与现有措辞一致）：
   - **"发布本服务的 Client 包"补一句**：服务同时提供机器端点与用户端点时，按认证方式拆 Refit 接口和命名客户端，各给一个注册入口；框架要求每个命名客户端只挂一种认证。单服务示例不预建两套接口。
   - **"调用其他服务"的身份模式说明之后补 scope 默认策略**：
     - 用户委托（Token Exchange）默认用服务级 scope，表示"允许代表用户访问这个服务"，默认与目标资源同名，实际以签发方登记的契约为准（`Scope` 可单独配置）；被调服务仍按用户在本地的权限判定（引用"被其他服务调用"）。项目需要更窄的应用委托能力时，显式设计对应的签发与校验契约。
     - 机器调用（client credentials）按能力分组细分 scope，保持最小权限，例如 `tenant-routing.read`；不按单个端点机械拆分，也不收敛成服务级。机器令牌没有用户权限兜底，scope 是它主要的能力授权边界，资源与租户校验照常保留。

### 验收标准

| # | Given / When / Then | 覆盖 |
| --- | --- | --- |
| V1 | Given 用户有上传的头像，When 以当前 `v` 请求，Then `Cache-Control` 为 `private, max-age=31536000, immutable`，带 ETag | 集成测试（沿用现有 `AvatarTests`） |
| V2 | Given 同上，When 以过期的 `v` 请求或不带 `v`，Then `Cache-Control` 为 `private, no-cache`，返回当前内容并带 ETag；带匹配当前内容的 `If-None-Match` 时返回 304 | 集成测试 |
| V3 | Given 用户把头像从 X 换成 Y，When 以 X 的 `v` 请求，Then 不是 `immutable`；When 换回 X 后以 X 的 `v` 请求，Then 返回 X 且为 `immutable` | 集成测试，先写，修复前红灯落在"旧版本请求不应 immutable"。TestServer 不模拟浏览器缓存，只断言响应头 |
| V3e | Given 上传 X 后清除头像，When 以 X 的地址请求，Then 返回 404 且 `Cache-Control` 为 `private, no-store`；When 再上传 X 后以同一地址请求，Then 返回 X | 集成测试 |
| V4 | Given 不应改变的行为，Then 现有头像相关测试不改断言即通过（可在其中补充缓存头断言） | 现有测试 |
| V6 | Given 文档两段，Then 文档引用、锚点、读取成本闸门通过 | `check-all -Only` 相关闸门 |
| V7 | Given 选择器给出的场景，Then `-GenerateOnly` 通过；每个场景对改动文件做格式检查、后端按场景构建；受影响的后端集成测试通过 | 本地验证配方 |

### 已确认的决策

- 用户 2026-10-24：按最佳实践终局决策并实施；沿用此前的流程（Codex 审方案直到明确通过，Codex 实施与验证，Claude 审代码后合入 develop）。

### 方案

- 后端：缓存头按版本判定，404 不存储；版本计算与 `AvatarUrls` 共用；先写 V3、V3e 回归测试。
- 文档两段。
- 不改前端。
- 只改模板，不出框架 beta；模板正式包随下一次 develop→main 同号发布。

## 4. 结果

### 实际交付

- **后端**：
  - `AvatarUrls.VersionOf` 抽出唯一的版本计算，地址生成与读取响应共用；
  - `UserAvatarOutputDto` 增加 `Version`，`UserAppService.GetAvatarAsync` 从同一次读取里同时返回内容与版本；
  - `UserController.GetAvatarAsync`：请求 `v` 与当前版本序数比较，匹配时 `immutable`，否则 `private, no-cache`；404 加 `private, no-store`。
- **测试**：`AvatarTests` 新增"换图再换回"、"清除再上传同图"两条回归，以及 5 种非精确版本的用例；原有断言保留，补充缓存头断言。
- **文档**：`template/docs/standards/service-invocation.md` 补 scope 默认策略、按认证方式拆分 Client 包两段。同一文件里有一个 JSON 示例：生成时条件块被裁掉后，它变成可解析的 JSON，被生成文档的 Prettier 闸门（`scripts/check-generated-markdown.mjs`）格式化。因此按 Prettier 重排了它。

### 与计划的偏差

阶段 1 审查（Codex）三条意见全部采纳：
- 404 可能被启发式缓存，加 `no-store` 并新增 V3e；
- `loading="lazy"` 与 `hlm-avatar` 的 `canShow()` 互相等待，移出范围，删除 V5；
- scope 规则改为可按签发方契约调整的模板默认策略。

框架结论里对应的三处说法随后在反馈文件中更正。

### 验收记录（证据在 `.tmp/avatar-cache/validation/`，未提交）

| 项 | 结果 |
| --- | --- |
| V3、V3e | 修复前 2 个失败，都是行为断言：旧版本请求仍为 `immutable`；404 没有 `Cache-Control`。修复后通过 |
| V1–V4 | `AvatarTests` 14/14 通过（真实 PostgreSQL），领域规则 17/17 通过 |
| V6 | 6 项闸门通过，读取成本不变 |
| V7 | 选择器给出 5 个场景，`-GenerateOnly` 通过；各场景 Release 构建、C# style 与 whitespace、文档 Prettier 共 20 项检查通过 |

### 遗留项

修复前已经被污染、且仍在新鲜期内的浏览器缓存不会被追溯清除；地址形状不变，这一点按规格接受。

### 已上收的常驻文档

`template/docs/standards/service-invocation.md`。
