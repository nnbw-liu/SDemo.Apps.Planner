# 事序 0.6.0 · Azure DevOps 接入

> 版本：0.6.0 · 2026-11-09 · 代码根 `src/SDemo.App.Planner.*`

## 目标

把 Azure DevOps Server（本地部署，例 `http://172.30.184.25:8088/Utilities`）里的
**冲刺（迭代）与任务（工作项）** 接进事序，作为**只读镜像**：

- ADO 是权威源：状态、标题、说明、优先级、迭代归属都从 ADO 拉；
- 本地手工条目**不受影响**，同步只碰 `external_source = 'azure-devops'` 的行；
- 同步按工作项 id 幂等：第二次跑不产生重复，未变化的条目只计入「未变」。

不做反向写入（不在事序改 ADO），也不做评论/附件同步。

## 连接设置（全局）

设置 → **Azure DevOps**：这里只配「连得上 ADO」的东西。

| 字段 | 说明 |
| --- | --- |
| 启用 | 关闭后项目页的同步入口与 ADO 徽标隐藏 |
| 集合地址 | 到集合为止，例 `http://172.30.184.25:8088/Utilities`（末尾斜杠会自动去掉） |
| 个人访问令牌（PAT） | 只写不读；留空表示保持不变，`ClearPat` 才清空 |

按钮：**保存**、**测试连接**（只读：认证用户 + 可见项目数）。

- 认证：`Authorization: Basic base64(":" + PAT)`；
- **api-version 固定 5.1**（ADO Server 2020：6.0/7.1 直接 400，`connectionData` 用 `5.1-preview.1`）；
- 存 `app_setting`：`ado.enabled` / `ado.collectionUrl` / `ado.pat`。

## 项目绑定与同步范围

每个本地项目自己决定「对应 ADO 的哪个项目、拉谁的任务、拉哪些迭代」——
项目页顶部的 **绑定云端项目 / 云端项目 · xxx** 打开 `AdoBindingDialog`：

| 字段 | 行为 |
| --- | --- |
| 云端项目 | **获取项目** 拉集合里的全部项目再下拉选（也可手填）；留空保存 = 解除绑定 |
| 指派人 | 空 = 全部；`@Me` = PAT 账号；也可选具体的人（**获取指派人** 按工作项数排序） |
| 迭代 | 勾选要同步的迭代；**不勾选 = 全部**。勾了只导入选中节点及其子节点，祖先自动带上（否则小迭代挂不上去） |
| 只同步未关闭 | WIQL 排除 `Closed/已关闭/Done/Resolved/已解决`，历史条目不再拉 |

按钮：**测试范围**（认证用户 + 迭代数 + 工作项数）、**立即同步**（先保存绑定再拉）、**保存**。

- 范围只影响「从 ADO 拉什么」，**不删本地已有条目**：之前同步进来的旧任务留在库里，也不会再被更新；
- 指派人 + 迭代是 AND：范围窄到没有交集时结果是 0 条；
- 迭代过滤按**工作项路径口径**（`System.IterationPath` 不含「迭代」根节点），
  选 `项目\迭代\3.0.1` 实际匹配 `项目\3.0.1`；`AdoMapper.IterationAlias` 负责换算；
- 绑定存 `app_setting` 前缀 `ado.bind.{projectId}.`：`project` / `assignedTo` / `iterations`（换行分隔）/
  `skipClosed` / `lastSyncAt` / `lastSyncMessage` / `lastSyncOk`；
- 同步**不会新建本地项目**：本地项目先存在，同步只把它链接到 ADO 项目（补 `external_*`，首次计入 `ProjectLinked`）；
- 「立即同步」与项目页「从 ADO 同步」等价，结果 toast + 绑定里留最近一次统计。

## 映射规则（`src/SDemo.App.Planner.Core/AdoMapper.cs`）

| ADO | 事序 |
| --- | --- |
| 新建 / New / Proposed / To Do | `open` |
| 活动 / Active / In Progress / Committed | `doing` |
| 已解决 / Resolved | `done` |
| 已验证 / Verified | `verified` |
| 已关闭 / Closed / Done | `closed` |
| 已删除 / Removed | `canceled`（拉取时已按 WIQL 排除，仅防御） |
| Bug / 缺陷 | `bug` |
| 调研 / Research / Spike | `research` |
| 杂务 / Chore | `chore` |
| 任务 / 用户情景 / 其它 | `task` |
| 优先级 P1…P4 | 4…1（缺失按 2） |

- 说明：`System.Description` 是 HTML，转纯文本（块级标签换行、去标签、解码实体），截断 4000 字；
- 迭代状态按起止推：未开始 `planned`、进行中 `active`、已过结束 `done`，都没日期按 `planned`；
- 迭代路径：分类节点形如 `项目\迭代\3.0.1`，工作项的 `System.IterationPath` 形如 `项目\3.0.1`
  （**不含迭代根节点**）。两边统一 `AdoMapper.NormalizePath`（去首尾 `\`），并给根节点去掉一层做别名；
- 事序只允许两层：ADO 里更深的迭代会拍平挂到最近的顶层节点（大迭代）下；根节点（「迭代」）本身导入为大迭代；
- 父项：`System.Parent` 指向的工作项不可见时（例如已删除的父项 #20516），父关系留空并进 `warnings`。

## 数据落库

- 新列：`todo` / `project` / `iteration` 各加 `external_source`(32) / `external_id`(64) /
  `external_url`(1024)，`todo` 另有 `external_rev`(int?)；三表各建 `(external_source, external_id)` 索引；
- 迁移：`20261109005314_AdoV6`；
- 幂等键：`external_source + external_id`（工作项用 ADO 工作项 id，迭代用分类节点 identifier）；项目不按 external 新建，同步只把已有本地项目链接到 ADO 项目；
- 更新走整行克隆 + `Revision + 1`（`PlannerStore.ApplyTodoWrites` 是整行覆盖，必须带全字段、保留原 tags/links）；
- 导入条目的 `Source = import`、操作者 `import`，因此「操作记录」能看出是同步写入；
- 同步失败不抛给 UI：`AdoSyncResultDto.Ok = false` + `Message`，写进该项目的 `ado.bind.{id}.lastSync*`。

## 单任务双向同步

任务详情里，来自 ADO 的任务（标题后带云朵 + `ADO` 徽标）多一行动作：

- **同步到云端**：把本地改动推回这条工作项，字段 = 标题 / 说明 / 状态 / 优先级：
  - 状态按 `AdoMapper.ToAdoStates` 给候选（如 `doing` → 活动/Active），服务端不认当前语言时自动换下一个；
  - 说明按 `ToAdoHtml` 转成 ADO 的 HTML（转义 `& <>`，换行 `<br />`）；
  - 优先级按 `ToAdoPriority` 反查（事序 4..1 → ADO P1..P4）；
  - 并发用 JSON Patch 的 `test /rev` 守卫：本地记的 rev 与服务端不一致时返回 409，提示「先「从云端同步」再推送」，不会静默覆盖别人的改动；
  - 成功后被推送的新 rev 写回本地，徽标旁的 `rev` 立即更新。
- **从云端同步**：只重拉这一条工作项，覆盖标题 / 说明 / 状态 / 优先级 / 迭代归属；工作项已被删除或不可见时给提示，不动本地数据。

两个按钮都要求：任务有 `external_id`、连接已启用、项目已绑定、PAT 已保存；任一缺失直接给中文说明，不发请求。

## 界面

- 设置「Azure DevOps」分区只留连接：启用 / 集合地址 / PAT / 保存 / 测试连接；
- 项目列表不再有全局同步按钮，ADO 链接过的项目卡片带 `ADO` 标记；
- 项目详情：「绑定云端项目 / 云端项目 · xxx」打开绑定对话框（云端项目 / 指派人 / 迭代 / 跳过已关闭 / 测试范围 / 立即同步）；对话框里的迭代是主题化的勾选列表（按层级缩进、选中态用强调色、右侧显示起止日期），「只同步未关闭」是开关样式；「保存并关闭」保存后直接收起对话框；
- 任务行标题后云朵 + `ADO` 徽标（悬浮提示显示工作项 id）；桌面任务组件与软件内的列表共用 `TaskRow` / `AdoBadge`；任务详情里徽标可点开 ADO 工作项页，另有「同步到云端 / 从云端同步」。
- 任务详情 Footer 提供「删除任务」（点一次变「确认删除」，移入 7 天回收站）；删除项目 / 迭代的确认弹窗带「同时删除…任务」复选框：不勾选任务保留并脱离归属，勾选则一并移入回收站（删迭代含全部层级子迭代）。

## 代码位置

```
Contracts/Ado.cs                 连接/快照/结果 DTO
Core/AdoClient.cs                HTTP：项目、迭代树、WIQL、批量取工作项、测连接
Core/AdoMapper.cs                纯映射 + HTML→文本 + 路径/URL 规整
Core/AdoImportService.cs         连接/绑定设置 → 拉快照 → 落库 → 记录结果
Core/PlannerService.Ado.cs       连接设置 + 项目绑定读写 + ApplyAdoSnapshot 幂等落库
Storage/PlannerStore.Ado.cs      GetXxxByExternal / UpdateTodoExternal
```

## 验证

- `dotnet build SDemo.Planner.slnf -c Release`：0 警告 0 错误；
- `dotnet test tests/SDemo.Planner.Tests`：101 通过 0 失败（`AdoTests`：状态/类型/优先级/迭代状态/HTML 映射、PAT 只写不读、首次链接并导入、幂等、父项缺失告警、绑定往返、未绑定同步报错、WIQL 生成、别名转义、迭代过滤带祖先、推送映射、JSON Patch 结构、rev 冲突）；
- 早期实机（2026-11-09，0.6 首版：同步会自动新建本地项目；现行模型见下面「项目绑定实机」）：
  - 获取项目：6 个（dynabookAccessibilitySettingsLauncher / dynabookAssistant / dynabookAssistant-Agent / dynabookServiceStation / dynabookSettings / OmoidePhotoViewer）；
  - 测试连接：68 个工作项、5 个迭代（1 大 + 4 小）；
  - 首次同步：项目新建、迭代 5 新建、任务 68 新建、0 警告；
  - 第二次同步：0 新建 / 0 更新 / 68 未变；
  - 状态分布：新建 13、活动 11、已关闭 39、已解决 5（已删除 2 条按 WIQL 排除）；
  - 类型：任务 45+、Bug 7；全部挂在 `3.0.1`（2026-10-01 ~ 2026-11-30，状态 `active`）。
- 同步范围实机（2026-11-09，同 PAT）：
  - 获取迭代：5 个（根「迭代」Depth 0；「迭代 1/2/3」「3.0.1」Depth 1）；
  - 获取指派人：2 个——`Xie, Huangteng` 20 条、`Liu, Qihui` 1 条（#104 挂在项目根迭代）；
  - `3.0.1` 全量 67 条（新建 12 / 已关闭 39 / 活动 11 / 已解决 5），勾「只同步未关闭」后 23 条；
  - `指派人=Xie, Huangteng` + `3.0.1` + 只同步未关闭：首次 迭代 2 新建 / 任务 11 新建（进行中 9、待办 2），第二次 0 新建 / 0 更新 / 11 未变；
  - `@Me` + `3.0.1` 为 0 条（该账号唯一工作项不在 3.0.1 下），属预期；
  - 范围外条目不会被删除，之前同步进来的旧任务原样保留。
- 项目绑定实机（2026-11-09，重构后，PAT 用户 `Liu, Qihui`）：
  - 获取项目：6 个；连接测试：`连接成功：Liu, Qihui，可见 6 个项目。`
  - 新建本地项目 → 绑定 `dynabookAssistant-Agent` + 指派人 `Xie, Huangteng` + 迭代 `...\迭代\3.0.1` + 只同步未关闭；
  - 测试范围：11 个工作项、2 个迭代；
  - 首次同步：`ProjectLinked=true`、迭代 2 新建、任务 11 新建；
  - 第二次同步：0 新建 / 0 更新 / 11 未变（`ProjectLinked=false`）；
  - 本地项目名保持「DA 助手（ADO 镜像）」，只补了 `external_*` 与空描述。
