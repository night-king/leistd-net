# 前端时间、数字、图表与推送规范

日期、时区、数字的展示，仪表盘图表与实时刷新，同时遵循 [前端开发规范](./coding-frontend.md) 与 [前端界面规范](./frontend-ui.md)。

## 1. 日期、时区与数字

时间以 UTC 传输与存储，只在展示时按会话时区换算。时区取自 `Display.TimeZone`（IANA 名，写入端已校验），按个人 → 组织 → 部署基线取值，都没有时用浏览器时区；书写方式取自界面语言（同一条链，最后是设备偏好）。两者由 `SettingContextService` 提供。

- 实际生效的展示时区只有一处判定：`shared/utils/zoned-time` 的 `resolveDisplayTimeZone`（设置值能被当前 `Intl` 使用就用它，否则浏览器时区），`appDate` 与按时区请求数据的地方共用。请求按日历区间统计时带这个值（见[批量与统计接口 §2](./api-batch-statistics.md#2-按日历区间统计)），坐标轴与提示按响应回显的时区格式化。
- 日历日用 `YYYY-MM-DD` 字符串表示；日期选择器给出的浏览器本地 `Date` 按年月日取，不经 `toISOString()`（那是 UTC，会折偏一天）。
- 业务日期用 `appDate` 管道，不用 Angular 的 `date`：后者传 IANA 名会静默回落到浏览器时区，也表达不了夏令时。

  ```html
  {{ row.creationTime | appDate: 'full' : displayTimeZone() : displayLocale() }}
  ```

- 槽位（`full`、`short`、`date`、`time`、`monthDayTime`、`monthDay`）由调用点按用途定；locale 取当前界面语言，不由时区推导。精度与格式串不做成设置项，语种写法差异加在管道的 `WRITING_OVERRIDES`。图表坐标轴、区间按钮等非模板调用方用同一个 `formatAppDate`，不另写日期格式。
- 时刻落在展示时区的哪一天用 `zonedCalendarDate`，日历日加减用 `addCalendarDays`；"刚刚 / N 分钟前 / 昨天 / N 天前"这类相对时间用 `shared/utils/relative-time` 的 `formatRelativeTime`（需要词条的种类交给模板翻译）。
- 业务数字用 `appNumber` 管道（非模板调用方用同一个 `formatAppNumber`）：`integer` 带千分位，`compact` 写大数（完整整数放进提示），`change` 写变化率，输入是比例（0.29 → `+29%`），乘 100 只在格式化处发生。locale 同样取界面语言；不在组件里直接 new `Intl.NumberFormat`。词条里的数字先格式化再作为参数传入。
- 时区候选项按"能否真的渲染"判定，不用 `Intl.supportedValuesOf('timeZone')` 过滤（它只列规范名，`UTC`、`Asia/Kolkata` 等可用值不在其中）。
- **相对时间**（「刚刚」「N 分钟前」「N 小时前」「昨天 HH:mm」「N 天前」）只用在扫一眼即可的近期列表，按展示时区的日历日分档，一周以前只写日期（今年省略年份）；完整时刻放进 Tooltip，元素可键盘聚焦。审计、详情等需要对时的位置写绝对时间。

## 2. 仪表盘与图表

- **托盘卡**（`frontend/src/app/shared/components/stat-panel`）：灰底外框放标题与右侧操作，内层白卡放内容，用于仪表盘这类同屏多块数据；内卡圆角按嵌套规则取 `rounded-lg`（外框 `rounded-xl` 减去 4px 间距）。普通页面区块仍用 Spartan Card，不套托盘。
- **图表**用 Spartan `chart`（TanStack Charts），序列颜色只取 `--chart-1…5` 令牌、主题用 `HLM_CHART_THEME`，不写色值；`chart` 只在用到它的懒加载页面里引入。主序列用 `--chart-1`，需要警惕的序列（失败、错误）用琥珀色 `--chart-3`；按时段的离散数据画直线、不做平滑；类目横轴（每个时段一个点）的刻度等间隔，用 `shared/utils/category-ticks` 的 `evenCategoryTicks` 按图表实际宽度（响应式 `defineChart({ chart: ({ width }) => … })`）从常用步长里取、从最近一个时段往前数，不只靠 `thin` 碰撞去重；主序列加自上而下淡出的渐变面积；图例是 2px 圆角方块；提示框写该时段的真实起止，逐行列出各序列的值并带色块；坐标轴与提示里的日期、钟点用 `formatAppDate`、数字用 `formatAppNumber` 的槽位，与其他页面同一写法（见 §1）。当前版本的网格没有虚线配置，保持实线。
- **指标卡**放在托盘卡里：外框标题右侧是口径说明（键盘可达的按钮 + Tooltip，写清这项怎么算），内卡是数值、迷你趋势（同一 `chart`，`guides: false` 去掉坐标轴与网格）和环比徽章；没有按桶序列的指标不画迷你趋势。环比徽章后的口径说明写短说法、单行截断（「较前 7 天」「较昨天」），完整口径（「同一进度」「同一时段」）放进可聚焦元素的 Tooltip。
<!--#if (IncludeRealTime)-->

## 3. 资源订阅：推送只表示"该重新查询了"

业务推送不持久化，断线期间、以及首次查询完成到加入订阅之间的变更都不会补发。把推送当作刷新提示的页面按同一写法接入（角色列表是示例）：

- 先监听 `SignalRService.resourceSubscribed$`，按自己的资源键过滤，订阅被确认（首次、自动重连后、重建连接后）时重新查询一次；再用 `watchResource(key, destroyRef?)` 登记订阅，并调用一次 `connect()`。订阅随页面销毁自动撤销，与连接何时建立无关；不要写成 `connect().then(() => 订阅)`，页面在连上之前离开时连上后照样会订阅。多个页面持有同一个键时，最后一个离开才真正退订。
- 收到业务事件时同样走既有的查询入口，不从推送内容里取数据：列表数据仍经受权限保护的查询取得，资源订阅本身也经过作用域和权限校验。
- 不用 `isConnected` 代替订阅确认：连接恢复时订阅还没重新建立，那时查询仍会漏掉之后的变更。

聊天、追加流这类按推送内容增量更新的功能，恢复方式由功能自己决定，不套用这条。
<!--#endif-->
