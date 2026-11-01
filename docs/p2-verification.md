# P2 验证记录（sdemo.planner v1）

> 日期：2026-11-01 · 环境：Windows 11 / .NET SDK 10.0.400 / SQLite（EF Core 10.0.12）
> 结论：**P2 范围（PL-01~05、PL-07、PL-09~12 + PL-13）代码完成，自动门禁通过；
> 人工验收清单见 §4。**

## 1. 门禁命令与结果

| # | 命令 | 结果 |
| --- | --- | --- |
| G1 | `dotnet build SDemo.Planner.slnf -c Release` | ✅ 6 个项目，0 warning / 0 error |
| G2 | `dotnet test tests/SDemo.Planner.Tests -c Release` | ✅ 32 passed / 0 failed（3 s） |
| G3 | `pwsh tools/smoke-mcp.ps1` | ✅ initialize / 13 tools / create / list / complete / 冲突 / 中文搜索 / 草稿采纳 / stats / 优雅退出 |
| G4 | 启动 WPF（临时库）→ `GET /v1/health`、`/mcp tools/list`、`/v1/widgets/.../state` → `POST /v1/shutdown` | ✅ 主窗口正常加载，组件 `visible=true, opacity=0.94`，13 tools，退出码 0 |
| G5 | `dotnet ef migrations add InitialCreate`（Storage） | ✅ 产物含 `todo_fts` 虚表（手写 SQL 段），运行时迁移成功 |

复跑：

```powershell
cd Apps/Planner
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools/smoke-mcp.ps1
```

## 2. 需求覆盖

| 需求 | 实现 | 自动验证 | 状态 |
| --- | --- | --- | --- |
| PL-01 todo/event 不同表 | `todo` / `event` 两套实体与接口；统计只算 todo | `Event_DoesNotPolluteTodoCompletionStats` | ✅ |
| PL-02 待办字段 CRUD | 标题/备注/截止/优先级/清单/标签/链接；主窗口列表与编辑框 | `CreateTodos_*`、`Update_*`、`BulkUpdate_*`、`ClearList_*` | ✅ |
| PL-03 日程 + 冲突 | 起止/地点/参与人/关联待办；创建时检测重叠并提示 | `Event_Create_ReportsOverlap`、`CalendarRange_*` | ✅ |
| PL-04 日/周/月日历，拖拽改期 | 月（6×7，周一起）、周、日；未排期列表拖到日期改 `dueAt`；双击新建 | `CalendarRange_*` + G4（UI 启动） | ✅（拖拽需人工） |
| PL-05 ICS 导出 | VTODO + VEVENT，转义/折叠/状态/优先级 | `IcsExport_ContainsTodoAndEvent`、`IcsExport_EscapesAndFoldsLongLines` | ✅（系统日历导入待人工） |
| PL-07 组件 ≤8 条 | 320×440、不抢焦点、标题拖动、快速添加、勾完成；`maxItems` 3–30 | G4 + 组件代码 | ✅（视觉需人工） |
| PL-09 草稿审阅 | 草稿区独立；逐条采纳/改标题/拒收；疑似重复提示 | `Draft_DoesNotCreateTodosUntilAccepted`、`AcceptDraft_CanRejectItems`、MCP `PlanFromText_*` | ✅ |
| PL-10 MCP `todo_*` | 13 个工具；风险/审批元数据；`todo_delete` / `todo_clear_list` 永远需确认，批量 ≥5 阈值确认 | `ToolsList_ExposesThirteenToolsWithRiskMetadata`、G3 | ✅（`calendar_*` 属 P3） |
| PL-11 provenance | `source` + `sourceSessionId` + `sourceExcerpt`；服务端注入，模型不可伪造 | `CreateTodos_Manual_RecordsProvenance`、`CreateListComplete_*`（source=ai） | ✅ |
| PL-12 软删 + 回收站 | 7 天 `restorableUntil`；回收站视图恢复；到期清理 | `SoftDelete_IsRestorable`、`PurgeExpired_RemovesOnlyExpiredDeletedRows` | ✅ |
| PL-13 全文搜索 | FTS5 + 2-gram；AND→OR→LIKE 降级；bm25 + snippet | `Search_ChineseTwoCharacterQuery_Hits`、`Search_SingleCharacterFallsBackToLike`、G3 | ✅ |
| PL-14 outbox / op-log | 每次写同事务落 `outbox` + `op_log` | `EveryWrite_ProducesOutboxAndOpLog` | ✅ 结构就绪（合并 UI 属 P5） |

P2 范围外、**未做**：PL-06（只读订阅，P5）、PL-08（提醒，P3）、`calendar_*` 工具（P3）。

## 3. 关键自动化用例（32 条）

- 存储：迁移 + FTS 建表、中文两字命中、单字回退、revision 冲突、
  outbox/op-log、2-gram 词汇。
- 服务：创建来源、AI 完全重复拒绝 + 近似重复告警、更新、幂等完成、软删恢复、
  批量、清空、日历区间、事件冲突、统计隔离、草稿不落库、逐条拒收、
  ICS 内容/折叠、过期清理。
- MCP：initialize、13 工具与风险元数据、create→list→complete、创建幂等、
  缺少 `expectedRevision`、过期 revision、未知工具、草稿→采纳、中文搜索、
  删除回收站窗口、批量请求只回请求。

## 4. 人工验收清单（P2 出口）

1. `pwsh tools/install-planner.ps1`，宿主启动 `sdemo.planner`；组件出现在右上角。
2. 组件：输入框可打字；回车快速新增；点 ○ 完成；拖动标题栏移动；半透明、不抢焦点。
3. 打开主窗口：新建 / 编辑 / 勾选 / 重开 / 删除一条待办；删除后进回收站并恢复。
4. 日历：切月/周/日；从「待排期」拖一条到某天，确认 `dueAt` 变化；双击空白新建。
5. 新建日程与已有日程重叠，出现冲突提示。
6. 「导出 ICS」，用 Outlook / 系统日历导入，确认待办与日程都在。
7. 让 DesktopAgent 调 `todo_plan_from_text`，草稿页出现条目；逐条改标题/拒收后采纳，
   确认未采纳的条目不进待办。
8. 搜索「登录」这类两字词，结果命中标题/备注。
9. 关闭主窗口（手动启动场景）进程退出；宿主场景由宿主 `POST /v1/shutdown` 退出。

## 5. 已知限制与后续

| 项 | 说明 | 计划 |
| --- | --- | --- |
| ICS 重复规则 | v1 不导出 RRULE，也不能编辑重复 | P5（ADR-4） |
| 事件跨多天 | 只在开始日显示 | P3 可做跨格 |
| 只读订阅 | 未实现；UI 无「上次同步失败」横幅 | P5 / PL-06 |
| 提醒 | 未实现 | P3 / PL-08 |
| `calendar_*` 工具 | 未实现，工具面只有 `todo_*` | P3 / PL-10 扩展 |
| 清单/标签管理 UI | 标签在编辑框输入；清单只能选不能建 | 后续迭代 |
| 组件浅色主题 | 只切外框/标题色，卡片仍深色 | 后续迭代 |
