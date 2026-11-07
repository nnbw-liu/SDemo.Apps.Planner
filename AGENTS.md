# AGENTS · sdemo.planner 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

事序（sdemo.planner）：项目 → 大迭代 → 小迭代 → 工作项（任务/Bug/调研/杂务）；日常习惯与定时提醒；待办与日程同库不同表；AI 草稿审阅后入库；ICS 导出与只读订阅；桌面组件 ≤8 条待办。

## 现状（2026-11-06）

✅ **v4 已实现并实机验证**：在 v3 六状态/完成记录/托盘之上完成更名「事序」、搜索可清除、中国法定节假日联网同步与手动假期管理。
- 六件套：`Contracts` / `Storage` / `Core` / `Mcp` / `Web` / `Host`，`SDemo.Planner.slnf`。
- 真实 EF migrations（含 FTS5 虚表 + `WorkspaceV2` + `WorkspaceV3` + `HolidaysV4`）+ 软删回收站 + outbox/op-log + MCP 审计。
- Host 是 WPF 外壳 + `BlazorWebView`；业务 UI 全在 `SDemo.App.Planner.Web`（RCL）。主窗口关闭收进托盘，提醒继续。
- 页面：首页、项目列表/详情、任务、日历（任务 + 日程 + 节假日）、日常、草稿、回收站。
- 页面标题（v4.2）：各页不再有 `.page-head` 英雄区，标题/说明统一在 topbar（`MainLayout` 的 `PageTitle`/`PageDescription`）；页面动作走右对齐 `.page-toolbar` 或筛选条里的 `.push-right`。
- 窗口（v4.2）：自绘标题栏（`WindowStyle=None` + `WindowChrome`），40px、图标 +「事序 · 项目与日常」+ 最小化/最大化/关闭；双击标题栏最大化；`WM_GETMINMAXINFO` 把最大化钉在工作区。
- 项目详情（v4.2）：大迭代/小迭代都可折叠；任务按「小迭代自己的列表 + 大迭代直属任务」展示，不再聚合。
- 日历（v4.2）：周末格子标灰色「休」badge，法定假期优先显示休/班；周末底色与日号弱化。
- 日历（v4.3）：假期徽标放大并给有假期的格子加底色（补班橙色「班」）；格子右下角 hover「+」直接新建任务并预填当天截止；工具栏不再有「任务」按钮。
- 项目页（v4.3）：「新建项目」在 topbar、「新建任务」左侧；由 `MainLayout.IsProjectsPage` 路由判断渲染，不用 Sections。
- 任务详情（v4.3）：始终显示「任务说明」段，空时给占位提示。
- 主题（v4.3）：深色/浅色双主题，默认跟随系统；设置入口在侧栏底部与「退出应用」同排；选择存 localStorage `planner-theme`，WPF 标题栏经 `IThemeHost` 同步。
- 工作项状态：open/doing/done/verified/closed/canceled；终态可写完成说明 + 产物；任务可延期（1 天/1 周/1 月/指定时间）。
- 任务列表（v4.1）：左侧只读状态徽标，点行看详情；右侧状态下拉/编辑/延期；分组支持「按项目」「项目 + 迭代」，两级都可折叠。
- 迭代下拉显示「大迭代 · 小迭代」，放不下时退化为只显示小迭代（`SelectOption.CompactLabel`）。
- 迭代可完成/重开；完成大迭代级联小迭代。
- 日常提醒：固定时间 / 每隔 N 分钟；30 分钟宽限；`last_fired_at` 去重；右下角非激活 Toast。
- 日历节假日：`holiday` 表；`HolidaySyncService` 拉 timor.tech；manual 优先于 remote；月格显示休/班 badge 与名称。
- 搜索：默认全部状态；支持清空按钮/Esc/任务页「清除」；关键字命中标题、备注、完成说明。
- 13 个 `todo_*` MCP 工具；app 面 `/v1/health|handshake|shutdown|widgets/...`。
- 宿主联调、组件冒烟、ICS 导入 Outlook 沿用 v1；v2 月历暂不支持拖拽改期。
- 桌面组件仍是 v1 `WidgetWindow`；v2 预留 `Web/Widgets/PlannerWidget.razor` 作为对外 Blazor 组件。
- 验证记录：[docs/planner-v4.md](docs/planner-v4.md)、[docs/planner-v3.md](docs/planner-v3.md)、[docs/planner-v2.md](docs/planner-v2.md)、[docs/p2-verification.md](docs/p2-verification.md)；v1 设计：[docs/planner-design.md](docs/planner-design.md)。

未做：`project_*` / `iteration_*` / `routine_*` / `holiday_*` MCP 工具；`SetTodoStatus` / `PostponeTodo` / `CompleteIteration` 的 MCP 工具；v2 桌面组件；日程拖拽/重复规则；托盘开机自启。
P5：outbox 合并、ICS 只读订阅。

## 目录

```
src/SDemo.App.Planner.Contracts  DTO、输入、常量、错误码、JSON/时间口径
src/SDemo.App.Planner.Storage    EF 实体 + migrations + FTS5 + 事务写入
src/SDemo.App.Planner.Core       服务门面、校验、ICS、草稿拆分、相似度、项目/迭代/日常/提醒
src/SDemo.App.Planner.Mcp        JSON-RPC、工具目录、风险元数据、审计
src/SDemo.App.Planner.Web        Blazor RCL：Pages / Components / Layout / Widgets
src/SDemo.App.Planner.Host       WPF 外壳 + BlazorWebView + WidgetWindow + ReminderToastWindow + TrayIcon + AppServer
tests/SDemo.Planner.Tests        xunit（存储 / 服务 / MCP）
deploy/                          app.json + widget.json
tools/                           install-planner.ps1 / smoke-mcp.ps1 / smoke-widget.ps1 / smoke-calendar-drag.ps1 / accept-host.ps1
```

## 命令

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
dotnet build src\SDemo.App.Planner.Web -c Release   # 只查 Blazor 编译
pwsh tools\smoke-mcp.ps1          # headless + 真实 HTTP 调 13 个工具
pwsh tools\smoke-widget.ps1       # 组件 UIA：输入/添加/勾完成（临时库）
pwsh tools\smoke-calendar-drag.ps1 # v1 WPF 月历拖拽（v2 Blazor 未实现，仅存档）
pwsh tools\accept-host.ps1        # 真宿主：发现/ensure/MCP/组件/主窗口（需 Shell 已构建）
pwsh tools\install-planner.ps1    # 发布到 %APPDATA%\...\apps 并重算 sha256
```

本地调试：

```powershell
dotnet run --project src\SDemo.App.Planner.Host -- --db=%TEMP%\planner-dev.db
# headless（供脚本/MCP 调试）：
dotnet run --project src\SDemo.App.Planner.Host -- --headless --instance=dev --db=%TEMP%\planner-dev.db
```

## 必读

| 改什么 | 读 |
| --- | --- |
| **v4 更名/搜索/节假日** | [docs/planner-v4.md](docs/planner-v4.md) |
| **v3 状态/完成记录/延期/下拉/排序/托盘** | [docs/planner-v3.md](docs/planner-v3.md) |
| **v2 产品/数据/提醒/组件** | [docs/planner-v2.md](docs/planner-v2.md) |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| v1 数据模型 / 日历 / ICS / 决策 | [docs/planner-design.md](docs/planner-design.md) |
| 已跑过的门禁 | [docs/p2-verification.md](docs/p2-verification.md) |
| MCP 工具面与审批 | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 跨应用引用 / 统一搜索 / 审批矩阵 | [../../docs/integration.md](../../docs/integration.md) |
| 组件清单字段 | [../../docs/contracts/widget-contract.md](../../docs/contracts/widget-contract.md) |
| 宿主怎么拉起本应用 | [../../docs/contracts/host-protocol.md](../../docs/contracts/host-protocol.md) |

## 铁律

1. **零模型凭证**：不引入 `IChatClient` / `ApiKey`；AI 草稿由 DesktopAgent 生成。
2. **草稿不入库**：`todo_plan_from_text` 只落草稿区；`todo_accept_draft` 才写正式表。
3. **同库不同表**：`todo` / `event` / `project` / `iteration` / `routine` 各自实体与接口，不要做万能实体；工作项复用 `todo`。
4. **删除软删**：`deleted_at` + 7 天回收站；MCP 写操作落审计。
5. **不直连其它应用**：只存 `sdemo.kb/doc/...` 这类 URI 字符串。
6. **契约优先**：常量（上限、阈值、错误码）在 `Contracts`，改它等于改契约，
   同步改 `docs/` 与测试。

## 常见坑

- 所有时间存 UTC，显示转本地；输入必须带时区偏移（`IsoTime.TryParseUtc`）。
- Host 必须是 `Microsoft.NET.Sdk.Razor`；`wwwroot` 交给 Razor SDK 当静态资产，不要手动 `<Content Include>`。
- 不能在 `app.Run()` 前 `Show()` 主窗口；WebView2 会抛 `EnsureCoreWebView2Async cannot be used before the application's event loop has started running`。先设 `app.MainWindow`，再 `app.Run(main)`。
- Blazor 的 `<input type=date/time>` 不要直接 `e.Value?.ToString()`；用 `PlannerPageBase.DateValue/TimeValue`，否则会带秒/区域格式，后端报 `时间格式必须是 HH:mm`。
- 项目/迭代最多两层：小迭代必须挂同项目的大迭代；`TodoPatch` 换项目时清掉不匹配的迭代。
- 日常提醒固定时间有 30 分钟宽限，`last_fired_at` 当天去重；`CollectDueReminders()` 会写 `last_fired_at`，测试时注意。
- 关闭主窗口是 `Hide()` 到托盘（`allowClose=false`），不是退出，提醒 tick 继续；`ExitApp()` / 托盘「退出」才真退出并清理 `Shell_NotifyIcon`。
- 托盘用 `TrayIcon.cs` 的 Win32 `Shell_NotifyIcon` + `HwndSource`；不要给 Host 加 `<UseWindowsForms>`，会和 WPF 的 `Application`/`MessageBox`/`Brushes`/`Cursors`/`Color`/`KeyEventArgs` 产生 `CS0104`。
- 工作项终态是 `done/verified/closed/canceled`；项目/迭代完成统计只算 `done/verified/closed`，`canceled` 排除；终态写 `completed_at`，非终态清空。
- 完成说明/产物走 `SetTodoStatus`，不要只改 `TodoPatch.Status`；任务列表默认 `Status=all`，搜索时也重置 all。
- 自定义下拉统一用 `Components/SelectBox.razor`，别新增原生 `<select>`；多个下拉的互斥由 `PlannerUi.SelectOpened` 协调。收起靠 `planner.js` 的 `watchOutside`（document pointerdown/Escape/scroll），**不要再加透明 backdrop 或把 SelectBox 包进 `<label>`**，否则点击会转回触发器导致收不起来。
- 托盘菜单是 `XamlReader.Parse` 的自绘模板；三段 XAML 都要带 `xmlns:x`，否则启动时抛 `XamlParseException`「前缀 x 未映射」。
- 自绘标题栏：`WindowStyle=None` + `WindowChrome`，`CaptionHeight=0` 时窗口按钮才能收到点击；拖拽靠 `TitleBar_MouseLeftButtonDown` 里 `DragMove()`。无边框最大化会盖住任务栏，必须在 `WndProc` 处理 `WM_GETMINMAXINFO` 把尺寸钉在 `rcWork`。默认 1180×740，别调回 1280×840——150% 缩放下它等于工作区，最大化看不出变化。
- 任务行 `TaskRow` 自带状态下拉/编辑/延期，页面只需 `<TaskRow Todo="todo" />`；分组折叠键用 `"p:" + 项目Id` 与迭代 Id，别混用。
- 任务行不要加 `opacity`（`is-done` 曾经用 opacity: .5）——它会让内部下拉继承半透明，并生成 stacking context，fixed 菜单被后续行盖住。弱化用颜色/子元素透明度。
- 主题：颜色一律走 `--surface-*` / `--wash-rgb` / 语义 token，别新写 `rgba(255,255,255,α)` 或深色 hex，否则浅色主题下会瞎；半透明遮罩写 `rgba(var(--wash-rgb), α)`。主题存 localStorage `planner-theme`，`ThemeService` + `IThemeHost` 同步 WPF 标题栏。
- `SectionOutlet`/`SectionContent` 在 BlazorWebView 里不渲染（内容被静默丢弃），页面级 topbar 动作改用 `MainLayout` 路由判断。
- 日历格子是 `<button>`，hover「+」用 `<span class="cell-add">` 绝对定位，别嵌 button。
- 节假日：`HolidaySyncService` 只替换 `source=remote`，同日 `manual` 必须保留；手动假期离线可用，联网失败不能清空已有数据。
- 搜索：`MainLayout` 的输入框在回车后保留关键字；清除走 `ClearSearch()` 回 `/tasks`；`Tasks` 的搜索必须保持 `Status=all`。
- 迁移必须让 `dotnet ef` 重新 build，别加 `--no-build`，否则会生成空迁移。
- 每次写都要 `revision + 1`（含删除/恢复）；MCP 写工具必须带 `expectedRevision`。
- FTS 是迁移 SQL 建的虚表，`todo_fts` 的 rowid 存在 `todo.fts_row_id`；
  Entity 里没有 `todo_fts` 的 DbSet，别用 `EnsureCreated`。
- 相似重复阈值 0.75；完全重复只拒绝 AI/meeting 来源，manual 不拦。
- `outbox` + `op_log` 与实体写在同一事务，别事后补。
- 宿主拉起时不要自动弹主窗口；只显示组件，主窗口从组件按需打开。
- 安装目录是 `%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.planner`（不是 `%LOCALAPPDATA%`），
  宿主只扫 APPDATA；`runtime` 与日志仍在 `%LOCALAPPDATA%`。
- 组件指令路径是 `/v1/widgets/{id}/command`（4 段）；改路由后跑一遍 `accept-host.ps1`。

## 不要读

`obj/`、`bin/`、`TestResults/`；`docs/requirements.md` 只通读一次。
