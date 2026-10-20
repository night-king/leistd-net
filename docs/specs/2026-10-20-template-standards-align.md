# 吸收 CRM 对齐模板基线的反馈

状态：已完成 · 风险档：中 · 关联：（待开 PR）

## 1. 需求

### 问题

CRM 三仓把 `docs/standards/` 与项目 Skill 恢复为模板基线（develop `6463debd`）后，逐段比对整理出 26 条模板改进项，见仓库外 `~/orca/workspaces/.crm-v2-adaptation/backlog-20261019/framework-feedback-standards-align.md`。这些条目分三类：项目写法比模板好且通用；模板文档与模板代码自相矛盾；项目侧的特有做法。

### 目标

- 逐条按框架规范、开发规范与"终局不过度设计"判断，采纳的落到模板对应层（规范、代码或项目 Skill），不采纳的写明理由。
- 文档与代码矛盾一律修正，以代码为准，代码有缺陷时修代码。
- 回执写到同目录 `framework-receipt-standards-align.md`，说明 CRM 可删掉哪些项目补充。

非目标：CRM 特有的视觉输入（`activeTone`、表格 `edge`）、语言状态与长度门禁、安全参数的额外启动校验。

### 验收标准

| # | Given | Then | 覆盖 |
| --- | --- | --- | --- |
| 1 | 宿主错误码与组件错误码完整重名 | `ApiExceptionMappingsTests` 失败 | 自动化 |
| 2 | 外部登录设置端点只带命名权限策略 | 类上补 `[Authorize]`，默认策略（自然人）同样生效 | `ExternalLoginSettingsTests` 回归 |
| 3 | 菜单与同名页面标题、根权限显示名的英文 | 一律句首大写 | `command-palette.spec.ts` |
| 4 | HTTP 错误没有可用的 Problem Details（空正文 5xx、HTML 正文、2xx 解析失败） | 不显示 `statusText`、正文或原始异常，按 `category` 取本地化通用文案；非安全方法提示"可能已完成，先核实" | `application-http-error.spec.ts`、`http-error-interceptor.spec.ts` |
| 5 | `hlm-select-trigger` 设可读名；`hlm-field-error` 以 `forceShow` 显示 | 可读名落在 combobox 按钮上；错误进入 `aria-describedby` | 组件 spec |
| 6 | 规范与随包脚本、模板代码的描述 | 动作码词条单向、设置页提示位置、外观行存储与代码一致 | 人工核对 |

方案：规范只补"默认就会写错"且其他文档未覆盖的规则，已在组件随包文档里的不重复；代码修正限于真实缺陷与已有使用者的通用可访问性问题。逐条结论见回执。

## 4. 结果

### 实际交付

逐条结论与 CRM 可删项见回执。采纳与部分采纳的落点：

- 项目 Skill：验证入口以项目测试规范为准；等待 CI 写成通用原则；先复现目标失败；零执行或缺报告不算通过。
- 规范：`api.md`（前缀与完整码、组件码译文、动作路由）、`auth.md`（来源防护路径）、`background-jobs.md`（`isEnabled` 与失败抛出）、`coding-backend.md` §3.7（偏离组件默认的层级、无参授权）、`service-invocation.md`（不绕开标准管道、类级 `[Authorize]`、共用地址节）、`testing.md`（`UseSetting`、E2E 等待与因果屏障、动作码单向）、`settings.md`（提示位置、外观行存储）、`frontend-lists.md`（展开详情写法、时间列 Tooltip）、`frontend-navigation.md`（句首大写）、`coding-frontend.md` §6（错误归一化）、`frontend-spartan.md` 与 `frontend-ui.md`（定制登记、验证码格 44px）、`frontend-i18n.md`（语言清单少一处）。
- 代码：`ApiExceptionMappingsTests` 互不重名断言；`ExternalLoginSettingController` 类级 `[Authorize]`；菜单句首大写（前后端词条、权限显示名、Mock、页面标题）；`ApplicationHttpError` 分类与公开文案收紧、拦截器传请求方法、删死词条 `httpError.*`；`hlm-select-trigger` 透传可读名并修分页器；`hlm-field-error` 按可见态登记；`input-otp-slot` 粗指针 44px；"已锁定"徽章可聚焦；`availableLangs` 取 `SUPPORTED_LANGS`。

### 与计划的偏差

- 模板规范读取成本上限按读数放宽（五类任务各增约 0.5%–1.6%）：新增的是默认就会写错的规则，相同主题的长段落已压缩，未找到可删的重复。

### 验收记录

| 项 | 结果 |
| --- | --- |
| `identity-all-features` 前端 lint、构建、全量单测 | 通过，135 个文件 1366 个用例 |
| `identity`（关闭本地化）受影响 spec、lint、构建 | 通过，61 个文件 577 个用例 |
| 后端集成测试 `ApiExceptionMappingsTests`、`ExternalLoginSettingsTests`、权限相关 | 通过，78 个 |
| 新断言证伪 | 还原 `hlm-field-error`、分页器修正后对应 spec 失败 |
| `scripts/check-all.ps1` | 50 道静态闸门通过 |

未验证：矩阵其他场景与浏览器端到端由 CI 承担；验证码格 44px 未在真实触屏量尺寸。

### 已上收的常驻文档

均为上列 `template/docs/standards/` 与项目 Skill 的改动。
