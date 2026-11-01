# SDemo.Apps.Planner

**sdemo.planner**：待办 + 日历。`todo` 与 `event` 同库不同表；AI 只产草稿，人审阅后入库。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块（`Apps/Planner`）。
> 状态：✅ **P2 已实现**（v0.1.0，2026-11-01）；P3 做 `calendar_*` / 提醒，P5 做 ICS 订阅与 outbox 合并。

## 快速开始

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools\smoke-mcp.ps1
pwsh tools\smoke-widget.ps1
pwsh tools\smoke-calendar-drag.ps1

# 本地跑 UI（临时库，不碰真实数据）
dotnet run --project src\SDemo.App.Planner.Host -- --db=$env:TEMP\planner-dev.db

# 无 UI 导出 ICS
dotnet run --project src\SDemo.App.Planner.Host -- --db=$env:TEMP\planner-dev.db --export-ics=$env:TEMP\planner.ics

# 安装进宿主
pwsh tools\install-planner.ps1
```

## 能力

- 待办 CRUD（标题/备注/截止/优先级/清单/标签/引用）、完成/重开、软删回收站 7 天。
- 日程 CRUD + 时间冲突提示；日历日 / 周 / 月视图，拖拽改 `dueAt`，双击新建。
- AI 草稿审阅：逐条采纳 / 改标题 / 拒收，疑似重复提示；草稿不写正式表。
- 13 个 `todo_*` MCP 工具（Streamable HTTP，Bearer token，风险元数据 + 审计）。
- 桌面组件：≤8 条未完成待办、快速添加、勾完成、不抢焦点。
- ICS 导出（VTODO + VEVENT）；中文两字全文搜索（FTS5 + 2-gram）。

## 文档

| 想知道 | 读 |
| --- | --- |
| 设计（数据模型 / 日历 / ICS / 决策） | [docs/planner-design.md](docs/planner-design.md) |
| P2 验证记录与人工清单 | [docs/p2-verification.md](docs/p2-verification.md) |
| 需求计划（PL-01–14、分期、验收） | [docs/requirements.md](docs/requirements.md) |
| MCP 工具面（`todo_*`，待扩 `calendar_*`） | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 需求原文与优先级 | [../../docs/sdemo-brand-blueprint.md](../../docs/sdemo-brand-blueprint.md) §B |
| 联动（引用、统一搜索、审批） | [../../docs/integration.md](../../docs/integration.md) |

## 边界

- **零模型凭证**：AI 草稿由 DesktopAgent 生成，本应用只做校验、去重提示与入库。
- **todo 与 event 不混表**：`todo` 可完成、只有截止点；`event` 有起止、不可完成。
- **外部集成只做 ICS**：导出 + 只读订阅；不做 Outlook / Google 双向同步。
- **删除软删 + 7 天回收站**；高风险操作（删除/批量≥5/清空）审批不可关闭。
- 数据目录 `%APPDATA%\SDemo\Apps\sdemo.planner\`（`planner.db` + `exports\`）。
