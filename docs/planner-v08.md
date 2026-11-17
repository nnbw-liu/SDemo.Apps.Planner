# 事序 0.8.0 · 任务阶段进展（Progress）

> 版本：0.8.0 · 2026-11-16 · 代码根 `src/SDemo.App.Planner.*`

## 目标

任务过去只有「说明」和「完成说明 + 产物」，中途做到哪一步、每个阶段的产出
（方案链接、构建产物、验收截图…）没有地方记。0.8 增加 **阶段进展**：

- 一条进展 = 一个阶段（方案评审 / 开发 / 联调 / 验收 / 复盘），带日期、正文、可选的整体进度百分比；
- 每条进展有自己的 **产物 / 链接** 与 **附件**；
- 任务详情直接按时间线展示，任务列表显示进度胶囊（百分比或条数）；
- 进展是任务的从属记录：跟着任务删除进回收站，7 天后物理清理；任务被物理清理时级联清掉。

## 一、数据模型

新表 `todo_progress`（迁移 `ProgressV8`，2026-11-16）：

| 列 | 说明 |
| --- | --- |
| `id` | `prg_` 前缀 |
| `todo_id` | 所属任务；有 `(todo_id, occurred_at)` 索引 |
| `title` | 阶段名，≤ `MaxProgressTitleChars`（80），空则落「进展」 |
| `notes` | 阶段说明，≤ `MaxProgressNotesChars`（2000） |
| `percent` | 可选整体进度 0–100 |
| `artifacts_json` | 产物 / 链接数组（同任务的 `ArtifactsJson` 口径） |
| `occurred_at` | 阶段发生时间（UTC） |
| `revision` / `created_at` / `updated_at` / `deleted_at` | 乐观并发 + 软删，与其它表一致 |

限制常量（`PlannerConstants`，改它等于改契约）：

| 常量 | 值 |
| --- | --- |
| `MaxProgressPerTodo` | 100 |
| `MaxProgressTitleChars` | 80 |
| `MaxProgressNotesChars` | 2000 |
| `MaxProgressArtifacts` | 30 |

## 二、服务接口

`src/SDemo.App.Planner.Core/PlannerService.Progress.cs`（partial）：

| 方法 | 行为 |
| --- | --- |
| `ListTodoProgress(todoId)` | 按 `OccurredAt` 升序返回；任务不存在/已删 → `NOT_FOUND` |
| `AddTodoProgress(todoId, TodoProgressInput, WriteContext)` | 校验标题/说明/百分比/产物，超 100 条 → `LIMIT` |
| `UpdateTodoProgress(id, expectedRevision, TodoProgressPatch, WriteContext)` | 合并语义（没传的字段保留；`Clear*` 显式清空）；revision 不符 → `CONFLICT` |
| `DeleteTodoProgress(id, expectedRevision, reason, WriteContext)` | 软删 + `op_log`（`entity_type = progress`） |
| `CountAttachments(ownerType, ownerIds)` | 批量数附件，供进展卡片显示「附件 N」 |

列表汇总不再额外查库：`MapTodos` 一次 `QueryProgressForTodos(ids)` 算出
`ProgressCount` / `ProgressPercent`（最近一条带百分比的记录）/ `LatestProgressTitle`，
由 `PlannerMapper.ToTodo(..., progress)` 写进 `TodoDto`。任务行只有 0.6 条查询的成本。

### 附件

`AttachmentOwnerTypes` 新增 `Progress = "progress"`。`AttachmentPanel` 复用同一块组件：

- 进展附件落在 `{root}\progress\{progressId}\at_{id}{ext}`；
- 进展软删后附件仍保留（`AttachmentOwnerExists` 用 `includeDeleted: true` 查进展）；
- 7 天后进展被物理清理，附件变成孤儿，`PurgeExpired` 扫到时删文件 + 删行；
- 任务被物理清理时，`ApplyTodoWrites` 的 purge 分支先删该任务的进展（含附件则下一轮扫描收掉）。

## 三、界面

### 任务详情（`TaskDetailDialog`）

「任务说明」之后、关联 Bug 之前插入 `TaskProgressPanel`：

- 头部：`阶段进展` + 条数 + 最近整体百分比 + **记录进展**；
- 进度条：最近一次带百分比的记录；
- 时间线倒序卡片：阶段名、日期、百分比徽标、正文、产物链接、`附件 N` 折叠面板；
  卡片右上角编辑 / 删除（二次确认）；
- 内联编辑器：阶段名、日期、整体进度（%）、正文、产物（一行一个）、保存/取消。

### 任务列表（`TaskRow`）

`task-meta` 尾部出现进度胶囊：迷你进度条 + `60%` 或 `3 条进展`，
有百分比时 tooltip「整体进度 60% · 3 条阶段进展」。终态任务胶囊变灰。

### 样式

`app.css` 末尾新增 `.progress-*` 段（`.progress-meter`、`.progress-list`、
`.progress-card`、`.progress-editor`、`.progress-mini`）。全部用主题 token
（`--line` / `--text` / `--muted` / `--faint` / `--accent` / `--accent-soft` / `--red`），
半透明底一律 `rgba(var(--wash-rgb), α)`。

## 四、边界与生命周期

- **不做终态限制**：任务完成后仍可补一条复盘进展；完成说明 / 产物仍走 `SetTodoStatus`。
- **软删 + 回收站**：`DeleteTodoProgress` 只写 `deleted_at`；`PurgeExpired` 同时清理
  过期进展（`PurgeExpiredProgress`），返回条数计入总清理数。
- **并发**：每条进展独立 `revision`，更新/删除必须带 `expectedRevision`；
  列表卡片上的编辑使用详情里读到的 revision。
- **不做 MCP 工具**（本轮范围）：`progress_*` 留给下一次；AI 仍走草稿区。
- **不写 `todo.revision`**：进展变化不改变任务本身 revision；任务行通过派生汇总刷新。

## 五、验证

| 门禁 | 结果 |
| --- | --- |
| `dotnet build SDemo.Planner.slnf -c Release` | 通过（0 警告 0 错误） |
| `dotnet test tests\SDemo.Planner.Tests -c Release` | 125 通过 / 0 失败 |

新增 `tests/SDemo.Planner.Tests/ProgressTests.cs`：

- `AddProgress_UpdatesTodoSummary`：字段落库 + `TodoDto.ProgressCount/Percent/LatestProgressTitle`；
- `UpdateProgress_MergesFieldsAndBumpsRevision`：合并语义、`ClearPercent/ClearArtifacts`、revision +1；
- `UpdateProgress_RejectsStaleRevision`：`CONFLICT` 带 `currentRevision`；
- `DeleteProgress_HidesFromListAndPurgesAfterRetention`：软删后列表为空，过了 7 天物理清理；
- `DeleteTodo_CascadesProgressAndOrphanAttachments`：任务 purge 级联进展，孤儿附件文件一并清理；
- `AddProgress_ValidatesPercentArtifactsAndTitle`：百分比范围、30 个产物上限、80 字阶段名。

## 六、本次改动文件

- `Contracts/Progress.cs`（新增）：`TodoProgressDto` / `TodoProgressInput` / `TodoProgressPatch` / `TodoProgressSummaryDto`；
- `Contracts/Dtos.cs`：`TodoDto` 增 `ProgressCount` / `ProgressPercent` / `LatestProgressTitle`；
- `Contracts/Attachments.cs`：`AttachmentOwnerTypes.Progress`；
- `Contracts/PlannerConstants.cs`：4 个进展常量 + `AppVersion = 0.8.0`；
- `Storage/Entities.cs`：`TodoProgressRow`；
- `Storage/PlannerDbContext.cs`：`todo_progress` 映射；
- `Storage/PlannerStore.Progress.cs`（新增）：进展读写、软删、到期清理、任务级联；
- `Storage/PlannerStore.Attachments.cs`：`CountAttachments`；
- `Storage/PlannerStore.cs`：任务 purge 级联进展；
- `Storage/Migrations/20261116061210_ProgressV8.cs`（新增）；
- `Core/PlannerService.Progress.cs`（新增）：进展门面；
- `Core/PlannerService.cs`：`MapTodos` 汇总、`PurgeExpired` 清理进展、附件 owner 判定；
- `Core/PlannerService.Attachments.cs`：进展可作为附件宿主；
- `Core/PlannerMapper.cs`：`ToTodo` 新增可选 `TodoProgressSummaryDto`；
- `Web/Components/TaskProgressPanel.razor`（新增）、`TaskDetailDialog.razor`、`TaskRow.razor`；
- `Host/wwwroot/css/app.css`：`.progress-*` 样式；
- `tests/.../ProgressTests.cs`（新增）；
- 版本：`Host.csproj`、`deploy/app.json`、`PlannerConstants.AppVersion`。
