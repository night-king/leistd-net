---
name: developing-leistd-framework
description: 在 leistd-net 仓库中为 framework/components、framework/ddd-struct、公共 API、依赖注入、Options、NuGet 打包及随包文档设计方案、制定实施计划、新增、修改、审查或排查时使用，例如“新增组件”“规划 DDD 基座调整”“审视组件边界”。不用于下游项目仅消费 Leistd 包或只维护项目模板。
---

# 开发 Leistd 框架

## 事实与边界

从目标文件和直接依赖建立事实；证据不足或涉及公共边界时再扩大范围。同类实现和测试提供写法，公共契约、安全或版本结论以权威规范及实际验证为准。规则按任务读取[开发指南](../../../docs/framework/development-guide.md)对应章节，不在此复述。

| 场景 | 补充事实与规范 | 验证重点 |
| --- | --- | --- |
| 新增包、类型或注册入口 | 同家族包与目录；[§1 命名与分组](../../../docs/framework/development-guide.md#1-命名与分组) | csproj 约定、命名空间闸门、打包 |
| 组件内部实现 | 目标实现、测试、调用方 | 相关单测，外部语义用真实依赖 |
| DI、Options、生命周期 | 注册入口、宿主、组件文档；[§6.6 依赖注入](../../../docs/framework/development-guide.md#66-依赖注入)、[§6.2 参数与配置校验](../../../docs/framework/development-guide.md#62-参数与配置校验) | 真实容器或最小宿主、默认值消费者 |
| 包依赖、DDD 分层或组合 | 各层项目引用、组合根；[§5 依赖方向](../../../docs/framework/development-guide.md#5-依赖方向不可违反) | 层依赖、组合宿主、受影响模板场景 |
| 公共 API 设计或变更 | 目标家族公共类型、消费者；[§6 公共 API](../../../docs/framework/development-guide.md#6-公共-api-的设计与变更)、`docs/framework/versioning.md` | 构建和包内容；依赖或集成契约变化时隔离消费，影响模板时验证生成场景 |
| 测试 | 同家族测试项目；[§7 测试](../../../docs/framework/development-guide.md#7-测试) | 测试布局与测试名闸门 |
| 随包文档、XML 注释 | 目标 `framework/docs/` 和相关源码；[§4 文档注释](../../../docs/framework/development-guide.md#4-文档注释) | 检查引用、示例与打包内容；不因纯文档改动运行隔离消费 |
| 缺陷修复 | 复现路径、相关测试与调用方 | 先写修复前失败的回归测试，修复后按 L1 扩大回归 |
| 审查或排障 | 当前行为、复现、相关规范 | 证据与未检查范围，不自动实施 |

源码和项目引用定义实际 API 与行为；文档冲突时修正权威文档，不为兼容旧说明保留错误实现。

修改注释或文档前读取[文档注释规范](../../../docs/framework/development-guide.md#4-文档注释)，以必要契约决定保留内容，不沿用邻近文件的冗余写法。

## 方案与实施计划

涉及通用组件、DDD 基座、公共 API 或包边界的方案设计时，先搜索同主题最新文档；评估、计划与稳定规范的归属和生命周期见 [`docs/README.md`](../../../docs/README.md)。`framework/docs/` 随 NuGet 分发，只写已实现且使用者必须知道的公共契约，分发边界同见该文。同时影响 Template、Skill、CI 或发布流程时，改用 `maintaining-leistd-repository` 维护一份跨交付面计划。

## 工作流

1. 检查分支、工作区和用户已有改动，确认受影响家族、依赖方向、公共表面和消费点。
2. 用邻近实现、现有测试和调用方建立当前行为基线。
3. 实施最小变更，并为行为风险补充对应测试和 XML 注释。
4. 公共 API、包依赖、注册、默认值或运行时语义变化时同步所有当前消费者和目标家族文档，不保留未发布兼容层。
5. 按变化选择验证：Markdown 检查引用与骨架；XML 变更构建受影响项目，关键示例单独编译；行为变化运行相关测试。
6. 随包内容或公共契约变化时打包到 `.tmp/local-feed`，检查 XML、文档及依赖；包依赖或集成契约变化时用 `framework/build/test-package-consumption.ps1` 验证隔离消费。
7. 影响 Template 消费方式时使用 `developing-leistd-template` 验证相关生成场景；其他运行时语义由组件测试或最小宿主验证。

用户要求提交时只暂存本任务文件，核对验证结果；提交格式按 `docs/framework/versioning.md` 执行。破坏性提交在 `BREAKING CHANGE:` 脚注说明变化与必要动作，不强制维护旧版对照。实际发布由 `maintaining-leistd-repository` 负责；开发完成本身不触发发版。

组件契约归 `framework/docs/components/{family}.md`，DDD 组合归 `framework/docs/ddd-struct/`，维护规则归 `docs/framework/`。缺少必要文档时创建最小权威说明并更新索引，不复制精确签名或维护过程。

## 验证入口

本地只产出定向证据，按[本地验证配方](../../../docs/framework/quality-assurance.md#本地验证配方)选择，重复验证与等待 CI 按[证据复用](../../../docs/framework/quality-assurance.md#证据复用)；命令见[开发规范 §8 提交前自检](../../../docs/framework/development-guide.md#8-提交前自检)。只读任务不运行无关测试；未执行项和原因必须如实说明。
