# 事序 0.7.0 · 直接拉取 ADO 项目 + 附件系统

> 版本：0.7.0 · 2026-11-13 · 代码根 `src/SDemo.App.Planner.*`

## 目标

0.6 的 ADO 接入只支持「本地先建项目 → 再绑定云端项目 → 再同步」。
0.7 补两件事：

1. **直接拉取云端项目**：不要求本地先建项目，按选定的迭代与指派人把整个 ADO 项目拉下来，
   自动建本地项目、写绑定、导入迭代与工作项。
2. **附件系统**：任务 / 项目 / 迭代都能挂文件；目录可在系统设置里改，改完能一键迁移；
   单文件上限 100 MB。

## 一、直接拉取 ADO 项目

### 入口

项目页筛选条右侧的 **拉取 ADO 项目**（`Icon download`）。只有设置里启用了 Azure DevOps 才显示。
点击打开 `AdoPullDialog`（`src/SDemo.App.Planner.Web/Components/AdoPullDialog.razor`）。

### 对话框

| 控件 | 行为 |
| --- | --- |
| 云端项目 | 打开即拉 `_apis/projects`；下拉选，可「刷新项目」 |
| 指派人 | 选完项目自动拉指派人；空 = 全部，`@Me` = PAT 账号，其余按具体的人过滤 |
| 迭代 | 主题化勾选树（层级缩进 + 起止日期）；**不勾 = 全部**；勾了自动带子孙与祖先 |
| 只同步未关闭 | 打开后 WIQL 排除 `Closed/已关闭/Done/Resolved/已解决` |
| 开始拉取 | 按范围抓快照并落库 |

成功后对话框显示统计（新建/更新/未变、警告），并提供 **打开项目** 直接跳到
`/projects/{id}`。失败保留对话框并显示原因。

### 行为

- `AdoImportService.PullProjectAsync(new AdoPullProjectInput{ AdoProject, AssignedTo, IterationPaths, SkipClosed })`
  是唯一入口；内部复用 0.6 的 `AdoClient.FetchAsync` 与 `PlannerService.ApplyAdoSnapshot`。
- **建项目规则**（`PlannerService.EnsureAdoProject`）：
  - 已有 `external_source = 'azure-devops'` + 同 `external_id` 的项目 → 直接复用，`ProjectCreated = false`；
  - 本地存在同名但未绑定的项目 → 拒绝并提示「本地已有同名项目…先绑定云端项目，或把云端项目改名后再拉取」，
    避免一个云端项目出现两份本地副本；
  - 否则新建 `ProjectRow`（`ExternalSource/Id/Url` 一次写好，`Actor = import`），`ProjectCreated = true`。
- 拉取后自动写入 **项目级绑定**（`ado.bind.{projectId}.*`），项目页后续「从 ADO 同步」直接可用。
- 重复拉同一个项目是幂等的：项目复用、迭代/工作项按 `external_source + external_id` upsert，
  第二次只计入「未变」。
- `AdoSyncResultDto.ProjectCreated` 是 0.7 新增字段，供调用方区分「新建」与「复用」。

## 二、附件系统

### 能挂在哪

`AttachmentOwnerTypes`：`todo` / `project` / `iteration`。界面入口：

- 任务详情：`TaskDetailDialog` 的 **附件** 段（标签之后、操作记录之前）；
- 完成任务：`CompleteTaskDialog` 的 **完成附件** 段（产物 / 链接之后），完成时顺手把产物、截图、构建结果传上来；
- 项目详情：项目 hero 之后的 **项目附件**；
- 迭代编辑：只在**编辑已有迭代**时出现 **迭代附件**（新建迭代还没有 id，不能先挂附件）。

组件是 `src/SDemo.App.Planner.Web/Components/AttachmentPanel.razor`，
桌面端用 `IPlatformFiles` 打开文件对话框 / 打开文件 / 在资源管理器中定位；
Hub 组件（`/widgets/*`）注册 `NullPlatformFiles`，不弹桌面对话框，只读列表。

### 目录与迁移

| 项 | 值 |
| --- | --- |
| 默认目录 | `%APPDATA%\SDemo\Apps\sdemo.planner\attachments`（`PlannerPaths.AttachmentsDirectory`） |
| 设置键 | `attachments.rootDir`、`attachments.previousRootDir` |
| 文件布局 | `{root}\{ownerType}\{ownerId}\at_{20位id}{扩展名}` |
| 单文件上限 | `PlannerConstants.MaxAttachmentBytes` = 100 MB |

设置 → **附件存储**：

- 输入框 + **浏览…** 选目录，**保存**（只改目录）、**保存并迁移**、**一键迁移**、**恢复默认目录**；
- 统计：文件数、占用大小、单文件上限；旧目录仍可迁移时显示提示与「一键迁移」。

迁移语义（`PlannerService.MigrateAttachments`）：

- 源目录 = `attachments.previousRootDir`，目标 = `attachments.rootDir`；
- 按 DB 登记的相对路径逐个复制：目标已存在 → 跳过；源缺失 → 跳过并计入失败；
- **复制成功后才删除源文件**，不先删；
- 全部无失败时把 `previousRootDir` 指回当前目录（`NeedsMigration` 变 false）；
- 有失败时保留 previous，便于修复后重试。

### 行为细节

- 添加：校验宿主对象存在 → 文件存在 → `<= 100 MB` → 复制到 `.part` 再 `File.Move`，计算 SHA-256，
  写 `attachment` 表（`op_log` 记 `attachment/create`）；DB 写失败会回滚已复制的文件。
- 打开：`ResolveAttachmentPath(id)` 只返回当前目录下**真实存在**的文件；不在时 UI 提示先去迁移。
- 删除：软删行 + 立即删文件；回收站 `PurgeExpired` 在 7 天后硬删行，同时清理
  「宿主已删」的孤儿附件（任务 / 项目 / 迭代软删后超过保留期）。
- 文件名去掉非法字符、最长 180 字符；相对路径挡 `..` 越界；owner id 只允许 `[A-Za-z0-9_-]{1,64}`。
- 设置里改目录只写 `app_setting`（同 0.6 的设置口径，不写 outbox/op_log）。

## 数据与迁移

新增 EF 迁移 `AttachmentsV7`（`src/SDemo.App.Planner.Storage/Migrations/20261113005001_AttachmentsV7.cs`）：

```sql
CREATE TABLE attachment (
    id TEXT NOT NULL CONSTRAINT PK_attachment PRIMARY KEY,
    owner_type TEXT NOT NULL,
    owner_id TEXT NOT NULL,
    file_name TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    extension TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    sha256 TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT NULL
);
CREATE INDEX IX_attachment_deleted_at ON attachment (deleted_at);
CREATE INDEX IX_attachment_owner_type_owner_id ON attachment (owner_type, owner_id);
```

生成命令（**用 Storage 作 startup project，且 `--configuration Release`**）：

```powershell
dotnet ef migrations add AttachmentsV7 `
  --project src\SDemo.App.Planner.Storage `
  --startup-project src\SDemo.App.Planner.Storage `
  --configuration Release
```

> 用 Host 作 startup project 会失败：EF Design 包只在 Storage 里；且 Debug 输出常被正在运行的
> 事序进程锁住（`MSB3027/MSB3021 文件被“事序”锁定`）。

## 测试

- `tests/SDemo.Planner.Tests/AttachmentTests.cs`：收集/列出/打开/删除、100 MB 上限、
  宿主不存在、迭代附件、保存目录、保存并迁移、相对路径拒绝。
- `tests/SDemo.Planner.Tests/AdoPullTests.cs`：路由式假 ADO（项目 / 迭代树 / WIQL / 工作项），
  验证新建项目 + 绑定 + 迭代 + 工作项、同名未绑定项目拒绝、重复拉取幂等、未启用连接直接失败。

## 未做

- ADO 侧附件同步（ADO 附件仍是 ADO 的，不进事序附件目录）；
- 附件 MCP 工具（`attachment_*`）；
- 附件在线预览 / 缩略图；只做打开与在资源管理器定位。
