# 事序 v4 · 命名、搜索与法定节假日

> 2026-11-06。v4 在 [v3](planner-v3.md) 的项目/迭代/工作项/日常之上，
> 完成品牌更名、搜索可撤销、日历法定节假日同步与手动假期管理。

## 1. 更名

产品显示名：**事序**，副标题保留「项目与日常」。S 图标继续使用，S 可读作 **Shixu**。

- 安装清单 `deploy/app.json`：`name = 事序`，`version = 0.4.0`。
- 窗口标题：`事序 · 项目与日常`。
- 侧栏品牌、浏览器标题、托盘提示、托盘菜单、页面 `PageTitle` 同步。
- 可执行文件名、AppId、单实例互斥名、数据库路径保持不变，避免破坏宿主发现与已有数据。

## 2. 搜索可撤销

- 顶部搜索框回车跳转 `/tasks?q=...`，仍保持“输入后回车”的语义。
- 输入框有内容、或当前 URL 带 `q=` 时，右侧显示清除按钮。
- 任务页搜索提示旁显示「搜索：xxx × 清除」。
- 清除后导航回 `/tasks`，保留当前排序/项目以外的筛选语义；搜索期间任务页自动切到「全部状态」。
- 键盘 `Esc` 也会清空搜索并退出搜索结果。
- 关键字匹配标题、备注、完成说明，仍复用 SQLite FTS/Contains 查询。

## 3. 法定节假日

### 数据

`holiday` 表（migration `HolidaysV4`）：

| 列 | 说明 |
| --- | --- |
| `date` | 本地日历日 `yyyy-MM-dd`，主键 |
| `name` | 假期/补班名称 |
| `is_workday` | `true` = 调休上班（班），`false` = 放假（休） |
| `source` | `manual` / `remote` |
| `updated_at` | 写入时间 UTC |

`HolidayDto` / `HolidayInput` 在 `Contracts/Workspace.cs`。

### 联网

- `HolidaySyncService` 使用 `HttpClient` 请求 `https://timor.tech/api/holiday/year/{year}`。
- 解析 `holiday` 对象：`holiday=true` 为休，`false` 为补班；`name` / `date` 直接使用。
- 同步是“按年替换 remote 记录”：先删除该年 `source=remote` 的行，再插入接口结果。
- **manual 记录优先**：同一天的 manual 记录不会被覆盖；同步也不会删除 manual 记录。
- 网络失败只提示错误，不破坏已有数据；手动假期始终可离线维护。
- 同步入口在日历工具栏「假期」弹窗里，按当前查看年份同步。

### 界面

- 月历日期格显示「休/班」badge 和假期名。
- 选中日 agenda 头部显示 `休 · 春节` / `班 · 春节后补班`。
- 假期弹窗支持：按年查看、联网同步、手动新增（日期/名称/休或班）、删除。
- 手动添加日期会覆盖同日 remote 记录；再次同步仍保留 manual。

## 4. 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误。
- `dotnet test ...`：49/49 通过；新增假期保存/删除、remote 替换保留 manual、接口 JSON 解析测试。
- 实机：2026 年同步返回 39 天；手动添加「公司假」后再次同步仍为 manual；月历 badge/名称/选中日 chip 正常。
- 搜索：输入「调研」→ 搜索提示与清除按钮出现 → 点击清除回到 `/tasks` 全部结果。
- `pwsh tools\smoke-mcp.ps1`、`pwsh tools\smoke-widget.ps1` 均通过。
- 安装 `0.4.0` 到 `%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.planner`；安装版启动标题为「事序 · 项目与日常」，静态资源正常。

截图：[月历假期](images/v4-calendar.png) · [假期管理](images/v4-holidays.png) · [搜索清除](images/v4-search.png)。

> 2026-11-07 补记：桌面组件已移除（`WidgetWindow`、`PlannerWidget.razor`、`widget.json`、`tools\smoke-widget.ps1` 与 widgets 路由均删除）。上方 `smoke-widget.ps1` 结果为当时记录。

## 5. 边界

- 节假日接口是第三方公开服务，不保证长期可用；失败时使用本地 manual 数据。
- 目前按年手动触发同步，不在启动时自动拉取，避免离线启动变慢。
- 不包含农历计算、调休规则推导；以接口/手动录入为准。
- MCP 尚未暴露 `holiday_*` 工具。

## 6. v4.1 交互细节（2026-11-07）

### 迭代显示

- 编辑任务的迭代下拉按「大迭代 → 其小迭代」排序，显示 `大迭代名` / `大迭代名 · 小迭代名`。
- `SelectOption` 新增可选 `CompactLabel`：触发器宽度放不下完整文案时（`scrollWidth > clientWidth`），自动退化为只显示小迭代名，仍用省略号截断超长名称。
- 选中态与选项列表共用同一文案规则；切换 `Value` 会重新测量。

### 下拉收起

- 删除 `.selectbox-backdrop` 透明层（它嵌在 `<label>` 里会把点击转回触发器，导致收不起来）。
- `planner.js` 改为 `watchOutside(element, ref)` / `unwatchOutside(element)`：`document` 的 `pointerdown` 捕获、`Escape`、`resize`/`scroll` 都会关闭当前下拉；多个下拉仍由 `PlannerUi.SelectOpened` 互斥。
- `SelectBox` 实现 `IAsyncDisposable`，新增 `Class` 参数供紧凑场景使用；任务编辑器里的项目/迭代字段不再用 `<label>` 包裹。

### 任务列表

- 左侧改为只读状态徽标 `.task-state`：圆点 + 文案，`open/doing/done/verified/closed/canceled` 六态各自配色，不再可点击。
- 点击任务行 = 打开只读「任务详情」（项目/迭代/截止/创建/更新/完成时间、备注、完成记录、产物），详情 Footer 提供「标记完成」（仍走 `CompleteTaskDialog` 填写完成说明/产物）与「编辑任务」。
- 右侧动作区：状态下拉（直接 `SetTodoStatus`，不再强制完成说明）、编辑、延期。
- 延期弹窗：`延期 1 天 / 1 周 / 1 个月 / 指定时间`；前两者走 `PostponeTodo`，1 个月按日历月顺延、指定时间走 `TodoPatch.DueAt`。
- 首页、日历、项目详情复用同一 `TaskRow`，行为一致。

### 分组

- 任务页新增分段切换「按项目 / 项目 + 迭代」，默认仍是按项目。
- 「项目 + 迭代」为两级：项目 → 大迭代 → 小迭代（任务挂大迭代时直接列在大迭代下）；没有迭代的任务归入「未归入迭代」。
- 项目组与迭代组都可点击标题折叠/展开，chevron 旋转；折叠状态保存在页面 `HashSet`，切换分组或刷新后重建。
- 子组内的任务行不再重复显示项目与迭代名。

### 托盘菜单

- `TrayIcon.BuildMenu()` 用 `ControlTemplate` 自绘：深色圆角面板（`#171c27`、圆角 12、细边框、投影）、标题区「事序 / 项目 · 迭代 · 日常」、MDL2 图标菜单项、悬停高亮、细分隔线。
- 解决 Aero2 默认菜单模板在深色 UI 下突兀的问题；同时用 `TransformFromDevice` 把 `GetCursorPos` 的物理像素换算成 DIP，修掉高 DPI 下菜单位置偏移。
- 自定义 XAML 字符串必须带 `xmlns:x`，否则 `XamlReader.Parse` 抛「前缀 x 未映射」。

### 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误；`dotnet test`：49/49 通过。
- 真实宿主（WebView2 CDP）逐项验证：下拉外部点击/再次点击均可收起、迭代长名退化、行内状态切换、延期 1 周、详情弹窗、两种分组与折叠。
- 托盘菜单用同一套模板在临时 WPF 预览窗口中截图核对样式。


## 7. v4.2 界面收口（2026-11-07）

### 页面标题上移

- 各页删除 `.page-head` 英雄区，标题与说明统一进 topbar：`MainLayout` 按路由给 `PageTitle` + `PageDescription`（今天/项目/项目详情/任务/日历/日常/草稿/回收站）。
- 页面级动作不再重复：任务页的「新建任务」只留 topbar 一个；项目页的「新建项目」v4.3 起移到 topbar（见下一节）；日常页用右对齐 `.page-toolbar`；日历的月份导航/日程/假期进 `.page-toolbar`；回收站的 tab 分段在 `.page-toolbar`。
- `.topbar` 改为 `min-height: 88px` + 上下 12px padding，容纳 eyebrow/h1/描述三行。

### 项目详情：迭代折叠

- 大迭代、小迭代标题左侧都有 `.group-toggle` chevron，点击折叠/展开，`HashSet<string> _collapsed` 保留状态。
- 任务列表从「大迭代 + 所有小迭代聚合」拆开：每个小迭代行下挂自己的任务（`.subiteration-tasks`），大迭代底部是「大迭代直属任务」。
- 折叠只影响渲染，不重新查询；任务行在项目详情里不再重复项目名/迭代名。

### 日历：周末与假期

- 每个周六/周日格子右上角显示灰色「休」徽标（`holiday-badge weekend`），有法定假期时仍优先显示假期的「休/班」。
- 假期名继续显示在格子内容区；日历图例新增「休 休息日」。
- 周末格子底色略深，周末日号弱化；真实库 2026 年 39 条假期记录与手动假期都能正常标记。

### 自定义标题栏

- `MainWindow.xaml` 改为 `WindowStyle=None` + `WindowChrome`（`CaptionHeight=0`、`ResizeBorderThickness=6`、保留缩放/贴边），标题栏 40px：左侧应用图标 +「事序 · 项目与日常」，右侧最小化/最大化/关闭（Segoe MDL2，hover 深色、关闭红）。
- `MainWindow.xaml.cs`：`TitleBar_MouseLeftButtonDown` 双击最大化/拖拽；`StateChanged` 切换最大化/还原图标；`WM_GETMINMAXINFO` 把最大化尺寸钉在显示器工作区；`DwmSetWindowAttribute(33, 2)` 圆角。
- 窗口默认 1180×740（原 1280×840 在 150% 缩放下等于工作区，最大化看不出变化）；`FitToWorkArea()` 继续兜底小屏。

### 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误；`dotnet test`：49/49 通过。
- 真实宿主：标题栏拖拽位移 (−100, +106) 精确、最大化/还原 rect 在工作区与还原尺寸间切换、最小化正常；日历周末 12 个「休」徽标 + 手动假期「测试假期」正常；项目详情大/小迭代折叠后任务行数归零；任务/项目/日常/回收站页头不再重复。
## 8. v4.3 主题与细节（2026-11-07）

### 日历

- 假期徽标放大（20px 高、10.5px 粗体、带底色与细边），有假期的格子加淡绿/淡橙底；调休补班用橙色「班」，与灰色周末「休」区分。
- 删除工具栏里的「任务」按钮；每个格子右下角 hover 出现蓝色「+」，点击直接打开新建任务，截止日期预填为当天（时间留空，保存时按 18:00 处理）。
- 日历格子本身是 `<button>`，新增的「+」用 `<span class="cell-add">` 绝对定位，避免嵌套 button。

### 项目页

- 「新建项目」移到 topbar，位于「新建任务」左侧（`.btn-ghost`）。
- 实现方式：`MainLayout` 用路由判断 `IsProjectsPage` 渲染，而不是 `SectionOutlet`/`SectionContent`——后者在 BlazorWebView 里整段不渲染（内容被丢弃且无报错），已回退。

### 任务

- 任务详情固定显示「任务说明」段，没有备注时给占位提示；不再因为 Notes 为空而整段消失。
- 修复状态下拉「穿透」：`.task-row.is-done` 去掉 `opacity: .5`（它会让菜单继承半透明，并生成 stacking context 导致 fixed 菜单被后续行盖住），改为标题/元信息分别弱化；`.selectbox-menu` z-index 300 → 900，背景改为不透明 `--surface-hover`。

### 浅色主题与设置

- `app.css` 顶部引入表面 token（`--surface-*`、`--hero-a..d`、`--topbar-bg`、`--holiday-*`），白色半透明遮罩统一写成 `rgba(var(--wash-rgb), α)`；`[data-theme="light"]` 覆盖整套变量。
- `ThemeService`（Web 层）：`System/Light/Dark` 三态，localStorage key `planner-theme`；`plannerTheme.init/set/dispose` 负责 `data-theme`、`color-scheme` 与 `matchMedia` 监听；`index.html` head 内联脚本在样式前应用，避免刷新闪白。
- 设置入口在侧栏底部与「退出应用」同一行（齿轮 icon）；`SettingsDialog` 左竖排导航「通用设置」+ 右侧「外观」三选一，切换即时生效。
- WPF 标题栏通过 `IThemeHost`/`HostThemeBridge` 跟随 Web 主题：`MainWindow.ApplyTheme(bool)` 换 `DynamicResource` 画刷，并设置 `DWMWA_USE_IMMERSIVE_DARK_MODE`。

### 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误；`dotnet test`：49/49 通过。
- 实机（150% DPI，浅色/深色各一遍）：启动跟随系统、设置弹窗切换三态、localStorage 持久化、标题栏同步换色；项目页 topbar 出现「新建项目」；日历 10 月国庆 8 个「休」+ 10 月 10 日「班」、周末「休」；hover「+」预填日期；任务详情显示/占位说明；状态下拉为不透明面板且盖住后续行。
## 9. v4.4 导航与托盘收口（2026-11-07）

### 侧栏与顶栏

- 浅色主题下选中项原先用 `color:#fff`，落在浅蓝底上几乎不可读；`[data-theme="light"] .nav-item.active` 改为深靛蓝字 `#1e2f7a` + 细内描边 + 半粗，保留左侧 accent 竖条。
- 「新建日常」从日常页 toolbar 移到 topbar 常驻（ghost 按钮 + repeat 图标），任何页面都能直接新建；日常页删掉原来的 `.page-toolbar`。
- 顶栏「新建任务」改为 `OpenNewTask()`：当前路由是 `/projects/{id}` 时带 `projectId` 打开，任务编辑器里的项目预填为当前项目；其它页面仍为空项目。
- 侧栏底部「退出应用」与齿轮设置按钮合并为一行 `.sidebar-actions`，不再多占一行、也不再被窗口底部裁掉。

### 托盘菜单

- 菜单配色改为跟随应用主题：`TrayIcon.BuildMenu(bool dark)` 把 `TrayBg/Border/Text/Muted/Hover/Separator/DangerText` 写进 `menu.Resources`，模板用 `{DynamicResource …}` 取值；`HostThemeBridge.Apply` 同时调用 `TrayIcon.Current?.ApplyTheme(dark)`，`ShowMenu()` 里 `EnsureMenu()` 在主题变化后重建。
- 标题区（「事序 / 项目 · 迭代 · 日常」）从 `menu.Items` 移进 `ContextMenu` 的 ControlTemplate，`Items` 只保留「打开事序」「退出」；这样标题不再被 WPF 包成 MenuItem，不会出现 hover 高亮或可选中。
- 深色调色：`#171c27 / #2b3547 / #dfe6f2 / #5d687c / #252d3d / #242d3d`；浅色：`#ffffff / #dbe2ee / #1f2937 / #7a879c / #eef2f8 / #e6ebf3`。

### 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误；`dotnet test`：49/49 通过。
- 实机（150% DPI）：浅色选中项清晰；项目详情顶栏新建任务项目预填 `dynabookAssistant`；任意页面「新建日常」都能打开编辑器；任务栏溢出里右键托盘菜单，浅色/深色各截一次，标题无 hover 高亮、两个菜单项配色与主界面一致。
