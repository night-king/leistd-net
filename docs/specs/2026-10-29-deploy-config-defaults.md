# 部署默认配置与配置查找指引

状态：已完成 · 风险档：中

## 1. 需求

用户于 2026-10-29 确认：默认配置即推荐配置，减少部署变量，并让开发者与 AI 知道清单外的配置如何查找、覆盖。交付面为模板部署资产、项目规范及仓库设计原则；不修改框架公共 API、外部登录变量或邮箱验证业务逻辑，不执行 Git 写操作。

| 项 | 现状与核对 | 采纳方案 |
| --- | --- | --- |
| 部署变量边界 | 未限定哪些值进入 Compose；布尔/数值缺省规则容易被理解为要求列出默认值 | 仅登记部署时才能确定的值；推荐调优项按标准键覆盖，已有界面设置不另设变量；限定布尔/数值规则适用范围 |
| 配置查找与覆盖 | 未串起框架随包文档、模板 Options、部署清单 | 配置参考分别说明三处来源、模板可调键的用途与默认值，以及 Compose/K8s 覆盖方式；部署入口与读取表链接到该入口 |
| `SMTP_ENABLE_SSL` | 组件默认 true，但模板基础档实际为 false，直接删除会让生产关闭 TLS | 删除变量，将 Mailpit 的 false 移到 Development 档，生产沿用组件推荐值 true |
| `TZ` | 后端业务时间按 UTC 处理，无需配置容器时区 | 删除 API、PostgreSQL、Redis 的 TZ 注入与清单项 |
| `ENABLE_EMAIL_VERIFICATION` | 与「注册设置」重叠；启动期读配置键，但运行期已有独立的设置校验器 | 删除变量，保留摘要密钥及标准配置键的启动校验，界面负责开关 |
| `IDENTITY_ISSUER` / `IDENTITY_AUTHORITY` / `IDENTITY_BASE_ADDRESS` | 三项各自必填 | 仅 Issuer 必填；另两项未设或为空时沿用 Issuer，仅内网地址不同时填写；API 与多租户 DbMigrator 同步 |
| 展示时区 | 当前基线是本地化开关两侧的互斥说明，并非生成后出现两遍 | 合并到模板可调键表，每种生成场景只保留适用的一行 |

方案：部署资产不再复制推荐默认值；框架键与默认值引用随包文档的「配置项」，模板自身可调项在配置参考按主题说明；清单外非机密通过 Production 档覆盖，机密与环境差异通过标准环境键覆盖。地址沿用采用 [Compose 官方支持的嵌套缺省](https://docs.docker.com/reference/compose-file/interpolation/)，不新增解析代码。

验收标准：

| Given / When / Then | 验证 |
| --- | --- |
| 给定全部有效模板形态，生成后部署变量与 `.env.example` 双向一致，配置键有效，外部登录变量不变 | 四片 `test-template-generation.py`、部署资产判据自检、差异核对 |
| 给定 Email/LocalIdentity 与 Resource 多租户代表场景，生成后文件与文档条件裁剪、引用及锚点有效 | 模板矩阵 `-GenerateOnly` |
| 给定只填必填项的环境，Compose 展开后 API/DbMigrator 两个回源地址等于 Issuer；显式内网值仍可覆盖；缺 Issuer 报错 | 生成项目 `docker --context … compose … config` 正反例 |
| 给定 Email 场景，Production 默认 TLS 开启，Development 的 Mailpit 关闭 TLS | 实际配置绑定验证 |
| 给定配置查找入口，开发者可定位未列入部署变量的键与覆盖方式，规范读取成本不超限 | 文档闸门、读取成本检查与人工核对 |

邮箱验证核查：`Application/Settings/Validators/EmailSettingValidator.cs` 已在 `DependencyInjection.cs` 注册，设置写入链的 `DefaultSettingManager` 在持久化前执行该校验器。无密钥开启时抛出 `EmailVerificationKeyMissing`，既有 `SettingsContractTests.Email_verification_cannot_be_enabled_without_a_digest_key` 断言 HTTP 409；有密钥时允许开启。发码层还有 `EnsureKeyUsable`。因此任务书担心的「界面开启、无密钥直到发码才失败」已被当前基线处理，未发现需要本任务修复的缺陷。删除别名变量后，直接在 Production 档或标准环境键配置 `UserRegistration:EnableEmailVerification=true` 仍可触发启动校验，保留它有实际意义，无需另提修复。

## 4. 结果

已完成部署变量清理、地址缺省、配置指引与规范同步，未提交或发布；外部登录的两个部署条件块与基线逐字一致。实际改动与计划的差异是 SMTP：为保持生产默认启用 TLS，把基础档的 false 下移到 Development，未扩大邮箱验证修复范围。

验收记录（本机 macOS arm64，.NET SDK 10.0.402，未提交工作树）：

| 命令或检查 | 结果 |
| --- | --- |
| `python3 scripts/measure-template-read-cost.py --check` | 全通过：backend 37101/37106，fullstack 70634/70640，UI 52932/52932，文案 19230/19233，审查 41181/41198；上限未改，新增定位文字通过精简同篇读取表抵消 |
| `pwsh scripts/check-all.ps1 -Only`：部署资产判据、条件符号、条件块、场景覆盖、文档锚点、引用、读取成本、退役符号 | 8 道最终全部通过；首次读取成本超限，精简后仅重跑该失败项通过 |
| `/usr/bin/python3 scripts/test-template-generation.py --shard control --output .tmp/generation-proof/control` | 768 个输入映射、10 次实际角色等价比较、4 个 CI 变体、10 种 verify 签名通过 |
| 同命令 `--shard 0/1/2 --output .tmp/generation-proof/<分片>` | 分别 107、107、106 个形态通过；四份证据源文件摘要均匹配当前模板，汇总 320 个唯一形态且归属正确 |
| `pwsh scripts/test-template-matrix.ps1 -Scenarios identity,standalone-external-login,resource -GenerateOnly` | 3/3 通过，覆盖 LocalIdentity、Email、外部登录及 RemoteTokenAuth+多租户；生成文档引用、锚点与条件裁剪有效 |
| 生成项目 `docker --context default compose --env-file <临时env> -f deploy/docker-compose.yml config --format json` | 官方 v2.40.3（已核对 SHA256）与本机 v5.5.0 共 70 用例通过：只填必填项、地址未设/空/分别或同时覆盖、逐个必填项缺失、Issuer 为空；API 与 DbMigrator 展开一致，使用匿名 Docker 配置与假值，未启动容器 |
| 实际 `SmtpOptions` + 生成项目 JSON 的 .NET 配置绑定 | identity 与 standalone-external-login 的 Production/Staging=true、Development=false，6/6 通过 |
| `git diff --check` | 通过 |

完整命令、日志位置与复核差异保存在本工作树 `.tmp/deploy-config-result.md`。临时验证程序及 70 条 Compose 用例记录在 `.tmp/deploy-config/`，全部生成证据在 `.tmp/generation-proof/`。

邮箱验证仅核查实际注册、设置写入、校验器与既有测试源码，未重跑数据库/HTTP 集成测试；本次也未运行 API 全量构建、镜像构建或启动验收。本任务未修改运行时代码、Dockerfile 或镜像构建路径，生成与配置生效值按上述范围验证，不将静态核查表述为运行测试。

常驻文档已同步：仓库设计原则、模板设置规范、配置参考、部署说明与项目文档读取表。未发现需要新增的邮箱验证修复；保留标准配置键的启动校验，后续由委托方审查并提交本次改动。
