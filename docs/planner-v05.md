# 事序 v0.5 — 项目分组、MCP 开关、迭代说明与状态排序

> 版本：0.5.0 · 2026-11-08 · 代码根 `src/SDemo.App.Planner.*`

## 一、项目分组

一个项目可以同时属于多个分组，分组是项目级标签，不参与迭代层级。

数据：

- `project_group`：`id / name / color / sort / revision / created_at / updated_at / deleted_at`
- `project_group_member`：复合主键 `(project_group_id, project_id)`，项目删除或分组删除时清理成员行
- 迁移：`20261108023313_ProjectGroupsAndSettingsV5`

契约：

- `ProjectGroupDto`、`ProjectGroupInput`、`ProjectGroupPatch`
- `ProjectDto.Groups`；`ProjectInput.GroupIds` / `ProjectPatch.GroupIds`
  - `null` = 本次不改分组；空数组 = 清空分组
  - 写入时只保留存在且未删除的分组，重复 id 去重

服务：

- `ListProjectGroups()`：带 `ProjectCount`
- `CreateProjectGroup / UpdateProjectGroup / DeleteProjectGroup`
- 项目列表、项目详情、项目编辑都返回并展示分组

UI：

- 项目页新增分组筛选条与「管理分组」入口（`ProjectGroupManager`）
- 项目卡与项目详情 hero 显示分组 pill
- 项目编辑弹窗可多选分组

## 二、MCP 总开关与逐工具开关

持久化在 `app_setting` 键值表，Host 与 Web 共用：

- `mcp.enabled`：总开关，缺省 `true`
- `mcp.tool.<name>`：逐工具开关，缺省 `true`

行为：

- 总开关关闭：`tools/list` 返回空数组；任何 `tools/call` 返回 `MCP_DISABLED` 并落审计
- 单工具关闭：不出现在 `tools/list`；调用返回 `MCP_TOOL_DISABLED` 并落审计
- 设置页「MCP 服务」：总开关 + 13 个工具列表（名称、说明、风险、只读标记、独立开关）

契约：

- `McpToolDescriptor` / `McpToolDescriptors.All`（设置页只读镜像）
- `McpSettingsDto` / `McpToolSettingDto`
- `ErrorCodes.McpDisabled` / `ErrorCodes.McpToolDisabled`

测试会校验描述表与 `McpToolCatalog.Tools` 的名称、风险、只读标记一致。

## 三、迭代弹窗

- 弹窗加宽为 `modal-lg`
- 表单顺序：层级 / 所属大迭代 → 名称 → 开始 / 结束 → 状态 → 目标说明
- 说明区置底，`field-sizing: content` 自适应高度（`min-height: 180px`，`max-height: 42vh`），可手动纵向拉伸
- 弹窗正文保持 `overflow-y: auto`，内容超出时在正文内滚动

## 四、任务说明一键复制

- 任务详情「任务说明」标题旁新增复制图标（紧贴标题，不靠右）
- `plannerClipboard.copy(text)`：优先 `navigator.clipboard`，不可用时退回 `textarea + execCommand`
- 成功/失败走 Toast 提示

新建/编辑任务弹窗的「备注」区使用 `.task-notes`：

- 最小高度 140px，打开即有足够书写空间
- `field-sizing: content` 随输入长高，最高 56vh，超过后在输入框内部滚动
- 仍保留手动纵向拉伸

## 五、任务列表默认按状态排序

- `TodoSort.Status`（JSON `status`）
- 排序顺序：`doing` → `open` → `done` → `verified` → `closed` → `canceled`
- 任务页默认选中「状态」；项目详情的直属任务与 Backlog 使用同一顺序，再按优先级、截止时间、创建时间收尾

## 六、任务派生 Bug

- 任务详情新增「关联 Bug」段：显示该任务下所有 Bug，并可直接「新建 Bug」
- 一个任务可以派生多个 Bug；每个 Bug 通过 `ParentTodoId` 挂到来源任务，层级仍限制为一层
- 新建 Bug 弹窗（`BugEditor`）填写标题、复现说明、优先级；项目 / 迭代沿用来源任务，创建后立即入库
- Bug 详情在「属性」区显示「来源任务」，可点击回到原任务
- 查询：`TodoQuery.ParentTodoId` / `TodoFilter.ParentTodoId`，按父任务过滤子项

## 验证

```powershell
dotnet build SDemo.Planner.slnf -c Release
dotnet test tests/SDemo.Planner.Tests/SDemo.Planner.Tests.csproj -c Release
pwsh tools/smoke-mcp.ps1
```

结果：构建 0 警告 0 错误；58 个测试全部通过；MCP 冒烟 13 工具、写读、草稿、审计与优雅退出全部通过。
