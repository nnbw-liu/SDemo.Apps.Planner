# SDemo.Apps.Planner

**sdemo.planner**：待办 + 日历。`todo` 与 `event` 同库不同表；AI 只产草稿，人审阅后入库。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块（`Apps/Planner`）。
> 状态：🚧 **规划中（P2 / P5）**，本仓目前只有文档，没有代码。

## 文档

| 想知道 | 读 |
| --- | --- |
| 需求计划（PL-01–14、分期、验收） | [docs/requirements.md](docs/requirements.md) |
| MCP 工具面（`todo_*`，待扩 `calendar_*`） | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 需求原文与优先级 | [../../docs/sdemo-brand-blueprint.md](../../docs/sdemo-brand-blueprint.md) §B |
| 联动（引用、统一搜索、审批） | [../../docs/integration.md](../../docs/integration.md) |

## 边界

- **零模型凭证**：AI 草稿由 DesktopAgent 生成，本应用只做校验、去重提示与入库。
- **todo 与 event 不混表**：`todo` 可完成、只有截止点；`event` 有起止、不可完成。
- **外部集成只做 ICS**：导出 + 只读订阅；不做 Outlook / Google 双向同步。
- **删除软删 + 7 天回收站**；高风险操作（删除/批量≥5/清空）审批不可关闭。
- 数据目录 `%APPDATA%\SDemo\Apps\sdemo.planner\`（`planner.db` + `outbox\`）。
