# 事序 0.9.0 · ADO 上传 / 关联 / 只同步状态

> 版本：0.9.0 · 2026-11-19 · 代码根 `src/SDemo.App.Planner.*`
>
> 状态：代码 + 单元测试完成。当前环境连不上 ADO，**尚未实机验证**；
> 恢复连接后按文末「待验证」逐条跑一遍。

## 目标

v0.6 的 ADO 是只读镜像，单任务「同步到云端」会推标题 / 说明 / 状态 / 优先级，
并带 `test /rev` 守卫。只要云端被人改过一次，本地记的 rev 就过期，推送永远 409，
于是「云端改过的项目就再也同步不回去」。0.9 补三个出口：

1. **本地新建任务可直接上传到 ADO**：新建任务弹窗里勾选「同时上传到 Azure DevOps」；
2. **云端改过也能把状态推回去**：任务详情新增「仅同步状态」——先读最新 rev，
   再只 PATCH `System.State`，不碰云端标题 / 说明 / 优先级；
3. **本地任务可指派已有云端工作项**：任务详情输入 ADO 编号，「关联已有」只记
   `external_*`，不改本地标题 / 内容 / 项目；之后就能用「仅同步状态」。

补充：详情里对「项目已绑定但任务还没关联」的情况，也提供「上传为新工作项」，
不必回编辑弹窗。

## 一、契约与客户端

### `Contracts/Ado.cs`

`AdoWorkItemDto` 新增 `ProjectName`（`System.TeamProject`），单条读取工作项时用来
校验「这个编号是不是属于本地项目绑定的云端项目」。列表映射（`MapWorkItem`）同样填充。

### `Core/AdoMapper.cs`

```csharp
public static IReadOnlyList<string> ToAdoWorkItemTypes(string? storageKind) => storageKind switch
{
    "bug" => ["Bug", "缺陷"],
    _     => ["任务", "Task"],
};
```

内部部署的流程模板可能是中文，所以类型按候选顺序试，第一个被 ADO 接受的为准。

### `Core/AdoClient.cs`

| 方法 | 行为 |
| --- | --- |
| `CreateWorkItemAsync(collectionUrl, pat, project, workItemTypes, title, notes, plannStatus, priority, ct)` | `POST {root}/{项目}/_apis/wit/workitems/${Uri.EscapeDataString(类型)}?api-version=5.1`；JSON Patch `add` 标题 / 说明 / 优先级 / 可选状态；类型候选 × 状态候选 + 「不带状态」逐个试；400/404 继续，其它状态抛 `AdoUnavailable` |
| `UpdateWorkItemStatusAsync(collectionUrl, pat, id, plannStatus, expectedRev, ct)` | `PATCH` 只发两个 op：`test /rev` + `replace /fields/System.State`；409 返回「刚刚又被改过，再点一次」；成功解析新 rev 与项目名 |

创建返回的 `ExternalId` / `ExternalRev` / `ExternalUrl` 来自响应里的 `id` / `rev` /
`System.TeamProject`；没解析出 id 视为失败（提示去云端确认，避免本地记错编号）。

### `Core/PlannerService.Ado.cs`

新增 `GetTodoByExternal(source, externalId)`：按外部编号查本地任务（关联查重用）。

### `Core/AdoImportService.cs`

| 方法 | 行为 |
| --- | --- |
| `UploadTodoAsync(todoId, workItemType = null, ct)` | 前置校验 → 已关联则拒绝 → `CreateWorkItemAsync` → 成功 `MarkAdoPushed` |
| `LinkTodoAsync(todoId, externalId, ct)` | 前置校验 → 编号解析（允许 `#` 前缀）→ 查重 → GET 工作项 → 校验云端项目 → `MarkAdoPushed`（ChangedFields 为空） |
| `PushStatusAsync(todoId, ct)` | `TryResolveTodo` → GET 最新 rev → `UpdateWorkItemStatusAsync`；409 时最多重读一次 |
| `TryResolveTodoProject(...)` | 私有前置校验：任务存在、有 `ProjectId`、项目已绑定、连接启用、集合地址 + PAT 非空；**不要求任务已来自 ADO** |

边界：

- 上传 / 关联都只写 `external_*`（`MarkAdoPushed` → `UpdateTodoExternal`），
  **不涨本地 `revision`、不写 `op_log`**；本地标题 / 说明 / 状态不动。
- 上传不写 `System.AssignedTo` 与迭代路径（`IterationRow` 没持久化 ADO 路径）。
- 同一 ADO 编号只能被一个本地任务占用；已关联的任务不能再次上传或改关联
  （目前没有「取消关联」入口）。
- 上传失败不影响本地任务：新建流程先落库，再上传，失败只改 Toast 文案。

## 二、界面

### 新建任务（`TaskEditor`）

- 注入 `AdoImportService`；项目切换时刷新 `Ado.GetBinding(projectId).Bound`。
- 只有「新建 + 当前项目已绑定」才显示勾选项「同时上传到 Azure DevOps」，
  并说明「上传失败不影响本地任务」。
- 保存后取 `TodoBatchResult.Items.First()`，勾选则 `UploadTodoAsync`：
  - 成功：`任务已加入，并上传到 ADO #123`；
  - 失败：`任务已加入，但上传 ADO 失败：…`。
- 编辑已有任务时不显示该选项（避免把编辑误当新建）。

### 任务详情（`TaskDetailDialog`）

新增独立段落「云端工作项」，出现条件：项目已绑定 **或** 任务已关联 ADO。
旧的顶部 `.ado-task-actions` 移进这个段落。

- 已关联：`ADO #id` 链接 + `rev` 提示 + 三个按钮
  - **同步到云端**：沿用 v0.6 全量推送（标题 / 说明 / 状态 / 优先级，`test /rev`）；
  - **仅同步状态**：只推状态，云端改过标题也不冲突；
  - **从云端同步**：沿用 v0.6 拉取覆盖。
- 未关联：编号输入框 + **关联已有** + **上传为新工作项**；
  说明「关联只记云端编号，不改本地内容；上传会在『云端项目』里按任务类型新建」。
- 所有操作成功后重取任务并 `Reload()`，刷新 badge / rev；失败只 Toast，不关弹窗。

### 样式（`Host/wwwroot/css/app.css`）

`.ado-task-actions` 去掉顶部虚线（现在是段内工具条）；新增
`.ado-task-rev`、`.ado-task-note`、`.ado-link-form`、`.ado-link-input`、
`.ado-upload-toggle`（浅色 / 深色都走 `--line` / `--faint` / `--text`）。

## 三、测试

`tests/SDemo.Planner.Tests/AdoWriteTests.cs`（10 个，全部通过）：

| 测试 | 覆盖 |
| --- | --- |
| `CreateWorkItem_PostsAddOpsAndParsesNewId` | POST 地址 / `add` 字段 / 无 `test /rev` / 解析 id+rev+URL |
| `CreateWorkItem_FallsBackThroughStatesAndTypes` | 类型 404 → 下一类型；状态 400 → 下一状态；都不认 → 不带 `System.State` |
| `UpdateStatus_SendsOnlyRevisionGuardAndState` | 只发 2 个 op，不含标题 / 说明 / 优先级 |
| `UploadTodo_LinksExternalFieldsWithoutTouchingLocalTitle` | 上传只写 `external_*`，标题和 revision 不变 |
| `UploadTodo_RejectsAlreadyLinkedTask` | 已关联拒绝重复上传 |
| `LinkTodo_OnlyWritesExternalFields` | 只关联，本地标题 / 类型 / 项目不变 |
| `LinkTodo_RejectsWorkItemFromAnotherCloudProject` | 云端项目与绑定不一致时拒绝 |
| `LinkTodo_RejectsDuplicateLocalOwner` | 同一编号不能被第二个本地任务占用 |
| `PushStatus_ReadsLatestRevisionThenPatchesState` | 先 GET 再 PATCH，写回新 rev，本地标题不被云端覆盖 |
| `ToAdoWorkItemTypes_MapsLocalKinds` | Bug / 任务 / null 的类型候选 |

全量：`dotnet test tests\SDemo.Planner.Tests\SDemo.Planner.Tests.csproj -c Release`
→ **136 通过，0 失败**；`dotnet build SDemo.Planner.slnf -c Release` → 0 警告 0 错误。

## 四、版本

- `Contracts/PlannerConstants.cs`：`AppVersion = 0.9.0`；
- `src/SDemo.App.Planner.Host/SDemo.App.Planner.Host.csproj`：`Version` / `FileVersion` 0.9.0；
- `deploy/app.json`：`version = 0.9.0`。

## 五、待验证（ADO 恢复连接后）

1. **上传**：在中文模板项目里新建任务并勾选上传，确认
   - `任务` / `Task` 哪个类型被接受；
   - `新建` / `New` 哪个状态被接受，或退化为云端默认状态；
   - 创建出来的工作项 `AreaPath` / `IterationPath` 是否是项目默认值（当前不指定）。
2. **关联**：拿一条已有 Bug 编号，确认「关联已有」后 badge / rev 正确，
   本地标题保持不变；再试另一云端项目的编号，确认被拒绝。
3. **仅同步状态**：在 ADO 里改标题但不改状态，本地改状态后点「仅同步状态」，
   确认云端状态变化且标题保持 ADO 上的新值；再在 ADO 里连续改两次，确认重试路径。
4. **冲突提示**：云端改过后点「同步到云端」，确认仍是 409 文案而不是静默覆盖。
5. 任务详情在深色 / 浅色主题下的「云端工作项」段落排版。

## 六、未做

- 上传时指定指派人 / 迭代 / 父工作项 / 标签；
- 取消关联、改关联到另一个编号；
- 批量上传 / 批量只同步状态；
- 本地状态变化后自动回推（当前仍是手动按钮）；
- ADO 评论 / 附件同步、`ado_*` MCP 工具（维持 v0.7 的「未做」）。
