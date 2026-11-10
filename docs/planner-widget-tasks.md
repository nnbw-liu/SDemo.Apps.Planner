# 任务桌面组件（`/widgets/tasks`）

> 2026-11-09。DesktopAssembly 的第二个组件，与 `/widgets/calendar` 并列。
> 入口：`SDemo.App.Planner.Web/Pages/WidgetTasks.razor` → `Components/TaskWidget.razor`。
> 样式：`Host/wwwroot/css/widget.css` 的「任务组件」段；所有颜色走主题 token。

## 登记

`HubServer` 在 `/v1/info` 与 `%LOCALAPPDATA%\SDemo\Hub\apps\sdemo.planner.json`
两处都声明 `widgets`，新增组件必须同步这两处，否则宿主发现不到：

```json
{
  "id": "tasks",
  "displayName": "任务",
  "description": "未完成任务、优先级 / 截止排序、项目迭代筛选",
  "entry": "/widgets/tasks",
  "size": { "width": 480, "height": 680 },
  "minSize": { "width": 360, "height": 420 },
  "defaultVisible": true,
  "config": {
    "type": "object",
    "properties": {
      "rows": {
        "type": "integer",
        "title": "显示条数",
        "description": "任务列表最多显示几行",
        "minimum": 1,
        "maximum": 50,
        "default": 10
      }
    }
  }
}
```

组件页和日历一样只接受 `<appId>.localhost` + host-only cookie。

## 列表与排序

- 只取 `Status = "active"`（`open` + `doing`），不显示终态。
- 默认 `TodoSort.Priority`：优先级数字升序（P1 最前）→ 有截止时间优先 → 截止时间升序 → 最近更新。
  也就是「越接近要求完成时间越靠前」，高优先级先排。
- 排序下拉：优先级 · 截止（默认）/ 截止时间 / 最近更新 / 创建时间。
- 行内展示：状态圆点、类型徽标（非普通任务）、标题、高优先级火焰、项目（仅「全部项目」时）、迭代名、截止文案。
- 截止文案：已逾期 / 逾期 N 天 / 今天 HH:mm / 明天 HH:mm / M月d日 / yyyy年M月d日；
  逾期行有淡红底和红字，今天为琥珀色。

## 筛选

- 项目：全部项目 / 具体项目；切换项目会清空迭代选择。
- 迭代：未选项目时禁用；选中项目后列出「全部迭代」、大迭代、小迭代（大 · 小）、孤儿小迭代、未归入迭代。
- 选「大迭代」时查询直属任务 + 所有子迭代任务，合并后统一排序；选具体小迭代只查该迭代。
- 「未归入迭代」在客户端收口（服务端查询不支持 null 迭代条件）。

## 行操作

- 左侧圆圈：完成任务 → 打开 `CompleteTaskDialog`，可记录完成说明/产物。
- 右侧常驻两个按钮：延期（`TaskPostponeDialog`）、查看详情（`TaskDetailDialog`）。
- 双击行：查看详情（`title="双击查看详情"`）。
- 右上角：新建任务（预填当前项目/迭代）、刷新。
- 写操作后 `PlannerService.Changed` 触发 `PlannerPageBase` 自动重载，完成的任务会立即离开列表。

## 详情里的状态修改

`TaskDetailDialog` 的「状态」下拉：

- 选 `open/doing`：直接 `SetTodoStatus`，保留原完成说明/产物，然后重新打开详情刷新 revision。
- 选 `done/verified/closed/canceled`：关闭详情并打开完成弹窗，默认选中该终态（`PlannerUi.OpenComplete(todo, status)` → `CompleteTaskDialog` 初始化 `_status`）。

## 显示条数（`rows`）

列表默认显示 **10 条**，可由宿主（DesktopAssembly）的组件设置调整。

- 入口查询参数：`/widgets/tasks?rows=N`。`N` 夹在 **1–50**（`PlannerConstants.WidgetTaskRows*`，
  上限复用 `MaxListLimit`）；空值 / 非数字回落默认 `10`。
- 组件在 `/v1/info` 与 `%LOCALAPPDATA%\SDemo\Hub\apps\sdemo.planner.json` 的 `widgets[]` 里
  用 `config`（`widget-contract.md §8` 的 Schema 子集）声明这个设置：
  `properties.rows`（`integer`，`minimum=1`、`maximum=50`、`default=10`）。
  宿主据此渲染「显示条数」，把值存进 `widgets.json`，再把 `?rows=N` 拼到 `entry` 上导航。
- 应用侧不做持久化：`WidgetTasks.razor` 用 `[SupplyParameterFromQuery(Name = "rows")]`
  读字符串再自行解析（非法值不抛异常），`TaskWidget.Rows` 再夹紧一次。
- 为判断「还有更多」，查询取 `N+1` 条（不超过 50）；标题显示 `10+`，
  列表底部提示「只显示前 10 条，可在组件设置里调大显示条数」。
  `rows=50` 时退回旧文案「最多显示 50 条，选项目或迭代可以缩小范围」。
- 宿主若把窗口高度固定，调大条数后仍可能需要滚动；这是窗口尺寸与条数的配合问题，
  与「取几条」是两件事。

## 与宿主自适应缩放（`data-fit-ignore`）

DesktopAssembly 的组件窗口会按内容尺寸自动调 `ZoomFactor`（`WidgetWindow.FitContentAsync`
的 `MeasureScript`：扫描所有元素，`scrollHeight > clientHeight` 即视为「内容装不下」，整页缩小）。
它分不清「真的溢出」和「这段本来就是滚动容器」，所以任务组件会出现两种抖动：

- 静止时列表本来就会滚，整页被压到 90% 左右；
- 双击任务打开详情，`.modal-body` 的正文高度被当成溢出，整页突然缩到 50% 上下，
  关闭后再来回跳几次（放大逻辑按 1.4× 试探）。

宿主日志可对照：`组件自适应缩放：sdemo.planner/tasks x → y（需要 ... 视口 ...）`。

应用侧的约定：给按设计就会滚动的容器加 `data-fit-ignore` 属性，宿主测量时跳过它。
当前标记处：

- `Components/TaskWidget.razor` 的 `.tw-list`
- `Components/Modal.razor` 的 `.modal-body`（所有详情/编辑弹窗共用）

宿主侧只需在 `MeasureScript` 的 `scan` 开头加一行：

```js
if (el.hasAttribute('data-fit-ignore')) { return; }
```

标记只影响宿主的测量，不改任何渲染；宿主还没实现时该属性无副作用。

## 数据上限

复用 `TodoQuery` / `PlannerService.ListTodos`，单次查询上限仍是契约里的
`PlannerConstants.MaxListLimit = 50`。`rows` 只能在这个上限内放大；要突破 50
属于契约变更，需同时改 MCP schema 与文档。

## 验证

- `dotnet build SDemo.Planner.slnf -c Release`
- `dotnet test tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release`
- 渲染冒烟（临时工程，不入库）：用 `HtmlRenderer` 渲染 `TaskWidget` 与 `TaskDetailDialog`，
  断言存在「项未完成」「优先级 · 截止」「延期」「查看详情」「状态」「标记完成」。
- 实机：重启宿主后确认 DesktopAssembly 出现「任务」组件；切项目/迭代、改排序、勾完成、延期、双击详情、详情改状态。
