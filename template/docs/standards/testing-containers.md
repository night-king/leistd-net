# 测试容器执行

源码、测试进程与 Docker bind mount 必须位于同一执行节点；不要仅切换 Docker context 后继续使用本机路径。集成测试仍通过 `dotnet test` 运行，执行节点和调度策略由调用方选择。

## 可选归属协议

集成测试支持执行器提供的 `RPR_DOCKER_LABELS`（JSON 字符串标签）和 `RPR_DOCKER_NAME_PREFIX`。两者必须同时提供，标签包含 managed=true、task、attempt 的 `remote-project-runner.*` 字段；PostgreSQL 容器使用唯一名称并保留全部标签。未提供时沿用 Testcontainers 默认行为。错误或不完整的协议在创建容器前失败，诊断不回显原始输入。

## 回收

Testcontainers 的 Ryuk 辅助容器继续由框架管理，不套用 PostgreSQL 的名称和标签；保留 Resource Reaper，执行器验收需同时确认 PostgreSQL 与本次 Ryuk 均已回收。测试进程被取消或断链时也不关闭 Resource Reaper 来绕过归属检查。执行器仅按本任务精确归属回收，不按可读名称前缀或全局 prune 删除资源。
