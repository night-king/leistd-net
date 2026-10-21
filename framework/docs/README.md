# Leistd 框架文档

这里仅保存面向 `Leistd.*` NuGet 使用者的公开文档，描述当前版本的公共 API、注册方式与运行时语义。各家族文档会随对应 NuGet 包一起分发。

| 内容 | 入口 |
| --- | --- |
| 通用组件：审计、授权、通知、实时、锁、事件总线等 | [组件总览](./components/README.md) |
| DDD 四层基座：Domain、Application.Contracts、Application、Infrastructure | [DDD 四层基座](./ddd-struct/ddd-struct.md) |

组件文档按能力家族组织。先从总览确认包与能力边界，再查看安装、注册、配置和用法；精确签名以已安装包内的 XML 文档和程序集为准。
