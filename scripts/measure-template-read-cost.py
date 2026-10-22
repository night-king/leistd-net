#!/usr/bin/env python3
"""统计生成项目典型任务读取的规范与 Skill 篇幅，检查不超过登记的上限。

口径：模板源码（含条件标记、全部功能开启）；每类任务按 docs/README.md 的按任务读取表与
项目 Skill 的意图路由列出应读文件，连同这些文件要求先读的伴随文件（如 Spartan Skill 的
rules）与按任务内容必读的专题，去重后累加字符数（Python len）。由 check-all 以 --check 运行，
任一任务超过登记的上限即失败；超限时先去重、删冗长示例，再按独立任务主题拆分。
"""
from __future__ import annotations

import argparse
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent.parent
SKILL = 'template/.agents/skills/leistd-project-workflow/'
STANDARDS = 'template/docs/standards/'
SPARTAN = 'template/.agents/skills/spartan/SKILL.md'
SPARTAN_RULES = 'template/.agents/skills/spartan/rules/'
COMMON = [SKILL + 'SKILL.md', 'template/docs/README.md']
IMPLEMENT = COMMON + [SKILL + 'references/development.md', SKILL + 'references/quality.md']


def standards(*names: str) -> list[str]:
    return [STANDARDS + name for name in names]


def spartan_rules(*names: str) -> list[str]:
    return [SPARTAN_RULES + name for name in names]


# 文件 → 读它时必须同读的伴随文件。Spartan Skill“Critical rules”要求做相关工作前先读对应
# rule：任何界面工作都涉及样式与组件组合；表单规则（forms.md）只在改表单时必读，按任务登记。
REQUIRED_COMPANIONS: dict[str, list[str]] = {
    SPARTAN: spartan_rules('styling.md', 'composition.md'),
}

# 任务 → 按任务内容必读、不随某个文件自动带出的规则与专题：样例请求涉及表单，就要读 Spartan 的
# forms.md 与表单专题；新增列表页要读列表与表格、导航专题（新页面要落菜单分组与页面标题）
TASK_REQUIRED: dict[str, list[str]] = {
    'ui-change': spartan_rules('forms.md') + standards('frontend-forms.md'),
    'fullstack-crud': standards('frontend-lists.md', 'frontend-navigation.md'),
}


# 任务 → (样例请求, 应读文件)
TASKS: dict[str, tuple[str, list[str]]] = {
    'backend-crud': ('新增一个带分页查询的资源（后端）',
                     IMPLEMENT + standards('coding-common.md', 'coding-backend.md', 'api.md', 'testing.md')),
    'fullstack-crud': ('新增上述资源并加列表页',
                       IMPLEMENT + standards('coding-common.md', 'coding-backend.md', 'api.md', 'testing.md',
                                             'coding-frontend.md', 'frontend-ui.md', 'frontend-lists.md',
                                             'frontend-navigation.md')
                       + [SPARTAN] + spartan_rules('styling.md', 'composition.md')),
    'ui-change': ('调整现有页面布局与表单',
                  IMPLEMENT + standards('coding-common.md', 'coding-frontend.md', 'frontend-ui.md', 'frontend-forms.md',
                                        'testing.md')
                  + [SPARTAN] + spartan_rules('styling.md', 'composition.md', 'forms.md')),
    'new-text': ('新增界面文案',
                 COMMON + [SKILL + 'references/development.md']
                 + standards('coding-common.md', 'coding-frontend.md', 'frontend-i18n.md')),
    'review-only': ('只审查一个全栈改动',
                    COMMON + [SKILL + 'references/quality.md']
                    + standards('coding-common.md', 'coding-backend.md', 'coding-frontend.md', 'api.md', 'testing.md')),
}

# 基线提交 e984db19（规范分层之前）的读数，仅供对比。按当时的读取集合计：当时没有
# frontend-ui.md 与 frontend-i18n.md，界面规范是 ui-design.md；Spartan rules 与当前集合相同。
BASELINE = {'backend-crud': 53365, 'fullstack-crud': 97875, 'ui-change': 63719,
            'new-text': 33266, 'review-only': 78841}

# 当前上限：调整后不得回升。只有新增必要规则且已删去重复后才按读数放宽，理由写在提交说明里，
# 历次放宽由 Git 记录，不在此累积。
LIMITS = {'backend-crud': 36996, 'fullstack-crud': 70530, 'ui-change': 52822,
          'new-text': 19200, 'review-only': 41127}


def missing_companions(tasks: dict[str, tuple[str, list[str]]]) -> list[str]:
    errors = []
    for task, (_, files) in tasks.items():
        for path, companions in REQUIRED_COMPANIONS.items():
            if path in files:
                errors += [f'{task}：含 {path} 但缺伴随文件 {c}' for c in companions if c not in files]
    for required_task, required in TASK_REQUIRED.items():
        if required_task in tasks:
            files = tasks[required_task][1]
            errors += [f'{required_task}：缺按任务必读的 {r}' for r in required if r not in files]
    return errors


def self_test() -> list[str]:
    # 反例：从含 Spartan Skill 的任务里删掉一个必读伴随文件，自检必须报出
    task, (sample, files) = next((t, v) for t, v in TASKS.items() if SPARTAN in v[1])
    removed = REQUIRED_COMPANIONS[SPARTAN][0]
    broken = {task: (sample, [f for f in files if f != removed])}
    failures = [] if any(removed in e for e in missing_companions(broken)) else [f'自检反例未报出：{task} 删掉 {removed}']
    # 反例：从任务里删掉按任务必读的规则，自检必须报出
    for required_task, required in TASK_REQUIRED.items():
        sample, files = TASKS[required_task]
        for path in required:
            broken = {required_task: (sample, [f for f in files if f != path])}
            if not any(path in e for e in missing_companions(broken)):
                failures.append(f'自检反例未报出：{required_task} 删掉 {path}')
    return failures


def measure(task: str) -> tuple[int, list[tuple[str, int]]]:
    files = list(dict.fromkeys(TASKS[task][1]))
    sizes = [(path, len((ROOT / path).read_text(encoding='utf-8'))) for path in files]
    return sum(size for _, size in sizes), sizes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--check', action='store_true', help='任一任务超过上限时以非 0 退出')
    parser.add_argument('--files', action='store_true', help='列出每类任务的去重文件与字符数')
    args = parser.parse_args()

    errors = missing_companions(TASKS) + self_test()
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1

    over = []
    for task, (sample, _) in TASKS.items():
        total, sizes = measure(task)
        change = (total - BASELINE[task]) * 100 / BASELINE[task]
        status = 'OK' if total <= LIMITS[task] else 'OVER'
        print(f'{task:15} {total:7} 上限 {LIMITS[task]:7} 基线 {BASELINE[task]:7} ({change:+.1f}%) {status}  # {sample}')
        if args.files:
            for path, size in sizes:
                print(f'    {size:7}  {path}')
        if total > LIMITS[task]:
            over.append(task)

    if args.check and over:
        print(f'超过上限：{", ".join(over)}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
