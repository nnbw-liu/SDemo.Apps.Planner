# AGENTS · sdemo.planner 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

待办（可完成）与日程（有起止）同库不同表；AI 草稿审阅后入库；ICS 导出与只读订阅；桌面组件 ≤8 条待办。

## 现状（2026-11-01）

✅ **P2 已实现**：PL-01~05、PL-07、PL-09~12 + PL-13（搜索）。
- 五件套：`Contracts` / `Storage` / `Core` / `Mcp` / `Host`，`SDemo.Planner.slnf`。
- 真实 EF migrations（含 FTS5 虚表）+ 软删回收站 + outbox/op-log + MCP 审计。
- 主窗口：待办 CRUD、日/周/月日历 + 拖拽改期、回收站、草稿审阅、ICS 导出。
- 13 个 `todo_*` MCP 工具；app 面 `/v1/health|handshake|shutdown|widgets/...`。
- 宿主联调通过：发现 → sha256 → ensure → runtime → MCP 13 工具 → 组件显示/指令 → 主窗口按需打开。
- 组件功能冒烟通过：UIA 输入 → 添加 → 勾完成，MCP 断言落库结果（`tools/smoke-widget.ps1`）。
- 日历拖拽冒烟通过：真实鼠标事件把未排期卡片拖到某日，`dueAt` 断言（`tools/smoke-calendar-drag.ps1`）。
- 无 UI 导出：`SDemo.Planner.exe --db=<db> --export-ics=<path>`。
- 验证记录：[docs/p2-verification.md](docs/p2-verification.md)；设计：[docs/planner-design.md](docs/planner-design.md)。

P3 再做：`calendar_*` 工具、提醒（PL-08）、事件跨天显示；P5：outbox 合并、ICS 只读订阅。

## 目录

```
src/SDemo.App.Planner.Contracts  DTO、输入、常量、错误码、JSON/时间口径
src/SDemo.App.Planner.Storage    EF 实体 + migrations + FTS5 + 事务写入
src/SDemo.App.Planner.Core       服务门面、校验、ICS、草稿拆分、相似度
src/SDemo.App.Planner.Mcp        JSON-RPC、工具目录、风险元数据、审计
src/SDemo.App.Planner.Host       WPF（MainWindow / WidgetWindow / 对话框）+ AppServer
tests/SDemo.Planner.Tests        xunit（存储 / 服务 / MCP）
deploy/                          app.json + widget.json
tools/                           install-planner.ps1 / smoke-mcp.ps1 / smoke-widget.ps1 / smoke-calendar-drag.ps1 / accept-host.ps1
```

## 命令

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools\smoke-mcp.ps1          # headless + 真实 HTTP 调 13 个工具
pwsh tools\smoke-widget.ps1       # 组件 UIA：输入/添加/勾完成（临时库）
pwsh tools\smoke-calendar-drag.ps1 # 月历真实指针拖拽改期（临时库）
pwsh tools\accept-host.ps1        # 真宿主：发现/ensure/MCP/组件/主窗口（需 Shell 已构建）
pwsh tools\install-planner.ps1    # 发布到 %APPDATA%\...\apps 并重算 sha256
```

本地调试：

```powershell
dotnet run --project src\SDemo.App.Planner.Host -- --db=%TEMP%\planner-dev.db
# headless（供脚本/MCP 调试）：
dotnet run --project src\SDemo.App.Planner.Host -- --headless --instance=dev --db=%TEMP%\planner-dev.db
```

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| 数据模型 / 日历 / ICS / 决策 | [docs/planner-design.md](docs/planner-design.md) |
| 已跑过的门禁 | [docs/p2-verification.md](docs/p2-verification.md) |
| MCP 工具面与审批 | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 跨应用引用 / 统一搜索 / 审批矩阵 | [../../docs/integration.md](../../docs/integration.md) |
| 组件清单字段 | [../../docs/contracts/widget-contract.md](../../docs/contracts/widget-contract.md) |
| 宿主怎么拉起本应用 | [../../docs/contracts/host-protocol.md](../../docs/contracts/host-protocol.md) |

## 铁律

1. **零模型凭证**：不引入 `IChatClient` / `ApiKey`；AI 草稿由 DesktopAgent 生成。
2. **草稿不入库**：`todo_plan_from_text` 只落草稿区；`todo_accept_draft` 才写正式表。
3. **同库不同表**：`todo` 与 `event` 两套实体、两套接口，不要做万能实体。
4. **删除软删**：`deleted_at` + 7 天回收站；MCP 写操作落审计。
5. **不直连其它应用**：只存 `sdemo.kb/doc/...` 这类 URI 字符串。
6. **契约优先**：常量（上限、阈值、错误码）在 `Contracts`，改它等于改契约，
   同步改 `docs/` 与测试。

## 常见坑

- 所有时间存 UTC，显示转本地；输入必须带时区偏移（`IsoTime.TryParseUtc`）。
- 每次写都要 `revision + 1`（含删除/恢复）；MCP 写工具必须带 `expectedRevision`。
- FTS 是迁移 SQL 建的虚表，`todo_fts` 的 rowid 存在 `todo.fts_row_id`；
  Entity 里没有 `todo_fts` 的 DbSet，别用 `EnsureCreated`。
- 相似重复阈值 0.75；完全重复只拒绝 AI/meeting 来源，manual 不拦。
- `outbox` + `op_log` 与实体写在同一事务，别事后补。
- 宿主拉起时不要自动弹主窗口；只显示组件，主窗口从组件按需打开。
- 安装目录是 `%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.planner`（不是 `%LOCALAPPDATA%`），
  宿主只扫 APPDATA；`runtime` 与日志仍在 `%LOCALAPPDATA%`。
- 组件指令路径是 `/v1/widgets/{id}/command`（4 段）；改路由后跑一遍 `accept-host.ps1`。

## 不要读

`obj/`、`bin/`、`TestResults/`；`docs/requirements.md` 只通读一次。
