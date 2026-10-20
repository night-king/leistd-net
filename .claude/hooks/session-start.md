本仓库包含框架、项目模板和可分发 Skill 三个交付面。仓库内部 Skill 的权威源位于 `.agents/skills/`；Claude Code 未发现这些 Skill 时，先按根 `README.md` 的“维护本仓库”说明创建本地适配。再按用户最终意图选择拥有交付责任的专项 Skill：Framework 使用 `developing-leistd-framework`，Template 使用 `developing-leistd-template`。

只有变更跨 Framework、Template、Skills、CI、版本或发布流程时才使用 `maintaining-leistd-repository` 统筹，并按范围组合专项 Skill。`template/.agents/skills/leistd-project-workflow` 只服务生成后的业务项目，不作为本仓库维护流程。

规格和计划写在 `docs/specs/YYYY-MM-DD-<topic>.md`，计划写在同一文件的 §3，不另建文件；规则见 `docs/README.md` 的“规格文档”，何时提交服从用户指示。
