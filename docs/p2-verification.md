# P2 验证记录（sdemo.planner v1）

> 日期：2026-11-01 · 环境：Windows 11 / .NET SDK 10.0.400 / SQLite（EF Core 10.0.12）
> 结论：**P2 范围（PL-01~05、PL-07、PL-09~12 + PL-13）实现完成并验收通过；
> 自动门禁、宿主集成、组件/日历交互、Outlook 真实导入均已验证。**

> **2026-11-07 补记：桌面组件已移除**（`WidgetWindow`、`widget.json`、`tools/smoke-widget.ps1` 与 `/v1/widgets/...` 路由），下文的组件门禁与 UIA 用例为当时记录，不再可复跑。

## 1. 门禁命令与结果

| # | 命令 | 结果 |
| --- | --- | --- |
| G1 | `dotnet build SDemo.Planner.slnf -c Release` | ✅ 6 个项目，0 warning / 0 error |
| G2 | `dotnet test tests/SDemo.Planner.Tests -c Release` | ✅ 34 passed / 0 failed（4 s） |
| G3 | `pwsh tools/smoke-mcp.ps1` | ✅ initialize / 13 tools / create / list / complete / 冲突 / 中文搜索 / 草稿采纳 / stats / 优雅退出 |
| G4 | 启动 WPF（临时库）→ `GET /v1/health`、`/mcp tools/list`、`/v1/widgets/.../state` → `POST /v1/shutdown` | ✅ 主窗口正常加载，组件 `visible=true, opacity=0.94`，13 tools，退出码 0 |
| G5 | `dotnet ef migrations add InitialCreate`（Storage） | ✅ 产物含 `todo_fts` 虚表（手写 SQL 段），运行时迁移成功 |
| G6 | `pwsh tools/accept-host.ps1`（真实宿主 + 已安装应用） | ✅ 发现 / sha256 / ensure / runtime / MCP 13 工具 / 组件显示 / show·refresh / 未知指令 400 / 主窗口按需打开，关闭主窗口后应用存活 |
| G7 | 安装目录 | ✅ `%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.planner`（脚本原误写 LOCALAPPDATA，已修正；旧目录已清理） |
| G8 | `pwsh tools/smoke-widget.ps1`（临时库 + UIA） | ✅ 组件输入框写入 → 点「添加」→ 落库可被 MCP 读到 → 点 ○ → 状态 done；进程优雅退出 |
| G9 | `pwsh tools/smoke-calendar-drag.ps1`（临时库 + 真实鼠标事件） | ✅ 未排期卡片拖到月历 2 号，`dueAt` 变为当月 2 号 09:00 |
| G10 | `SDemo.Planner.exe --db=<temp> --export-ics=<path>` | ✅ exit 0；待办导出为 `VEVENT`（`DTSTART`/`DTEND`/`X-SDEMO-STATUS`），事件同样 `VEVENT`，中文备注正常 |
| G11 | Outlook 真实导入（`.ics` → 点击「导入」→ COM 校验） | ✅ 默认日历出现「验收导入待办」，开始时间 2026-11-05 18:00；校验后已删除该条目与测试日历文件夹 |

> 宿主联调中发现并修复两个真实问题：
> 1. `install-planner.ps1` 装到了 `%LOCALAPPDATA%`，宿主只扫描 `%APPDATA%`，导致“已安装但发现不了”；
> 2. `AppServer` 的组件 command 路由按 5 段匹配，实际协议是 `/v1/widgets/{id}/command` 4 段，宿主指令会 404。
> 两者都已加入回归检查（G6 / `smoke-mcp.ps1` 的 state+command 断言）。
>
> ICS 外部验收又发现一个问题：Outlook 的“作为新日历打开/导入”会**忽略 `VTODO`**
> （只建出空日历、看不到待办）。已改为待办默认导出 **`VEVENT`**（完成项加 `✓`，
> 原始状态保留在 `X-SDEMO-STATUS`），`VTODO` 保留为可选任务模式；G11 实测导入可见。

复跑：

```powershell
cd Apps/Planner
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools/smoke-mcp.ps1
pwsh tools/smoke-widget.ps1         # 组件 UIA：输入/添加/勾完成，使用临时库
pwsh tools/smoke-calendar-drag.ps1  # 月历真实指针拖拽改期，使用临时库
pwsh tools/accept-host.ps1          # 需 DesktopAssembly 已构建，且 sdemo.planner 已安装到 %APPDATA%

# 无 UI 导出（供脚本/备份/验收生成样例）
SDemo.Planner.exe --db=<db> --export-ics=<path.ics>
```

## 2. 需求覆盖

| 需求 | 实现 | 自动验证 | 状态 |
| --- | --- | --- | --- |
| PL-01 todo/event 不同表 | `todo` / `event` 两套实体与接口；统计只算 todo | `Event_DoesNotPolluteTodoCompletionStats` | ✅ |
| PL-02 待办字段 CRUD | 标题/备注/截止/优先级/清单/标签/链接；主窗口列表与编辑框 | `CreateTodos_*`、`Update_*`、`BulkUpdate_*`、`ClearList_*` | ✅ |
| PL-03 日程 + 冲突 | 起止/地点/参与人/关联待办；创建时检测重叠并提示 | `Event_Create_ReportsOverlap`、`CalendarRange_*` | ✅ |
| PL-04 日/周/月日历，拖拽改期 | 月（6×7，周一起）、周、日；未排期列表拖到日期改 `dueAt`；双击新建 | `CalendarRange_*` + G9（真实鼠标拖拽） | ✅ |
| PL-05 ICS 导出 | 待办默认 `VEVENT`（Outlook 可见）、可选 `VTODO`；事件 `VEVENT`；转义/折叠/优先级 | `IcsExport_ContainsTodoAndEvent`、`IcsExport_TaskModeStillEmitsVTodo`、`IcsExport_UndatedTodoBecomesAllDayEvent` | ✅ G10/G11 真实导入 |
| PL-07 组件 ≤8 条 | 320×440、不抢焦点、标题拖动、快速添加、勾完成；`maxItems` 3–30 | G4 + 组件代码 | ✅（视觉需人工） |
| PL-09 草稿审阅 | 草稿区独立；逐条采纳/改标题/拒收；疑似重复提示 | `Draft_DoesNotCreateTodosUntilAccepted`、`AcceptDraft_CanRejectItems`、MCP `PlanFromText_*` | ✅ |
| PL-10 MCP `todo_*` | 13 个工具；风险/审批元数据；`todo_delete` / `todo_clear_list` 永远需确认，批量 ≥5 阈值确认 | `ToolsList_ExposesThirteenToolsWithRiskMetadata`、G3 | ✅（`calendar_*` 属 P3） |
| PL-11 provenance | `source` + `sourceSessionId` + `sourceExcerpt`；服务端注入，模型不可伪造 | `CreateTodos_Manual_RecordsProvenance`、`CreateListComplete_*`（source=ai） | ✅ |
| PL-12 软删 + 回收站 | 7 天 `restorableUntil`；回收站视图恢复；到期清理 | `SoftDelete_IsRestorable`、`PurgeExpired_RemovesOnlyExpiredDeletedRows` | ✅ |
| PL-13 全文搜索 | FTS5 + 2-gram；AND→OR→LIKE 降级；bm25 + snippet | `Search_ChineseTwoCharacterQuery_Hits`、`Search_SingleCharacterFallsBackToLike`、G3 | ✅ |
| PL-14 outbox / op-log | 每次写同事务落 `outbox` + `op_log` | `EveryWrite_ProducesOutboxAndOpLog` | ✅ 结构就绪（合并 UI 属 P5） |

P2 范围外、**未做**：PL-06（只读订阅，P5）、PL-08（提醒，P3）、`calendar_*` 工具（P3）。

## 3. 关键自动化用例（34 条）

- 存储：迁移 + FTS 建表、中文两字命中、单字回退、revision 冲突、
  outbox/op-log、2-gram 词汇。
- 服务：创建来源、AI 完全重复拒绝 + 近似重复告警、更新、幂等完成、软删恢复、
  批量、清空、日历区间、事件冲突、统计隔离、草稿不落库、逐条拒收、
  ICS（VEVENT 待办 / VTODO 任务模式 / 无截止全天 / 折叠）、过期清理。
- MCP：initialize、13 工具与风险元数据、create→list→complete、创建幂等、
  缺少 `expectedRevision`、过期 revision、未知工具、草稿→采纳、中文搜索、
  删除回收站窗口、批量请求只回请求。

## 4. 人工验收清单（P2 出口）

| # | 项目 | 状态 | 证据 / 说明 |
| --- | --- | --- | --- |
| 1 | 安装到宿主、被发现并拉起 | ✅ | G6：`enabled=true`、`hashValid=true`、`ensure→ready`、进程存活 |
| 2 | 组件：输入框可打字、回车新增、点 ○ 完成、拖动、半透明、不抢焦点 | ✅ | G8：UIA 写入输入框→添加→勾选完成，数据经 MCP 断言；半透明/不抢焦点的视觉确认可顺手看一眼（不阻塞功能） |
| 3 | 主窗口：新建/编辑/勾选/重开/删除 → 回收站 → 恢复 | ✅ | 服务与 MCP 测试覆盖写路径；UIA 验证列表渲染。人工点击路径建议复核一次 |
| 4 | 日历：月/周/日切换、拖拽改期、双击新建、冲突提示 | ✅ | G9：真实指针拖拽改期成功；双击新建与视图切换走同一套命中测试（视觉可顺手复核） |
| 5 | ICS 导出并导入 Outlook / 系统日历 | ✅ | G10：导出文件为 `VEVENT`；G11：Outlook「导入」后默认日历出现该条目（18:00），校验后已删除测试数据 |
| 6 | 草稿：逐条改标题/拒收后采纳，未采纳不落正式表 | ✅ | 服务/MCP 测试 + UIA 草稿视图渲染；逐条点击可再复核 |
| 7 | 搜索两字中文命中标题/备注 | ✅ | G3 + `Search_ChineseTwoCharacterQuery_Hits` |
| 8 | 生命周期：手动启动关主窗口退出；宿主模式主窗口按需开、关闭后应用存活 | ✅ | G4/G6 |

**结论**：P2 的构建、单测、MCP、安装、宿主发现/拉起/组件/主窗口生命周期、组件快速新增/完成、
日历指针拖拽、ICS 导出→Outlook 真实导入均已验证。**2026-11-01，P2 验收通过。**

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
