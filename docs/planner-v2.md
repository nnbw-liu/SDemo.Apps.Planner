# Planner v2 · 项目与日常

> 状态：已落地（2026-11-06）。v1 的「待办 + 日历」保留为底层能力，v2 把产品中心改为「项目推进 + 日常节奏」。
> v3 增量（六状态、完成记录/产物、延期、迭代完成、自定义下拉、排序、图标、托盘）：[planner-v3.md](planner-v3.md)。
> 关联：[需求](requirements.md) · [v1 设计](planner-design.md) · [MCP 工具面](planner-mcp-surface.md) · [P2 验收](p2-verification.md)

## 1. 产品模型

一句话：**用项目承接目标，用迭代拆节奏，用任务推进，用日常守住生活。**

```
项目 Project
├─ 大迭代 Iteration (level=big)     版本 / 里程碑
│  └─ 小迭代 Iteration (level=small) Sprint / 一段可交付的节奏
│     └─ 工作项 WorkItem            task | bug | research | chore
└─ 未归入迭代的 Backlog 工作项
```

- 项目：名称、说明、颜色、状态、起止/目标日期；进度按任务完成率计算。
- 迭代：自引用 `parent_id`；大迭代不挂父级，小迭代必须挂同项目的大迭代；最多两层。
- 工作项仍是 `todo` 表：可完成、可设截止时间、可挂项目/迭代，支持 `kind` 与一层子任务。
- 日程 `event` 保持独立表：有起止、地点、参与人，供日历使用。
- 日常 `routine` 与打卡 `routine_check` 独立于项目：喝水、吃药、起身走动等重复事项。

## 2. 架构

```
WPF 外壳 (SDemo.App.Planner.Host)
├─ BlazorWebView  ──  SDemo.App.Planner.Web (RCL)
│                     Pages / Components / Layout / Widgets
├─ AppServer       ──  /v1/health|handshake|shutdown|widgets/...
├─ WidgetWindow    ──  v1 桌面组件，暂不重做
└─ ReminderToastWindow
Contracts / Storage / Core / Mcp 保持原分层
```

关键点：

- Host 使用 `Microsoft.NET.Sdk.Razor` + `Microsoft.AspNetCore.Components.WebView.Wpf` 10.0.110。
- `MainWindow` 只负责 `BlazorWebView`，设置 `Services` 与 `RootComponent<Routes>("#app")`；业务视图全在 Web 项目。
- 静态资源在 `src/SDemo.App.Planner.Host/wwwroot`：`index.html`、`css/app.css`、`_framework/blazor.webview.js`。
- Program 用 `ServiceCollection` + `AddWpfBlazorWebView()` 注册 `PlannerService` / `PlannerUi`，并且必须等 WPF 事件循环启动后再显示主窗口，避免 WebView2 初始化时序异常。
- 关闭主窗口改为最小化，进程继续跑提醒；宿主模式仍由宿主决定窗口生命周期。

## 3. 数据模型（v2 增量）

迁移：`Storage/Migrations/20261106131438_WorkspaceV2.cs`。

### `project`

`id, name, description, color, status, start_at, target_at, sort, revision, created_at, updated_at, deleted_at`

### `iteration`

`id, project_id, parent_id, level(big|small), name, goal, status, start_at, end_at, sort, revision, created_at, updated_at, deleted_at`

### `routine` / `routine_check`

- `routine`：`id, title, emoji, notes, days_mask, time_of_day, interval_minutes, active_end, target_count, remind, enabled, sort, revision, last_fired_at, created_at, updated_at, deleted_at`
- `routine_check`：`routine_id + local_date` 复合主键，`count, last_completed_at`；按本地日期记打卡，跨时区不会串日。
- `days_mask`：bit0=周一 … bit6=周日；`127` 每天，`0b0011111` 工作日。

### `todo` 增量

`project_id, iteration_id, kind, parent_todo_id, sort`。`kind` 默认 `task`，旧数据兼容。

### 不变量

- 迭代最多两层；小迭代必须挂在同项目的大迭代下。
- 任务可没有项目；有迭代时自动带出所属项目；换项目时清掉不匹配的迭代。
- 项目/迭代删除是软删；项目删除时任务保留，只清空归属。
- 所有写操作 `revision + 1`，与 `outbox` / `op_log` 同事务。

## 4. 首页与页面

- `/` 首页：问候与日期、今日日常打卡、今日焦点、正在进行的迭代（大迭代卡内嵌小迭代进度）、接下来 7 天、项目卡。
- `/projects`：项目卡片、状态筛选。
- `/projects/{id}`：项目头、整体进度、迭代路线（大迭代 → 小迭代 → 任务）、Backlog、添加任务/迭代。
- `/tasks`：状态/类型/项目筛选，按项目分组，支持搜索词。
- `/calendar`：月历 + 选中日 agenda；任务可完成/编辑，日程可新建/编辑/删除。
- `/routines`：本周节奏、今天要做、全部日常管理（提醒开关、编辑、删除）。
- `/drafts`：AI/会议草稿审阅，采纳才入库。
- `/trash`：任务/日程回收站，7 天内可恢复。

视觉：深色 Linear/Notion 风，240px 侧栏，卡片化内容；屏幕小于工作区时主窗口自动收敛。

## 5. 日常提醒

- **固定时间**：`time_of_day` 到点触发；当天已触发过则跳过。
- **间隔提醒**：`time_of_day` ~ `active_end` 之间每 `interval_minutes` 触发一次；首次从开始时间起算。
- 只对当天 `days_mask` 命中的、`enabled` 且 `remind` 的日常生效；完成 `target_count` 后不再提醒。
- 固定提醒有 **30 分钟宽限**：晚于 30 分钟不补发，避免晚上弹出早上的吃药提醒。
- `last_fired_at` 持久去重；`PlannerService.CollectDueReminders()` 可注入时间做单测。
- Host 每 30 秒 tick，用右下角非激活 `ReminderToastWindow` 展示，12 秒自动关闭。
- 关闭主窗口只是最小化，提醒继续；侧栏「退出应用」需点击两次确认，才会真正结束进程。

## 6. 对外 Blazor 小组件

> **2026-11-07：本节已作废。** `Widgets/PlannerWidget.razor` 与 `.planner-widget*` 样式已随桌面组件一并删除。

`src/SDemo.App.Planner.Web/Widgets/PlannerWidget.razor` 是可复用的纯展示组件：

- 参数：`Title`、`Todos`、`RoutineDone`、`RoutineTotal`、`Compact`、`MaxItems=8`、`OnToggle`、`OnOpen`。
- 不注入 `PlannerService`，不直接读写数据库；宿主/外层负责取数与回调。
- 样式内聚在 `app.css` 的 `.planner-widget*` / `.widget-*`，可被未来桌面组件或外部 Blazor 页面复用。

## 7. 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误。
- `dotnet test tests/SDemo.Planner.Tests`：41 通过。
- 真实运行验证：项目 → 大迭代 → 小迭代 → 任务 → 完成，进度/统计联动；日常创建、打卡、周条；固定提醒到点弹出 Toast；任务删除 → 回收站 → 恢复；日程新建并在月历显示；窗口关闭后进程保留、提醒继续。
- 运行参数：`SDemo.Planner.exe --db=%TEMP%\planner-v2-dev.db --instance=v2dev`。

## 8. 未做 / 后续

- MCP 工具面目前仍是 `todo_*` 13 个；尚未暴露 `project_*` / `iteration_*` / `routine_*`，也尚未把 `projectId` / `iterationId` / `kind` 加进 `todo_*` schema。
- 桌面组件仍是 v1 的 `WidgetWindow`；v2 只预留了 `PlannerWidget` 组件边界。
- 日程暂不支持拖拽改期、跨天条、重复规则编辑。
- 暂无托盘图标；关闭主窗口为最小化。
