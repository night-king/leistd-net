# Leistd Framework

Leistd 是面向 **.NET 10** 的 DDD 应用框架基座，独立版本化、以 NuGet 包形式发布，供模板生成的业务项目（及其它项目）复用。

框架采用单点版本、中央包管理（CPM）、PDB 内嵌和 Source Link 源码调试。

> 组件与 DDD 基座用法从 [使用者文档](docs/README.md) 开始；框架维护规范见仓库级 [框架开发文档](../docs/framework/README.md)。

## 快速上手

```bash
# 编译整个框架
dotnet build framework/Leistd.Framework.slnx -c Release

# 本地打包（固定输出 .tmp/local-feed，每个项目产出内嵌 PDB 的 .nupkg）
# 直接 dotnet pack 是往目录里追加：组件删除后旧包会残留，被隔离消费验证当成当前代码的问题
pwsh framework/build/pack-local-feed.ps1
```

> 正式打包与发布由 CI 全自动完成（push 触发，见 `.github/workflows/release.yml`），本地一般无需手动发布。

## 目录结构

```
framework/
├── common.props              # 共享构建属性 + NuGet 元数据 + Source Link（版本读自仓库根 VERSION）
├── Directory.Build.props     # 自动导入 common.props 到所有框架项目
├── Directory.Packages.props  # CPM：第三方包版本集中声明
├── Leistd.Framework.slnx     # 框架解决方案
├── NuGet.md                  # 各包的 NuGet 自述
├── docs/                     # 随包交付的组件与 DDD 基座使用文档
├── components/               # 共享组件（aop / core / lock / security / tracing …）
└── ddd-struct/               # DDD 四层基础类型（Domain / Application(.Contracts) / Infrastructure）
```

## 关键概念（详见文档）

| 主题 | 一句话 | 详细 |
| --- | --- | --- |
| 组件用法 | 各组件的场景、安装、配置、API | [组件总览](docs/components/README.md) |
| 版本机制 | 版本基准在仓库根 `VERSION`，CI 按 Conventional Commits 推算 | [版本与发布](../docs/framework/versioning.md) |
| 消费组件 | 按家族查安装、注册、API 与运行时语义 | [使用者文档](docs/README.md) |
| 本地联调 | 模板在模板开发工作区里经本地候选包源消费框架包 | [模板开发工作区](../docs/template/development-guide.md#5-模板开发工作区) |
| 新增组件 | 命名、csproj、CPM、加入 slnx 的规范 | [开发规范](../docs/framework/development-guide.md) |
