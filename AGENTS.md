# AGENTS · sdemo.planner 子仓

> 路由文件。跨产品文档在 `../../docs/`（相对本仓根）。

## 职责

待办（可完成）与日程（有起止）同库不同表；AI 草稿审阅后入库；ICS 导出与只读订阅；桌面组件 ≤8 条待办。

## 现状（2026-10-31）

🚧 未开工：只有文档，`src/` 还不存在。需求计划见 [docs/requirements.md](docs/requirements.md)。

## 开工先做三件事

1. 按五件套建工程：`src/SDemo.App.Planner.{Contracts,Storage,Core,Mcp,Host}`。
2. 建 `SDemo.Planner.slnf`；在父仓 `workspaces/planner.json` 把 `status` 改 `active`。
3. 补 `planner-design.md`（日历视图 / ICS 映射 / RRULE / 组件布局），再把本文件换成真实说明。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求与验收 | [docs/requirements.md](docs/requirements.md) |
| MCP 工具面与审批 | [docs/planner-mcp-surface.md](docs/planner-mcp-surface.md) |
| 跨应用引用 / 统一搜索 / 审批矩阵 | [../../docs/integration.md](../../docs/integration.md) |
| 组件清单字段 | [../../docs/contracts/widget-contract.md](../../docs/contracts/widget-contract.md) |
| 宿主怎么拉起本应用 | [../../docs/contracts/host-protocol.md](../../docs/contracts/host-protocol.md) |

## 命令（建好工程后启用）

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test  tests -c Release
```

## 铁律

1. **零模型凭证**：不引入 `IChatClient` / `ApiKey`；AI 草稿由 DesktopAgent 生成。
2. **草稿不入库**：`todo_create_draft` 只落草稿区；`todo_accept_draft` 才写正式表。
3. **同库不同表**：`todo` 与 `event` 两套实体、两套接口，不要做万能实体。
4. **删除软删**：`deleted_at` + 7 天回收站；MCP 写操作落审计。
5. **不直连其它应用**：只存 `sdemo.kb/doc/...` 这类 URI 字符串。

## 常见坑

- 报警与日历视图的 `dueAt` 时区必须统一（存 UTC，显示本地）。
- ICS 只读订阅拉取失败不能阻塞启动；要能显示"上次同步失败"。
- `outbox` 是给 P5 同步预留的，写操作必须同时落 op-log，不要事后补。

## 不要读

`obj/`、`bin/`、`TestResults/`；`docs/requirements.md` 只通读一次。
