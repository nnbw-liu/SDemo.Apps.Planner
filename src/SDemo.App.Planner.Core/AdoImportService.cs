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
