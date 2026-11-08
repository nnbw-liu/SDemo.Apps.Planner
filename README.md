# 事序（SDemo.Apps.Planner）

**事序**（`sdemo.planner`）：用项目承接目标，用迭代拆节奏，用任务推进，用日常守住生活。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块（`Apps/Planner`）。
> 状态：✅ **v4 已实现并实机验证**（0.4.1，2026-11-07）。WPF 外壳 + Blazor UI；项目/大迭代/小迭代/工作项、六状态与完成记录、日常提醒、日历节假日、托盘常驻。

## 快速开始

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools\smoke-mcp.ps1

# 本地跑 UI（临时库，不碰真实数据）
dotnet run --project src\SDemo.App.Planner.Host -- --db=$env:TEMP\planner-dev.db

# 无 UI 导出 ICS
dotnet run --project src\SDemo.App.Planner.Host -- --db=$env:TEMP\planner-dev.db --export-ics=$env:TEMP\planner.ics

# 安装进宿主
pwsh tools\install-planner.ps1
```

> `tools\smoke-calendar-drag.ps1` 只用于 v1 WPF 月历；v2 起的 Blazor 月历暂不支持拖拽。

## 能力

- **项目 / 迭代 / 工作项**：项目 → 大迭代 → 小迭代 → 任务/Bug/调研/杂务；进度、逾期、Backlog。
- **工作项六状态**：新建 / 进行中 / 已完成 / 已验证 / 已关闭 / 已取消；终态记录完成说明与产物，可延期、重开；按优先级/截止/更新时间/创建时间排序。
- **首页**：今日日常打卡、今日状态、进行中的迭代、接下来 7 天、项目。
- **日常提醒**：每天固定时间 / 每隔 N 分钟，按星期重复、每日目标次数、30 分钟宽限、右下角 Toast。
- **日历**：任务截止 + 日程起止；中国法定节假日联网同步，支持手动休/班，manual 优先。
- **搜索**：跨全部状态匹配标题/备注/完成说明；顶部 ×、任务页「清除」、Esc 均可撤销。
- **桌面体验**：深色 WPF 外壳；关闭窗口收进托盘，后台提醒继续；应用图标。
- **数据与集成**：SQLite 单库；软删回收站 7 天；13 个 `todo_*` MCP 工具；ICS 导出。
- **桌面组件**：v1 的小窗口组件已于 2026-11-07 移除，替代方案待定；宿主拉起应用时直接显示主窗口。

## 文档

| 想知道 | 读 |
| --- | --- |
| v4 更名 / 搜索 / 节假日 | [docs/planner-v4.md](docs/planner-v4.md) |
| v3 状态 / 完成记录 / 托盘 | [docs/planner-v3.md](docs/planner-v3.md) |
| v2 产品 / 数据 / 提醒 | [docs/planner-v2.md](docs/planner-v2.md) |
| v1 数据模型 / 日历 / ICS / 决策 | [docs/planner-design.md](docs/planner-design.md) |
| P2 验证记录与人工清单 | [docs/p2-verification.md](docs/p2-verification.md) |
| 需求计划（PM-01–24、分期、验收） | [docs/requirements.md](docs/requirements.md) |
| MCP 工具面（`todo_*` 13 个；项目/迭代/日常/节假日待扩） | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 需求原文与优先级 | [../../docs/sdemo-brand-blueprint.md](../../docs/sdemo-brand-blueprint.md) §B |
| 联动（引用、统一搜索、审批） | [../../docs/integration.md](../../docs/integration.md) |

## 边界

- **零模型凭证**：AI 草稿由 DesktopAgent 生成，本应用只做校验、去重提示与入库。
- **todo 与 event 不混表**：`todo` 可完成、只有截止点；`event` 有起止、不可完成。
- **外部集成只做 ICS**：导出 + 只读订阅；不做 Outlook / Google 双向同步。
- **删除软删 + 7 天回收站**；高风险操作（删除/批量≥5/清空）审批不可关闭。
- 数据目录 `%APPDATA%\SDemo\Apps\sdemo.planner\`（`planner.db` + `exports\`）。
