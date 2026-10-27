# 可移植集成测试执行

状态：已完成 · 风险档：中

## 1. 需求

用户于 2026-10-27 确认：远端测试所需的通用项目适配应进入仓库，个人机器、凭据和调度偏好保留在用户配置。生成项目的真实 PostgreSQL 集成测试应可在本机、CI 或远端 Linux Docker 上运行。

- Given 没有执行器环境，When 运行集成测试，Then 保留 Testcontainers 默认创建及回收行为（RunnerContainerOwnershipTests）。
- Given 完整归属协议，When 创建 PostgreSQL，Then 使用本次运行唯一名称与全部归属标签，原业务断言不变（真实生成项目集成验证）。
- Given 部分或错误协议，When 创建容器前解析，Then 明确失败且不回显输入（RunnerContainerOwnershipTests）。

采用可选环境协议接入现有 PostgreSQL fixture，使用官方 WithName/WithLabel，保留 Ryuk。复用现有模板生成、候选包来源检查及 dotnet test 入口，不增加节点专用启动脚本。

## 4. 结果

Windows WSL 上实际生成默认 identity 项目，602 个真实 PostgreSQL 集成用例通过，含归属协议正反测试；执行器核对带本次名称与标签的 PostgreSQL 已删除，cleanupComplete=true。保留 Testcontainers 自有 Ryuk 清理机制。生成项目测试规范已同步，项目既有生成与候选包校验入口复用。CI 结果随 PR 记录。

额外运行现有 source-preflight 自检时发现基线脚本从矩阵文件提取已移到公共文件的 Invoke-External，导致该自检失败；本变更不调整该无关脚本。
