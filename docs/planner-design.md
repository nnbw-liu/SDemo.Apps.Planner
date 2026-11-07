# sdemo.planner 设计（P2 / v1）

> **2026-11-06 更新**：产品已升级为 v4「事序」。项目 / 大迭代 / 小迭代 / 工作项、日常提醒、Blazor UI 见 [planner-v2.md](planner-v2.md)；v3 的六状态 / 完成记录 / 延期 / 迭代完成 / 托盘见 [planner-v3.md](planner-v3.md)；v4 的更名 / 搜索 / 法定节假日见 [planner-v4.md](planner-v4.md)。本文保留 v1 的 todo/event/日历/ICS 设计，作为底层数据能力说明。v2/v3/v4 新增表与页面以新文档为准。

> 状态：**P2 已实现**（2026-11-01）。本文件记录实现时的数据模型、交互与取舍；
> 需求编号见 [requirements.md](requirements.md)，工具面见 [planner-mcp-surface.md](planner-mcp-surface.md)。

## 1. 边界

| 做 | 不做 |
| --- | --- |
| 待办（可完成、只有一个截止点）与日程（有起止、不可完成），同库不同表 | 不做「万能条目」把两者合表 |
| AI 只产**草稿**，人逐条采纳后才写正式待办 | 应用内不调用模型，不持有任何 Provider / Key |
| ICS 导出 + 只读订阅（P2 只做导出；订阅刷新留 P5） | 不做 Outlook / Google 双向同步 |
| 软删除 + 7 天回收站；MCP 写操作落审计 | 不做物理删除 |
| 桌面组件最多 8 条未完成待办 | 组件不承担完整 CRUD / 日历 |

数据目录：`%APPDATA%\SDemo\Apps\sdemo.planner\planner.db`；
日志：`%LOCALAPPDATA%\SDemo\logs\planner\yyyy-MM-dd.log`；
进程发现：`%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\sdemo.planner.json`。

## 2. 工程结构

```
src/
  SDemo.App.Planner.Contracts  DTO / 输入 / 常量 / 错误码 / JSON 与时间口径
  SDemo.App.Planner.Storage    EF Core 实体、迁移、FTS5、事务写入
  SDemo.App.Planner.Core       服务门面、校验、映射、ICS、草稿拆分、相似度
  SDemo.App.Planner.Mcp        JSON-RPC、13 个 todo_* 工具、风险元数据、审计
  SDemo.App.Planner.Host       WPF 主窗口 / 组件 / app 面 HTTP / 进程入口
tests/
  SDemo.Planner.Tests          xunit：存储、服务、MCP
```

依赖方向：`Host → Mcp → Core → Storage → Contracts`。Core 不依赖 WPF / HTTP；
Mcp 不依赖 WPF；Host 是唯一组合根。应用之间零 `ProjectReference`。

## 3. 数据模型

表名与列名全部 snake_case（显式映射）。时间一律存 UTC（`DateTime` + converter），
对外 DTO 用 `DateTimeOffset`，显示时转本地。

### 3.1 `todo`

| 列 | 说明 |
| --- | --- |
| `id` | `td_` + 20 位 GUID fragment |
| `title` / `notes` | 200 / 4000 字符上限 |
| `status` | `open` / `doing` / `done` |
| `priority` | 0–4，默认 2 |
| `due_at` | 可空；**没有 start/end** |
| `completed_at` | 完成时间；重开时清空 |
| `list_id` / `list_name` | 清单外键 + 反规范化名称（列表查询免 join） |
| `revision` | 乐观并发，创建为 1，每次写 +1 |
| `source` | `manual` / `ai` / `meeting` / `import` / `ics` |
| `source_session_id` / `source_excerpt` | 来源会话与原文片段（provenance） |
| `created_at` / `updated_at` | UTC |
| `deleted_at` / `restorable_until` | 软删；后者 = 删除时间 + 7 天 |
| `fts_row_id` | 对应 `todo_fts` 的 rowid |

### 3.2 `event`

`id`(`ev_`)、`title`、`notes`、`start_at`、`end_at`、`location`、`participants_json`、
`linked_todo_id`（只存本应用 todo id）、`list_id`、`revision`、`source`、
`created_at` / `updated_at` / `deleted_at` / `restorable_until`。
**没有 status / completed_at**——日程不能勾完成。

### 3.3 其余表

- `list`：`li_inbox`「收件箱」为默认清单，启动迁移时 seed；不可删除。
- `tag` / `todo_tag`：标签归一为小写，每条 todo ≤10 个、每个 ≤32 字符。
- `link`：跨应用引用字符串（`sdemo.<app>/<entity>/<id>`）或 `http(s)`；每条 ≤10。
- `draft` / `draft_item`：草稿区与正式表物理分开；item 带 `excerpt`、`decision`、
  `suspect_duplicate_of`。
- `outbox`：每次写操作同事务写入 `{op, entity_type, entity_id, payload_json, created_at}`，
  给 P5 同步预留；`op_log` 记录 before/after JSON 与 `reason`。
- `mcp_audit`：`tool`、参数截断摘要（≤240 字符）、item_count、result_code、
  duration_ms、request_id。
- `idempotency`：`client_request_id` → 结果 JSON，10 分钟窗口。

### 3.4 不变量

1. 任何写都在**一个** SQLite 事务里完成：实体 + 标签/链接 + FTS + outbox + op_log。
2. `revision` 冲突抛 `TODO_CONFLICT`，附 `currentRevision`，绝不静默覆盖。
3. 软删也占用一个 revision；恢复是显式动作，再 +1。
4. 只有超过 `restorable_until` 的行才会被 `PurgeExpired` 物理清理。
5. `todo` 与 `event` 各自校验、各自接口；统计只算 todo。

## 4. 并发、幂等与审计

- **乐观并发**：MCP 的 `todo_update` / `todo_complete` / `todo_reopen` / `todo_delete`
  必须带 `expectedRevision`。UI 编辑先读当前 revision，保存时带同一个值。
- **完成幂等**：已完成的条目再调 `todo_complete` 返回同一状态、不加 revision。
- **创建幂等**：`todo_create` 可带 `clientRequestId`，10 分钟内重复请求直接返回首次结果。
- **审计**：每个 `tools/call`（含失败）写 `mcp_audit`。参数只存摘要，备注全文不入审计。
- **来源注入**：MCP 层固定 `Source = Ai`，会话 id / 原文片段来自
  `params._meta["sdemo/sourceSessionId"] / ["sdemo/sourceExcerpt"]`；
  模型无法把一条 AI 条目伪装成 `manual`。

## 5. 检索（PL-13）

- FTS5 虚表 `todo_fts(todo_id UNINDEXED, terms, tokenize='unicode61')`，
  由迁移 SQL 创建；`todo.fts_row_id` 指向对应 rowid。
- 入库前做**应用侧 2-gram 预分词**：中文连续片段切成两字词（单字保留），
  Latin 按词；标题重复 3 次、备注 1 次，实现「标题优先」的加权。
- 查询：先 AND，无结果降级 OR，再降级单字 `LIKE`；响应里 `widened=true` 告知放宽过。
- 排序用 bm25；返回 `snippet` 而不是整段备注。
- 事件暂不参与检索（`calendar_*` 工具面留 P3）。

## 6. 日历视图

- 三种粒度：**月**（周一开头，固定 6 行 × 7 列）、**周**（7 天一行）、**日**（单列议程）。
- 区间查询 `[from, to)`：todo 按 `due_at` 落点；event 与区间相交即出现。
- 事件跨多天时**只显示在开始日**（v1 取舍，避免一个事件在多格重复；P3 可做跨列）。
- 拖拽改期：左侧「待排期」列表拖到日期格 → 更新 `dueAt`，保留原有时刻，未排期默认 09:00。
- 双击空白日期格 → 新建待办，日期预填、时刻 09:00。
- 日程与待办用不同配色芯片；逾期待办用红色；超过 3 条显示 `+N 更多`。
- 时间口径：所有格按本地日期分组，底层仍存 UTC。

## 7. 草稿与重复

- `todo_plan_from_text` 是**确定性拆分器**：按换行、编号/项目符号、句号分句，
  最多 50 条；每条的 `excerpt` 是原句。**不做自然语言日期解析**，因为应用零模型凭证；
  时间由调用方（DesktopAgent）或用户补。
- 草稿只写 `draft` / `draft_item`，`todo_accept_draft` 才写 `todo`；采纳时逐条
  `accept` / `reject`，可改标题。
- 重复：AI/meeting 来源创建时，标题归一后**完全相同** → `TODO_DUPLICATE_SUSPECT`
  并带 `existingId`；2-gram Jaccard ≥ **0.75** → 只产生 warning，由用户裁决。
  `manual` 来源不拦截（用户明确要建）。

## 8. ICS 导出

- 输出 `VCALENDAR`（`PRODID:-//SDemo//sdemo.planner 0.1.0//CN`、`X-WR-CALNAME`）。
- **待办默认导出为 `VEVENT`**（ADR-9）：Outlook 的“作为新日历打开/导入”会忽略 `VTODO`，
  导入后日历里看不到内容；用 `DTSTART` = `dueAt`、`DTEND` = `dueAt + 30min` 导出为日程。
  无截止时间的待办导出为创建当天的全天事件；完成项 `SUMMARY` 前加 `✓`，
  原始状态放在 `X-SDEMO-STATUS` / `X-SDEMO-TODO-ID` / `X-SDEMO-REVISION`。
- 需要任务语义的客户端可设 `IcsExportOptions.TodoMode = Task`，改回 `VTODO`（保留原
  `DUE` / `STATUS` / `COMPLETED` / `PERCENT-COMPLETE` 写法）。
- `VEVENT`（事件）：`UID` = `event.<id>@sdemo.planner`；`DTSTART` / `DTEND`（UTC）/
  `LOCATION` / `DESCRIPTION`。
- 转义 `, ; \` 与换行；长行按 75 octet 折叠（continuation 以空格开头）。
- **v1 不导出 RRULE**，也没有重复规则编辑；重复日程留给 P5（见 ADR-4）。
- 只读订阅入口在 P5 落地；失败不得阻塞启动，UI 需显示上次同步失败（预留）。

## 9. 桌面组件

- 320×440 DIP，`WS_EX_TOOLWINDOW`，不抢焦点，标题栏拖动；最小化拦截为 Hide。
- 内容：最多 `config.maxItems`（默认 8，夹在 3–30）条未完成待办，
  按「有截止时间优先、时间近优先、优先级高优先」排序；支持快速添加、勾完成/重开。
- 宿主指令：`show` / `hide` / `focus` / `refresh` / `themeChanged` / `configChanged`；
  宿主传 `opacity` 时由组件自绘（夹在 0.35–1.0）。
- `/v1/widgets/{id}/state` 返回 `{widgetId, visible, opacity}`，读取不碰 UI 对象。
- 「打开主窗口」：宿主拉起时主窗口不自动显示，只从组件按需打开；手动启动时两者都显示。

## 10. app 面与 MCP 传输

| 方法 | 路径 | 鉴权 | 说明 |
| --- | --- | --- | --- |
| GET | `/v1/health` | 免 | `{ok, pid, since}` |
| POST | `/v1/handshake` | Bearer | 交换版本，返回 `capabilities:["native-window"]` |
| POST | `/v1/shutdown` | Bearer | 200 后 100ms 退出 |
| POST | `/v1/widgets/sdemo.planner.widget/command` | Bearer | 组件指令 |
| GET | `/v1/widgets/sdemo.planner.widget/state` | Bearer | 组件状态 |
| POST | `/mcp` | Bearer | MCP Streamable HTTP（JSON-RPC 单发/批量） |

- 只监听 `127.0.0.1` 随机端口；token 从 `SDEMO_APP_TOKEN` 或 runtime.json 生成，
  固定时间比较；请求体 ≤64KB，超过 413。
- 错误信封 `{error:{code,message,hint}}`；成功返回裸对象或 JSON-RPC `result`。
- MCP 版本 `2025-06-18`，兼容协商 `2024-11-05` / `2025-03-26`；未实现方法 → `-32601`。
- 工具结果同时给 `content[0].text` 与 `structuredContent`；业务失败用 `isError:true`
  + `{ok:false, code, message, hint, details}`，不混用 JSON-RPC 协议错误。

## 11. 关键决策（ADR）

| # | 决策 | 理由 |
| --- | --- | --- |
| ADR-1 | todo / event 两张表、两套接口 | 可完成与有起止是两种不变量，合表会让「完成日程」变成合法操作 |
| ADR-2 | 草稿区物理独立，采纳才写正式表 | 防止模型输出直接污染长期数据；审计能回答「哪句话来的」 |
| ADR-3 | 2-gram 预分词 + FTS5 | 中文两字查询能命中，不引入分词依赖；标题加权靠重复 terms |
| ADR-4 | v1 不做 RRULE / 重复编辑 | UI 与同步复杂度高；P2 先保证单次日程与截止点正确 |
| ADR-5 | 相似重复只做提示（≥0.75），完全重复才拒绝（AI 来源） | 不能让近义词静默堆库，也不能让模型替用户决定 |
| ADR-6 | 来源由 MCP 服务端注入 | provenance 不可伪造；审批/审计都依赖真实来源 |
| ADR-7 | 回收站 7 天 + 定时清理 | 满足「删除一律软删」，又避免无限增长 |
| ADR-8 | app 面与 MCP 同端口 | 宿主只需一个 runtime.json；减少一个监听面 |
| ADR-9 | 待办默认导出为 `VEVENT`，`VTODO` 作为可选任务模式 | Outlook/多数系统日历导入会忽略 VTODO，PL-05 要求导入后能看到内容；保留 Task 模式给支持的客户端 |

## 12. P2 明确未做

- PL-08 提醒 / 系统通知（依赖宿主 notifications 能力与调度，放 P3）。
- `calendar_*` MCP 工具（P3 与 DesktopAgent 联调时加）。
- ICS 只读订阅与失败横幅（P5）。
- 事件跨多天跨格显示、RRULE、拖拽改事件时长。
- 标签管理 UI（当前只在待办编辑框里输入）、清单新建/改名 UI。
- 组件主题的完整浅色皮肤（只切换外框与标题色，卡片仍为深色）。
