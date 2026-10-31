# sdemo.planner 需求计划

> 状态：需求计划（v1），2026-10-31
> 范围：需求编号 PL-01–PL-14；分期见 §5。
> 关联：[品牌蓝图](../../../docs/sdemo-brand-blueprint.md) §B · [工作台规划](../../../docs/workbench-roadmap.md) §3.1 · [MCP 工具面](planner-mcp-surface.md) · [联动需求](../../../docs/integration.md)

---

## 1. 目标与边界

**一句话**：一张清单加一本日历，AI 能起草，人负责点头。

- 一个应用、一个库；`todo`（任务，可完成）与 `event`（日程，有起止）**同库不同表**。
- 对外集成只做 **ICS（RFC 5545）**：导出 + 只读订阅；不做 Outlook / Google 双向同步。
- **零模型凭证**：AI 草稿由 DesktopAgent 生成，本应用只校验、去重提示、入库。
- 数据目录：`%APPDATA%\SDemo\Apps\sdemo.planner\`（`planner.db` + `outbox\`）。

**v1 不做**：团队共享、多人 ACL、RRULE 完整编辑、双向日历同步、云同步（P5 只做零服务器导出/导入）。

## 2. 需求清单

| # | 需求 | 优先级 | 验收要点 |
| --- | --- | --- | --- |
| PL-01 | `todo` 与 `event` 同库不同表；todo 可完成，event 有起止 | P0 | 完成率统计不受 event 污染 |
| PL-02 | 待办：标题/备注/截止/优先级/清单/标签 | P0 | 字段齐全，列表可筛选 |
| PL-03 | 日程：起止/参与人/地点/关联待办 | P0 | 时间冲突能看见 |
| PL-04 | 日历视图：日/周/月，todo 的 `dueAt` 显示为点 | P0 | 拖拽改期即改 `dueAt` |
| PL-05 | ICS 导出（todo + event） | P0 | 导入系统日历不报错 |
| PL-06 | ICS 只读订阅（URL/本地文件，定时拉取） | P1 | 外部改动可见，不写回 |
| PL-07 | 桌面组件：≤8 条待办 + 快速新增 + 勾选 | P0 | 半透明、不抢焦点、输入框可打字 |
| PL-08 | 提醒：到期前 N 分钟通知 | P1 | 走宿主通知通道 |
| PL-09 | AI 草稿审阅：逐条 采纳/编辑/拒绝 + 疑似重复 | P0 | 草稿不入库 |
| PL-10 | MCP 工具面：`todo_*` / `calendar_*` | P0 | 删除/批量必须审批 |
| PL-11 | provenance：`source` + 会话 + 原文片段 | P0 | 每条能回答"从哪句话来的" |
| PL-12 | 软删除 + 7 天回收站 + 撤销 | P0 | 误删可恢复 |
| PL-13 | 搜索：标题/备注全文 | P1 | 中文两字可命中 |
| PL-14 | outbox 变更日志，为多端同步预留 | P1 | 导出的 op-log 可被另一端合并 |

## 3. 非功能与数据

| 项 | 约定 |
| --- | --- |
| 存储 | SQLite；`todo` / `event` / `list` / `tag` / `links` / `draft` / `outbox` |
| 时间 | 一律存 UTC，按本地时区显示；`dueAt` 与 event 起止同一套转换 |
| 迁移 | 真实 EF migrations（不用 `EnsureCreated`）；启动时单实例迁移 |
| 搜索 | FTS5 + 2-gram 预分词（复用 DesktopAgent 已验证方案），中文两字可命中 |
| 组件 | 只读+勾选+快速新增；不渲染完整日历 |
| 审阅 | 草稿区与正式表物理分开；采纳才写 `source` / `sessionId` / `quote` |

## 4. 工程约束（多仓）

- 五件套：`SDemo.App.Planner.{Contracts,Storage,Core,Mcp,Host}`；`Mcp` 单独工程，工具面与领域逻辑物理隔离。
- 不引用 Agent、不引用其它 App；跨应用只存 URI 字符串（`sdemo.kb/doc/<id>@v#b`）。
- MCP 端点只回环 + 进程级 token；工具声明风险等级（低/中/高）。
- 契约（组件字段、URI、审批）以主仓 [contracts](../../../docs/contracts/) 为准。

## 5. 分期与交付

| 期 | 覆盖需求 | 交付物 | 验收 |
| --- | --- | --- | --- |
| **P2** v1 | PL-01 ~ PL-05、PL-07、PL-09 ~ PL-12 | 手工 CRUD、日历视图、ICS 导出、组件、`todo_*` MCP | 手建/勾选/改期可用；导出可导入系统日历；MCP 建待办通 |
| **P2 尾** | PL-13 | 搜索 | 中文两字命中 |
| **P3** | PL-10 扩展 | `calendar_*` 工具 + Agent 联调 | 聊天里能建待办、删除弹确认 |
| **P5** | PL-14、PL-06 | outbox + 零服务器导出/导入；ICS 只读订阅 | 两台机器合并后条目一致、冲突可见 |

## 6. 联动点

| 联动 | 做法 |
| --- | --- |
| 会议候选 → 待办 | Meeting 产出 `mtg_action(candidate)` → Agent 调 `todo_create_draft` → 用户审阅 |
| 待办关联文档 | 存 `sdemo.kb/doc/<id>@v#b` 字符串；Agent 解析跳转 |
| 统一搜索 | Agent fan-out 调 `todo_search` / `calendar_range`，结果合并排序 |
| 高风险审批 | 删除 / 批量≥5 / 清空 → 每次确认 + 审计，不可关闭 |

## 7. 风险与待定

| 项 | 说明 |
| --- | --- |
| R1 | todo + event 合并后模块变大 → 两套实体、两套接口，别做万能实体 |
| R2 | 日历视图交互复杂度 → 先日/周/月只读 + 拖拽改期，月视图重复规则后置 |
| Q1 | 清单（list）与标签的默认模型（默认清单 + 可关标签） |
| Q2 | RRULE 支持到哪一档（v1 建议：不编辑重复，只显示外部 ICS 带来的重复） |
| Q3 | 提醒通道走宿主通知还是应用内（建议宿主） |
