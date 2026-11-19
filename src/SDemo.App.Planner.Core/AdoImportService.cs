using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// ADO 同步的编排层：<see cref="AdoClient"/> 只负责 HTTP，<see cref="PlannerService"/> 只负责落库，
/// 这里把「读连接设置 / 项目绑定 → 拉快照 → 幂等落库 → 记录上次结果」串起来，Web 只调这一个门面。
/// </summary>
public sealed class AdoImportService(PlannerService planner, AdoClient client)
{
    /// <summary>当前连接设置（不含 PAT 明文）。</summary>
    public AdoConnectionDto GetConnection() => planner.GetAdoConnection();

    public AdoConnectionDto SaveConnection(AdoConnectionInput input) => planner.SaveAdoConnection(input);

    /// <summary>只测连接：认证用户 + 可见项目数。表单里没填 PAT 时用已保存的那份。</summary>
    public Task<AdoConnectionTestDto> TestConnectionAsync(AdoConnectionInput input, CancellationToken ct = default)
    {
        var pat = ResolvePat(input.Pat);
        return client.TestConnectionAsync(input.CollectionUrl, pat, ct);
    }

    public AdoBindingDto GetBinding(string projectId) => planner.GetAdoBinding(projectId);

    public AdoBindingDto SaveBinding(string projectId, AdoBindingInput input) => planner.SaveAdoBinding(projectId, input);

    /// <summary>按绑定范围测一次：认证用户 + 迭代数 + 工作项数。</summary>
    public Task<AdoConnectionTestDto> TestBindingAsync(string projectId, AdoBindingInput input, CancellationToken ct = default)
    {
        var connection = planner.GetAdoConnection();
        var pat = ResolvePat(null);
        var project = (input.AdoProject ?? string.Empty).Trim();
        if (project.Length == 0)
        {
            project = planner.GetAdoBinding(projectId).AdoProject;
        }

        return client.TestAsync(connection.CollectionUrl, project, pat, ToOptions(input), ct);
    }

    /// <summary>列项目里的迭代，供项目设置里勾选同步范围。</summary>
    public Task<AdoIterationsResultDto> ListIterationsAsync(
        string? collectionUrl,
        string project,
        string? pat,
        CancellationToken ct = default)
    {
        var root = ResolveCollection(collectionUrl);
        return client.ListIterationsAsync(root, project, ResolvePat(pat), ct);
    }

    /// <summary>列项目里出现过的指派人，供选「只同步某个人的任务」。</summary>
    public Task<AdoAssigneesResultDto> ListAssigneesAsync(
        string? collectionUrl,
        string project,
        string? pat,
        CancellationToken ct = default)
    {
        var root = ResolveCollection(collectionUrl);
        return client.ListAssigneesAsync(root, project, ResolvePat(pat), ct);
    }

    /// <summary>列集合里的项目，供项目设置里挑「对应的云端项目」。</summary>
    public Task<AdoProjectsResultDto> ListProjectsAsync(string? collectionUrl, string? pat, CancellationToken ct = default)
    {
        var root = ResolveCollection(collectionUrl);
        return client.ListProjectsAsync(root, ResolvePat(pat), ct);
    }

    /// <summary>用项目绑定 + 已保存的连接同步一次。失败也写「上次结果」，不抛给 UI。</summary>
    public async Task<AdoSyncResultDto> SyncProjectAsync(string projectId, CancellationToken ct = default)
    {
        var connection = planner.GetAdoConnection();
        var binding = planner.GetAdoBinding(projectId);

        AdoSyncResultDto Failure(string message) => new()
        {
            Ok = false,
            Message = message,
            ProjectId = projectId,
            SyncedAt = DateTimeOffset.UtcNow,
        };

        if (!connection.Enabled)
        {
            return Failure("Azure DevOps 接入已关闭，先到「设置 → Azure DevOps」打开。");
        }

        if (!binding.Bound)
        {
            return Failure("这个项目还没有绑定云端项目，先在项目里设置。");
        }

        var pat = planner.GetAdoPat();
        if (string.IsNullOrWhiteSpace(pat))
        {
            return Failure("还没有保存 PAT，先到「设置 → Azure DevOps」填一个。");
        }

        try
        {
            var snapshot = await client.FetchAsync(connection.CollectionUrl, binding.AdoProject, pat, ToOptions(binding), ct);
            var result = planner.ApplyAdoSnapshot(snapshot, projectId);
            planner.RecordAdoSync(projectId, result);
            return result;
        }
        catch (PlannerException ex)
        {
            var failure = Failure(ex.Message);
            planner.RecordAdoSync(projectId, failure);
            return failure;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var failure = Failure($"连不上 Azure DevOps：{ex.Message}");
            planner.RecordAdoSync(projectId, failure);
            return failure;
        }
    }

    /// <summary>
    /// 「直接拉取云端项目」：不要求本地先建项目。按选定的迭代 / 指派人拉快照，
    /// 建一个带 ADO 标识的本地项目，写入绑定，再把迭代与工作项幂等落库。
    /// </summary>
    public async Task<AdoSyncResultDto> PullProjectAsync(AdoPullProjectInput input, CancellationToken ct = default)
    {
        var connection = planner.GetAdoConnection();
        var projectName = (input.AdoProject ?? string.Empty).Trim();
        string? localProjectId = null;

        AdoSyncResultDto Failure(string message) => new()
        {
            Ok = false,
            Message = message,
            ProjectId = localProjectId,
            SyncedAt = DateTimeOffset.UtcNow,
        };

        if (!connection.Enabled)
        {
            return Failure("Azure DevOps 接入已关闭，先到「设置 → Azure DevOps」打开。");
        }

        if (projectName.Length == 0)
        {
            return Failure("先选择要拉取的云端项目。");
        }

        var pat = planner.GetAdoPat();
        if (string.IsNullOrWhiteSpace(pat))
        {
            return Failure("还没有保存 PAT，先到「设置 → Azure DevOps」填一个。");
        }

        try
        {
            var options = new AdoFetchOptions
            {
                AssignedTo = string.IsNullOrWhiteSpace(input.AssignedTo) ? null : input.AssignedTo.Trim(),
                IterationPaths = input.IterationPaths,
                SkipClosed = input.SkipClosed,
            };

            var snapshot = await client.FetchAsync(connection.CollectionUrl, projectName, pat, options, ct);
            var match = planner.EnsureAdoProject(snapshot, WriteContext.Manual());
            localProjectId = match.Project.Id;

            planner.SaveAdoBinding(localProjectId, new AdoBindingInput
            {
                AdoProject = projectName,
                AssignedTo = options.AssignedTo,
                IterationPaths = options.IterationPaths,
                SkipClosed = options.SkipClosed,
            });

            var result = planner.ApplyAdoSnapshot(snapshot, localProjectId);
            result.ProjectCreated = match.Created;
            if (match.Created)
            {
                result.Message = "已拉取新项目：" + result.Message;
            }

            planner.RecordAdoSync(localProjectId, result);
            return result;
        }
        catch (PlannerException ex)
        {
            var failure = Failure(ex.Message);
            if (localProjectId is not null)
            {
                planner.RecordAdoSync(localProjectId, failure);
            }

            return failure;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var failure = Failure($"连不上 Azure DevOps：{ex.Message}");
            if (localProjectId is not null)
            {
                planner.RecordAdoSync(localProjectId, failure);
            }

            return failure;
        }
    }

    /// <summary>单任务「从云端同步」：按 ADO 当前内容覆盖本地这一条。</summary>
    public async Task<AdoWorkItemSyncResultDto> PullTodoAsync(string todoId, CancellationToken ct = default)
    {
        if (!TryResolveTodo(todoId, out var todo, out var id, out var failure))
        {
            return failure!;
        }

        try
        {
            var item = await client.FetchWorkItemAsync(
                planner.GetAdoConnection().CollectionUrl, ResolvePat(null), id, ct);

            if (item is null)
            {
                return new AdoWorkItemSyncResultDto
                {
                    Ok = false,
                    Message = $"ADO #{id} 已删除或不可见，本地这条保持不变。",
                    TodoId = todoId,
                };
            }

            var applied = planner.ApplyAdoWorkItem(item, todo.ProjectId!);
            return new AdoWorkItemSyncResultDto
            {
                Ok = true,
                Message = $"已从 ADO #{id} 拉取：状态「{applied.Status}」，rev {item.Rev}。",
                TodoId = applied.Id,
                ExternalId = item.ExternalId,
                ExternalRev = item.Rev,
                ExternalUrl = item.Url,
                ChangedFields = ["标题", "说明", "状态", "优先级"],
            };
        }
        catch (PlannerException ex)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = ex.Message, TodoId = todoId };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}", TodoId = todoId };
        }
    }

    /// <summary>单任务「同步到云端」：把标题 / 说明 / 状态 / 优先级推回 ADO。</summary>
    public async Task<AdoWorkItemSyncResultDto> PushTodoAsync(string todoId, CancellationToken ct = default)
    {
        if (!TryResolveTodo(todoId, out var todo, out var id, out var failure))
        {
            return failure!;
        }

        try
        {
            var result = await client.UpdateWorkItemAsync(
                planner.GetAdoConnection().CollectionUrl,
                ResolvePat(null),
                id,
                todo.Title,
                todo.Notes,
                PlannerMapper.ToStorage(todo.Status),
                todo.Priority,
                todo.ExternalRev ?? 0,
                ct);

            if (result.Ok)
            {
                planner.MarkAdoPushed(todo.Id, result.ExternalId ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    result.ExternalRev, result.ExternalUrl ?? todo.ExternalUrl);
            }

            return result with { TodoId = todoId };
        }
        catch (PlannerException ex)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = ex.Message, TodoId = todoId };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}", TodoId = todoId };
        }
    }

    /// <summary>
    /// 把本地任务上传成一条新的 ADO 工作项（新建任务时勾选「同时上传」走这里）。
    /// 只对还没关联云端编号的任务生效，避免重复创建。
    /// </summary>
    public async Task<AdoWorkItemSyncResultDto> UploadTodoAsync(
        string todoId,
        string? workItemType = null,
        CancellationToken ct = default)
    {
        if (!TryResolveTodoProject(todoId, out var todo, out var binding, out var collectionUrl, out var pat, out var failure))
        {
            return failure!;
        }

        if (todo.ExternalSource == AdoMapper.SourceName && !string.IsNullOrWhiteSpace(todo.ExternalId))
        {
            return new AdoWorkItemSyncResultDto
            {
                Ok = false,
                Message = $"这条任务已经关联了 ADO #{todo.ExternalId}，不会再新建一条；要更新云端内容请用「同步到云端」。",
                TodoId = todoId,
            };
        }

        try
        {
            IReadOnlyList<string> types = string.IsNullOrWhiteSpace(workItemType)
                ? AdoMapper.ToAdoWorkItemTypes(PlannerMapper.ToStorage(todo.Kind))
                : [workItemType.Trim()];

            var result = await client.CreateWorkItemAsync(
                collectionUrl,
                pat,
                binding.AdoProject,
                types,
                todo.Title,
                todo.Notes,
                PlannerMapper.ToStorage(todo.Status),
                todo.Priority,
                ct);

            if (result.Ok)
            {
                planner.MarkAdoPushed(todo.Id, result.ExternalId!, result.ExternalRev, result.ExternalUrl);
            }

            return result with { TodoId = todoId };
        }
        catch (PlannerException ex)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = ex.Message, TodoId = todoId };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}", TodoId = todoId };
        }
    }

    /// <summary>
    /// 给本地任务指派一条已有的云端工作项（只记 external_*，不改本地标题 / 说明 / 状态）。
    /// 校验编号存在、云端项目与本地绑定一致、且没有别的本地任务占用同一个编号。
    /// </summary>
    public async Task<AdoWorkItemSyncResultDto> LinkTodoAsync(
        string todoId,
        string externalId,
        CancellationToken ct = default)
    {
        if (!TryResolveTodoProject(todoId, out var todo, out var binding, out var collectionUrl, out var pat, out var failure))
        {
            return failure!;
        }

        if (todo.ExternalSource == AdoMapper.SourceName && !string.IsNullOrWhiteSpace(todo.ExternalId))
        {
            return new AdoWorkItemSyncResultDto
            {
                Ok = false,
                Message = $"这条任务已经关联了 ADO #{todo.ExternalId}，没有再改关联（需要换编号请先取消关联）。",
                TodoId = todoId,
            };
        }

        var text = (externalId ?? string.Empty).Trim().TrimStart('#').Trim();
        if (!long.TryParse(text, out var id) || id <= 0)
        {
            return new AdoWorkItemSyncResultDto
            {
                Ok = false,
                Message = "请输入正确的 ADO 工作项编号，例如 12345。",
                TodoId = todoId,
            };
        }

        var idText = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var occupied = planner.GetTodoByExternal(AdoMapper.SourceName, idText);
        if (occupied is not null && occupied.Id != todo.Id)
        {
            return new AdoWorkItemSyncResultDto
            {
                Ok = false,
                Message = $"ADO #{id} 已经关联到本地任务「{occupied.Title}」，不能重复关联。",
                TodoId = todoId,
            };
        }

        try
        {
            var item = await client.FetchWorkItemAsync(collectionUrl, pat, id, ct);
            if (item is null)
            {
                return new AdoWorkItemSyncResultDto
                {
                    Ok = false,
                    Message = $"ADO #{id} 不存在或当前 PAT 看不到它。",
                    TodoId = todoId,
                };
            }

            if (!string.IsNullOrWhiteSpace(item.ProjectName)
                && !string.Equals(item.ProjectName.Trim(), binding.AdoProject.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new AdoWorkItemSyncResultDto
                {
                    Ok = false,
                    Message = $"ADO #{id} 属于云端项目「{item.ProjectName}」，和本地项目绑定的「{binding.AdoProject}」不一致。",
                    TodoId = todoId,
                };
            }

            planner.MarkAdoPushed(todo.Id, item.ExternalId, item.Rev, item.Url);
            return new AdoWorkItemSyncResultDto
            {
                Ok = true,
                Message = $"已关联 ADO #{id}「{item.Title}」（{item.Type} · {item.State}）。之后可以只把状态同步过去。",
                TodoId = todoId,
                ExternalId = item.ExternalId,
                ExternalRev = item.Rev,
                ExternalUrl = item.Url,
                ChangedFields = [],
            };
        }
        catch (PlannerException ex)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = ex.Message, TodoId = todoId };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}", TodoId = todoId };
        }
    }

    /// <summary>
    /// 只把本地状态推回 ADO：先读最新 rev，再只 PATCH System.State，
    /// 不碰标题 / 说明 / 优先级，所以云端被别人改过也能同步状态。
    /// </summary>
    public async Task<AdoWorkItemSyncResultDto> PushStatusAsync(string todoId, CancellationToken ct = default)
    {
        if (!TryResolveTodo(todoId, out var todo, out var id, out var failure))
        {
            return failure!;
        }

        var collectionUrl = planner.GetAdoConnection().CollectionUrl;
        var pat = ResolvePat(null);

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var item = await client.FetchWorkItemAsync(collectionUrl, pat, id, ct);
                if (item is null)
                {
                    return new AdoWorkItemSyncResultDto
                    {
                        Ok = false,
                        Message = $"ADO #{id} 已删除或不可见，本地这条保持不变。",
                        TodoId = todoId,
                    };
                }

                var result = await client.UpdateWorkItemStatusAsync(
                    collectionUrl, pat, id, PlannerMapper.ToStorage(todo.Status), item.Rev, ct);

                if (result.Ok)
                {
                    planner.MarkAdoPushed(todo.Id, item.ExternalId, result.ExternalRev, result.ExternalUrl ?? item.Url);
                    return result with { TodoId = todoId };
                }

                // 只有 rev 撞车才重读一次；状态名不被流程接受时直接返回，重试也没用。
                if (!result.Message.Contains("刚刚又被改过", StringComparison.Ordinal))
                {
                    return result with { TodoId = todoId };
                }
            }

            return new AdoWorkItemSyncResultDto
            {
                Ok = false,
                Message = $"ADO #{id} 一直在被改，没敢再推状态；过几秒再点一次「仅同步状态」。",
                TodoId = todoId,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = ex.Message, TodoId = todoId };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AdoWorkItemSyncResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}", TodoId = todoId };
        }
    }

    /// <summary>
    /// 上传 / 关联前的前置检查：任务要存在且归到项目、项目要绑定、连接要启用、PAT 要有。
    /// 和 <see cref="TryResolveTodo"/> 的区别是不要求任务已经来自 ADO。
    /// </summary>
    private bool TryResolveTodoProject(
        string todoId,
        out TodoDto todo,
        out AdoBindingDto binding,
        out string collectionUrl,
        out string pat,
        out AdoWorkItemSyncResultDto? failure)
    {
        todo = null!;
        binding = null!;
        collectionUrl = string.Empty;
        pat = string.Empty;
        failure = null;

        var found = planner.GetTodo(todoId);
        if (found is null)
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "任务不存在", TodoId = todoId };
            return false;
        }

        todo = found;
        if (string.IsNullOrWhiteSpace(todo.ProjectId))
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "先把任务归到一个项目，再由项目绑定云端项目。", TodoId = todoId };
            return false;
        }

        binding = planner.GetAdoBinding(todo.ProjectId);
        if (!binding.Bound || string.IsNullOrWhiteSpace(binding.AdoProject))
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "所在项目还没有绑定云端项目。", TodoId = todoId };
            return false;
        }

        var connection = planner.GetAdoConnection();
        if (!connection.Enabled)
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "Azure DevOps 接入已关闭，先到设置里打开。", TodoId = todoId };
            return false;
        }

        collectionUrl = ResolveCollection(connection.CollectionUrl);
        pat = ResolvePat(null);
        if (string.IsNullOrWhiteSpace(collectionUrl) || string.IsNullOrWhiteSpace(pat))
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "ADO 连接还不完整，先到设置里填集合地址和 PAT。", TodoId = todoId };
            return false;
        }

        return true;
    }

    /// <summary>推送 / 拉取前的前置检查：任务要来自 ADO、项目要绑定、连接要启用、PAT 要有。</summary>
    private bool TryResolveTodo(string todoId, out TodoDto todo, out long id, out AdoWorkItemSyncResultDto? failure)
    {
        todo = null!;
        id = 0;
        failure = null;

        var found = planner.GetTodo(todoId);
        if (found is null)
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "任务不存在", TodoId = todoId };
            return false;
        }

        todo = found;
        if (todo.ExternalSource != AdoMapper.SourceName
            || !long.TryParse(todo.ExternalId, out id))
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "这条任务不是从 Azure DevOps 同步来的，没法单条同步。", TodoId = todoId };
            return false;
        }

        if (!planner.GetAdoConnection().Enabled)
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "Azure DevOps 接入已关闭，先到设置里打开。", TodoId = todoId };
            return false;
        }

        if (string.IsNullOrWhiteSpace(todo.ProjectId) || !planner.GetAdoBinding(todo.ProjectId).Bound)
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "所在项目还没有绑定云端项目。", TodoId = todoId };
            return false;
        }

        if (string.IsNullOrWhiteSpace(planner.GetAdoPat()))
        {
            failure = new AdoWorkItemSyncResultDto { Ok = false, Message = "还没有保存 PAT，先到设置里填一个。", TodoId = todoId };
            return false;
        }

        return true;
    }

    private string ResolveCollection(string? collectionUrl) =>
        string.IsNullOrWhiteSpace(collectionUrl) ? planner.GetAdoConnection().CollectionUrl : collectionUrl.Trim();

    private string ResolvePat(string? pat) =>
        string.IsNullOrWhiteSpace(pat) ? planner.GetAdoPat() ?? string.Empty : pat.Trim();

    private static AdoFetchOptions ToOptions(AdoBindingInput input) => new()
    {
        AssignedTo = string.IsNullOrWhiteSpace(input.AssignedTo) ? null : input.AssignedTo.Trim(),
        IterationPaths = input.IterationPaths,
        SkipClosed = input.SkipClosed,
    };

    private static AdoFetchOptions ToOptions(AdoBindingDto binding) => new()
    {
        AssignedTo = string.IsNullOrWhiteSpace(binding.AssignedTo) ? null : binding.AssignedTo.Trim(),
        IterationPaths = binding.IterationPaths,
        SkipClosed = binding.SkipClosed,
    };
}
