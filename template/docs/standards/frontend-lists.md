# 前端列表与表格规范

列表页与数据表格的规范，同时遵循 [前端界面规范](./frontend-ui.md)。

## 1. 状态与数据

- 列表的分页、排序、筛选以 URL query params 为唯一来源（`queryParamMap` 派生 + `router.navigate({ queryParams })` 回写），非法参数回退默认，不存 localStorage。
- 加载、失败与空状态按[设计原则](./frontend-ui.md#1-设计原则)区分；服务端分页参数与排序表达式见 [API 规范 §5](./api.md#5-分页规范)。

## 2. 数据表格

- 页面滚动列（`flex-1 overflow-y-auto`）加 `*:shrink-0`，免得 `overflow-hidden` 子项被压扁。
- 表格用 TanStack Table（`@tanstack/angular-table`，服务端 `manualPagination`/`manualSorting`/`rowCount`）；分页与筛选复用 `frontend/src/app/shared/components/table-paginator`、`faceted-filter`。
- 列按优先级（`primary`/`secondary`/`tertiary`）裁剪：视口档位（`tableViewportSignal()`）给上限，带吸附操作列的表格外框挂 `[appTableFit]="tableViewport()" [appTableFitContent]="rows()"`，放不下再降一档（更新数据须换新数组）。被裁的列由 TanStack 行展开补偿（按 `getRowId` 的实体 id 记），不另建展开状态。主列用 `TITLE_COLUMN_META` 与 `TITLE_CONTENT_CLASS`：最窄档截断长名称。
- 展开详情按列定义生成：数据列登记 `detail: true`，被折叠的列按定义顺序、用表头同一份标签列出（标签键集中在 `columnLabelKeys`，列值只写一个 `ng-template[appColumnValue]`、按 `mode` 区分 `cell`/`detail`，详情列取 `hiddenDetailColumns(table)`），值给全文与完整时刻；不对应列的补偿接在后面，不重复主行。
- 详情取自行数据，展开即渲染，不设详情接口；一对多、要分页的子数据才按需请求，按行记状态、丢弃迟到响应。值带按钮时 `dl` 用 `items-baseline`，`dd` 为不折行的 `flex items-center gap-1`，值（`min-w-0 break-all`）在前、按钮（`shrink-0`）在后，空值写「-」。
- 排序表头：能排序的列（`column.getCanSort()`）在表头格里直接放排序按钮。`[attr.aria-sort]` 取 `tableSortAria`，只有当前排序的列带值；表格 spec 调用 `expectAriaSortOnSortedHeaderOnly`，覆盖无排序、排序与换列。
- 表头（含吸附操作列的表头格）统一 `bg-muted`，正文格跟随行表面与悬停态。单元格密度用 Spartan 默认（表头 `px-2`、单元格 `p-2`），只给首列加 `first:ps-4`，页面不另写横向内边距。时间列用 `appDate` 的 `short` 槽位，完整时刻（`full`）放进可聚焦元素的 Tooltip；审计类表格（操作记录）按秒对时，用 `full`。
- 操作列按"常用优先、破坏性置后"排序：≤3 项桌面端平铺（icon 按钮 + tooltip），>3 项显示 2 个高频操作 + `…` 溢出菜单；`<sm` 一律收进 `…`，破坏性操作用 `variant="destructive"` 并以分隔线隔开。
- 只读溢出列表用 `frontend/src/app/shared/components/readonly-list-popover`，整个摘要是入口、数量不截断；只读浮层要可聚焦、按视口限高；条目单行截断、不给键盘与触屏全文入口是它的例外（见组件注释），其他场景截断须给可访问的全文入口。
- **禁用原因跟着被禁用的控件走，不进数据单元格**：本人行、内置超管行等不可操作的原因，行内按钮置灰并把 Tooltip 挂在外层（禁用按钮收不到悬停），溢出菜单里在该项下方用一行 `text-xs text-muted-foreground` 说明，均以 `aria-describedby` 关联。单元格里只放标记（如姓名后的「你」徽章，`shrink-0`、姓名截断），身份列保持两行。
