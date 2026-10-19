# 前端列表与表格规范

列表页与数据表格的规范，同时遵循 [前端界面规范](./frontend-ui.md)。

## 1. 状态与数据

- 列表的分页、排序、筛选以 URL query params 为唯一来源（`queryParamMap` 派生 + `router.navigate({ queryParams })` 回写），非法参数回退默认，不存 localStorage。
- 加载、失败与空状态按[设计原则](./frontend-ui.md#1-设计原则)区分；服务端分页参数与排序表达式见 [API 规范 §5](./api.md#5-分页规范)。

## 2. 数据表格

- 表格用 TanStack Table（`@tanstack/angular-table`，服务端 `manualPagination`/`manualSorting`/`rowCount`）；分页与筛选复用 `frontend/src/app/shared/components/table-paginator`、`faceted-filter`。
- 列按优先级（`primary`/`secondary`/`tertiary`）裁剪：视口档位（`tableViewportSignal()`）给上限，带吸附操作列的表格外框挂 `[appTableFit]="tableViewport()" [appTableFitContent]="rows()"`，放不下再降一档（更新数据须换新数组）。被裁的列由 TanStack 行展开补偿（按 `getRowId` 的实体 id 记），不另建展开状态。主列用 `TITLE_COLUMN_META` 与 `TITLE_CONTENT_CLASS`：最窄档截断长名称。
- 展开详情按列定义生成：数据列登记 `detail: true`，被折叠的列按定义顺序、用表头同一份标签列出，值给全文与完整时刻；不对应列的补偿接在后面，不重复主行。
- 排序表头：能排序的列（`column.getCanSort()`）在表头格里直接放排序按钮，能否排序由按钮表达。`[attr.aria-sort]` 取 `tableSortAria` 的值：只有当前排序的列带 `ascending` 或 `descending`，未排序与不可排序的列不带这个属性（WAI-ARIA 建议一张表同一时刻只给一个表头）。表格 spec 调用 `expectAriaSortOnSortedHeaderOnly`，覆盖无排序、排序与换列三种状态。
- 表头统一灰底（`thead` 与吸附操作列的表头格都用 `bg-muted`），正文与吸附操作列的正文格跟随行表面与悬停态。单元格密度用 Spartan 表格默认（表头 `px-2`、单元格 `p-2`），只给首列加 `first:ps-4` 留出与外框的间距，不在页面上另写横向内边距。时间列用 `appDate` 的 `short` 槽位；审计类表格（操作记录）按秒对时，用 `full`。
- 操作列按"常用优先、破坏性置后"排序：≤3 项桌面端平铺（icon 按钮 + tooltip），>3 项显示 2 个高频操作 + `…` 溢出菜单；`<sm` 一律收进 `…`，破坏性操作用 `variant="destructive"` 并以分隔线隔开。
- 只读溢出列表用 `frontend/src/app/shared/components/readonly-list-popover`，整个摘要是入口、数量不截断；只读浮层要可聚焦、按视口限高。面板求紧凑：标题区一行（标题 · 对象名在左、数量在右），每个条目一行（圆点与名称在左、补充说明在右，两列至少隔 24px，统一行高后垂直居中），放不下时单行省略、名称优先占宽，被截断的条目悬停提示全文。这是本组件的例外：条目不可聚焦、提示不响应触摸，键盘与纯触屏用户看不到被截掉的部分，筛选框只能缩小范围；其他场景截断后仍须给可访问的全文入口。
- **禁用原因跟着被禁用的控件走，不进数据单元格**：本人行、内置超管行等不可操作的原因，行内按钮置灰并把 Tooltip 挂在外层（禁用按钮收不到悬停），溢出菜单里在该项下方用一行 `text-xs text-muted-foreground` 说明，两处都以 `aria-describedby` 关联。单元格里只放标记（如姓名后的「你」徽章，`shrink-0`、姓名截断），身份列保持两行。
