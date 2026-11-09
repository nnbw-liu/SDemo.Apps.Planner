using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// Azure DevOps Server（本地部署）只读客户端。**只依赖 HttpClient**，无模型凭证；
/// 认证用 PAT 的 Basic 头（<c>":" + pat</c>），api-version 固定 5.1（服务端是 ADO Server 2020，
/// 6.0/7.1 会 400）。
/// </summary>
public sealed class AdoClient(HttpClient http)
{
    public const string ApiVersion = "5.1";

    private const int WorkItemBatchSize = 200;

    /// <summary>只测连接：认证用户 + 可见项目数。失败返回 Ok=false，不抛网络异常。</summary>
    public async Task<AdoConnectionTestDto> TestConnectionAsync(
        string collectionUrl,
        string pat,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw PlannerException.Validation("PAT 不能为空");
        }

        try
        {
            string? user = null;
            using (var doc = await SendAsync(HttpMethod.Get, $"{root}/_apis/connectionData?api-version={ApiVersion}-preview.1", pat, null, ct))
            {
                if (doc.RootElement.TryGetProperty("authenticatedUser", out var auth))
                {
                    user = auth.TryGetProperty("providerDisplayName", out var name) ? name.GetString() : null;
                }
            }

            var count = 0;
            using (var doc = await SendAsync(HttpMethod.Get, $"{root}/_apis/projects?api-version={ApiVersion}", pat, null, ct))
            {
                if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    count = value.GetArrayLength();
                }
            }

            return new AdoConnectionTestDto
            {
                Ok = true,
                Message = $"连接成功：{user ?? "未知用户"}，可见 {count} 个项目。",
                AuthenticatedUser = user,
                ProjectCount = count,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoConnectionTestDto { Ok = false, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdoConnectionTestDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}" };
        }
    }

    /// <summary>测某个项目的同步范围：认证用户 + 迭代数 + 工作项数（按范围统计）。失败返回 Ok=false，不抛网络异常。</summary>
    public async Task<AdoConnectionTestDto> TestAsync(
        string collectionUrl,
        string project,
        string pat,
        AdoFetchOptions? options = null,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            throw PlannerException.Validation("项目不能为空");
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw PlannerException.Validation("PAT 不能为空");
        }

        try
        {
            string? user = null;
            using (var doc = await SendAsync(HttpMethod.Get, $"{root}/_apis/connectionData?api-version={ApiVersion}-preview.1", pat, null, ct))
            {
                if (doc.RootElement.TryGetProperty("authenticatedUser", out var auth))
                {
                    user = auth.TryGetProperty("providerDisplayName", out var name) ? name.GetString() : null;
                }
            }

            var projectInfo = await ResolveProjectAsync(root, project, pat, ct);
            var iterations = FilterIterations(
                await FetchIterationsAsync(root, projectInfo, pat, ct),
                options?.IterationPaths ?? []);
            var ids = await QueryWorkItemIdsAsync(root, projectInfo.Name, pat, options, ct);
            var scopeNote = options is null || (string.IsNullOrWhiteSpace(options.AssignedTo)
                && options.IterationPaths.Count == 0
                && !options.SkipClosed)
                ? string.Empty
                : "（当前同步范围）";
            return new AdoConnectionTestDto
            {
                Ok = true,
                Message = $"连接成功：{projectInfo.Name}，{iterations.Count} 个迭代，{ids.Count} 个工作项{scopeNote}。",
                AuthenticatedUser = user,
                WorkItemCount = ids.Count,
                IterationCount = iterations.Count,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoConnectionTestDto { Ok = false, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdoConnectionTestDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}" };
        }
    }

    /// <summary>列集合里的所有项目，供设置页挑选。失败返回 Ok=false，不抛网络异常。</summary>
    public async Task<AdoProjectsResultDto> ListProjectsAsync(
        string collectionUrl,
        string pat,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw PlannerException.Validation("PAT 不能为空");
        }

        try
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{root}/_apis/projects?api-version={ApiVersion}", pat, null, ct);
            var projects = new List<AdoProjectRefDto>();
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        projects.Add(new AdoProjectRefDto { ExternalId = id, Name = name });
                    }
                }
            }

            projects.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new AdoProjectsResultDto
            {
                Ok = true,
                Message = $"找到 {projects.Count} 个项目。",
                Projects = projects,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoProjectsResultDto { Ok = false, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdoProjectsResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}" };
        }
    }

    /// <summary>拉一次快照：项目信息 + 迭代树 + 工作项（已排除 Removed；按 <paramref name="options"/> 过滤范围）。</summary>
    public async Task<AdoProjectSnapshot> FetchAsync(
        string collectionUrl,
        string project,
        string pat,
        AdoFetchOptions? options = null,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0 || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(pat))
        {
            throw new PlannerException(
                ErrorCodes.AdoNotConfigured,
                "Azure DevOps 连接未配置完整（集合地址 / 项目 / PAT）",
                "到「设置 → Azure DevOps」填好后重试");
        }

        var projectInfo = await ResolveProjectAsync(root, project, pat, ct);
        var iterations = FilterIterations(await FetchIterationsAsync(root, projectInfo, pat, ct), options?.IterationPaths ?? []);
        var workItems = await FetchWorkItemsAsync(root, projectInfo, pat, options, ct);
        if (options is { IterationPaths.Count: > 0 })
        {
            workItems = workItems.Where(item => AdoMapper.IterationMatches(options.IterationPaths, item.IterationPath)).ToList();
        }

        return new AdoProjectSnapshot
        {
            ExternalId = projectInfo.Id,
            Name = projectInfo.Name,
            Url = AdoMapper.ProjectUrl(root, projectInfo.Name),
            Description = projectInfo.Description,
            Iterations = iterations,
            WorkItems = workItems,
        };
    }

    /// <summary>列项目里的迭代（供设置页勾选同步范围）。失败返回 Ok=false，不抛网络异常。</summary>
    public async Task<AdoIterationsResultDto> ListIterationsAsync(
        string collectionUrl,
        string project,
        string pat,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            throw PlannerException.Validation("项目不能为空");
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            throw PlannerException.Validation("PAT 不能为空");
        }

        try
        {
            var projectInfo = await ResolveProjectAsync(root, project, pat, ct);
            var iterations = await FetchIterationsAsync(root, projectInfo, pat, ct);
            var refs = iterations
                .Select(item => new AdoIterationRefDto
                {
                    ExternalId = item.ExternalId,
                    Path = AdoMapper.NormalizePath(item.Path),
                    Name = item.Name,
                    Depth = Math.Max(0, AdoMapper.SplitPath(item.Path).Length - 2),
                    StartAt = item.StartAt,
                    EndAt = item.EndAt,
                })
                .ToList();
            return new AdoIterationsResultDto
            {
                Ok = true,
                Message = $"找到 {refs.Count} 个迭代。",
                Iterations = refs,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoIterationsResultDto { Ok = false, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdoIterationsResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}" };
        }
    }

    /// <summary>列项目里出现过的指派人（供设置页选「只同步我的/某人的任务」）。</summary>
    public async Task<AdoAssigneesResultDto> ListAssigneesAsync(
        string collectionUrl,
        string project,
        string pat,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0 || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(pat))
        {
            throw new PlannerException(
                ErrorCodes.AdoNotConfigured,
                "Azure DevOps 连接未配置完整（集合地址 / 项目 / PAT）",
                "到「设置 → Azure DevOps」填好后重试");
        }

        try
        {
            var projectInfo = await ResolveProjectAsync(root, project, pat, ct);
            var ids = await QueryWorkItemIdsAsync(root, projectInfo.Name, pat, null, ct);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in ids.Chunk(WorkItemBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var url = $"{root}/_apis/wit/workitems?ids={string.Join(',', chunk)}"
                    + $"&fields=System.AssignedTo&api-version={ApiVersion}";
                using var doc = await SendAsync(HttpMethod.Get, url, pat, null, ct);
                if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in value.EnumerateArray())
                {
                    if (!element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = ReadAssignedTo(fields);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
            }

            var assignees = counts
                .Select(pair => new AdoAssigneeDto { DisplayName = pair.Key, WorkItemCount = pair.Value })
                .OrderByDescending(item => item.WorkItemCount)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new AdoAssigneesResultDto
            {
                Ok = true,
                Message = $"找到 {assignees.Count} 个指派人。",
                Assignees = assignees,
            };
        }
        catch (PlannerException ex)
        {
            return new AdoAssigneesResultDto { Ok = false, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdoAssigneesResultDto { Ok = false, Message = $"连不上 Azure DevOps：{ex.Message}" };
        }
    }

    /// <summary>只保留选中迭代及其祖先/后代，避免把历史迭代一起导进来。</summary>
    public static IReadOnlyList<AdoIterationDto> FilterIterations(
        IReadOnlyList<AdoIterationDto> all,
        IReadOnlyList<string> selectedPaths)
    {
        if (selectedPaths.Count == 0)
        {
            return all;
        }

        var byPath = new Dictionary<string, AdoIterationDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var iteration in all)
        {
            byPath[AdoMapper.NormalizePath(iteration.Path)] = iteration;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in selectedPaths)
        {
            var path = AdoMapper.NormalizePath(raw);
            if (path.Length == 0)
            {
                continue;
            }

            foreach (var iteration in all)
            {
                var candidate = AdoMapper.NormalizePath(iteration.Path);
                if (candidate.Equals(path, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    keep.Add(candidate);
                }
            }

            // 祖先节点要保留，否则大迭代不存在、小迭代挂不上去。
            var cursor = byPath.GetValueOrDefault(path)?.ParentPath;
            var guard = 0;
            while (cursor is { Length: > 0 } && guard++ < 16)
            {
                var parentPath = AdoMapper.NormalizePath(cursor);
                keep.Add(parentPath);
                cursor = byPath.GetValueOrDefault(parentPath)?.ParentPath;
            }
        }

        return all.Where(item => keep.Contains(AdoMapper.NormalizePath(item.Path))).ToList();
    }

    // ── 单条工作项：拉取 / 推送 ──────────────────────────────────────────────

    /// <summary>读一条工作项（单任务「从云端同步」用）。</summary>
    public async Task<AdoWorkItemDto?> FetchWorkItemAsync(
        string collectionUrl,
        string pat,
        long id,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        using var doc = await SendAsync(
            HttpMethod.Get,
            $"{root}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?api-version={ApiVersion}",
            pat,
            null,
            ct);

        var projectName = doc.RootElement.TryGetProperty("fields", out var fields)
            ? FieldString(fields, "System.TeamProject")
            : null;
        return MapWorkItem(root, projectName ?? string.Empty, doc.RootElement);
    }

    /// <summary>
    /// 把本地任务推回 ADO（标题 / 说明 / 状态 / 优先级）。带 <c>/rev</c> 的 test op 做乐观并发：
    /// ADO 里的 rev 变了就返回 Ok=false，让用户先「从云端同步」再改。
    /// </summary>
    public async Task<AdoWorkItemSyncResultDto> UpdateWorkItemAsync(
        string collectionUrl,
        string pat,
        long id,
        string title,
        string? notes,
        string plannStatus,
        int priority,
        int expectedRev,
        CancellationToken ct = default)
    {
        var root = NormalizeCollection(collectionUrl);
        if (root.Length == 0)
        {
            throw PlannerException.Validation("集合地址不能为空");
        }

        var url = $"{root}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?api-version={ApiVersion}";
        var states = AdoMapper.ToAdoStates(plannStatus);
        var changed = new List<string> { "标题", "说明", "优先级" };
        var lastError = "未知错误";

        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            var ops = new List<Dictionary<string, object?>>
            {
                PatchOp("test", "/rev", expectedRev),
                PatchOp("replace", "/fields/System.Title", title),
                PatchOp("replace", "/fields/System.Description", AdoMapper.ToAdoHtml(notes)),
                PatchOp("replace", "/fields/Microsoft.VSTS.Common.Priority", AdoMapper.ToAdoPriority(priority)),
                PatchOp("replace", "/fields/System.State", state),
            };

            var (status, text) = await SendRawAsync(
                HttpMethod.Patch, url, pat, ops, "application/json-patch+json", ct);

            if (status is >= 200 and < 300)
            {
                var rev = 0;
                string? projectName = null;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("rev", out var revElement) && revElement.TryGetInt32(out var parsed))
                    {
                        rev = parsed;
                    }

                    if (doc.RootElement.TryGetProperty("fields", out var responseFields))
                    {
                        projectName = FieldString(responseFields, "System.TeamProject");
                    }
                }
                catch (JsonException)
                {
                    // 返回体不是 JSON 也不影响已经成功的写入。
                }

                var itemUrl = string.IsNullOrWhiteSpace(projectName)
                    ? $"{root}/_workitems/edit/{id.ToString(CultureInfo.InvariantCulture)}"
                    : AdoMapper.WorkItemUrl(root, projectName, id);

                return new AdoWorkItemSyncResultDto
                {
                    Ok = true,
                    Message = $"已推送到 ADO #{id}（状态「{state}」）。",
                    ExternalId = id.ToString(CultureInfo.InvariantCulture),
                    ExternalRev = rev,
                    ExternalUrl = itemUrl,
                    ChangedFields = changed.Concat(["状态"]).ToList(),
                };
            }

            lastError = Snippet(text);
            if (status == 409)
            {
                return new AdoWorkItemSyncResultDto
                {
                    Ok = false,
                    Message = $"ADO 里的这条工作项已经被改过（本地记的 rev {expectedRev} 不是最新），先「从云端同步」再推送。",
                    ExternalId = id.ToString(CultureInfo.InvariantCulture),
                };
            }

            // 状态名不被这个流程接受（中英文混用）时换下一个候选；其它 4xx 直接报错。
            if (status != 400)
            {
                throw new PlannerException(ErrorCodes.AdoUnavailable, $"Azure DevOps 返回 HTTP {status}：{lastError}", HintFor(status));
            }
        }

        return new AdoWorkItemSyncResultDto
        {
            Ok = false,
            Message = $"ADO 拒绝了这次更新：{lastError}",
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static Dictionary<string, object?> PatchOp(string op, string path, object? value) => new()
    {
        ["op"] = op,
        ["path"] = path,
        ["value"] = value,
    };

    // ── 内部：项目 / 迭代 / 工作项 ───────────────────────────────────────────

    private sealed record ProjectInfo(string Id, string Name, string? Description);

    private async Task<ProjectInfo> ResolveProjectAsync(string root, string project, string pat, CancellationToken ct)
    {
        using var doc = await SendAsync(
            HttpMethod.Get,
            $"{root}/_apis/projects/{Uri.EscapeDataString(project)}?api-version={ApiVersion}",
            pat,
            null,
            ct);
        var element = doc.RootElement;
        var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? project : project;
        var id = element.TryGetProperty("id", out var i) ? i.GetString() ?? name : name;
        var description = element.TryGetProperty("description", out var d) ? d.GetString() : null;
        return new ProjectInfo(id, name, description);
    }

    private async Task<IReadOnlyList<AdoIterationDto>> FetchIterationsAsync(
        string root,
        ProjectInfo project,
        string pat,
        CancellationToken ct)
    {
        var url = $"{AdoMapper.ProjectUrl(root, project.Name)}/_apis/wit/classificationnodes/iterations"
            + $"?api-version={ApiVersion}&$depth=10";
        using var doc = await SendAsync(HttpMethod.Get, url, pat, null, ct);
        var rootPath = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        var list = new List<AdoIterationDto>();

        // 分类节点根（例如「迭代」）本身也是树的一层：保留它，子节点才能挂成「小迭代」。
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            var rootName = doc.RootElement.TryGetProperty("name", out var rn) ? rn.GetString() : null;
            var segments = AdoMapper.SplitPath(rootPath);
            if (string.IsNullOrWhiteSpace(rootName)
                || string.Equals(rootName, project.Name, StringComparison.OrdinalIgnoreCase))
            {
                // 服务端对这个根节点给的名字有时就是项目名，用路径末段（通常是「迭代」）更直观。
                rootName = segments.Length > 0 ? segments[^1] : rootPath;
            }
            var rootId = doc.RootElement.TryGetProperty("identifier", out var ri) ? ri.GetString() : null;
            DateTimeOffset? rootStart = null;
            DateTimeOffset? rootEnd = null;
            if (doc.RootElement.TryGetProperty("attributes", out var rootAttrs))
            {
                rootStart = ReadDate(rootAttrs, "startDate");
                rootEnd = ReadDate(rootAttrs, "finishDate");
            }

            list.Add(new AdoIterationDto
            {
                ExternalId = string.IsNullOrWhiteSpace(rootId) ? rootPath : rootId,
                Path = AdoMapper.NormalizePath(rootPath),
                ParentPath = null,
                Name = rootName,
                StartAt = rootStart,
                EndAt = rootEnd,
            });
        }

        Flatten(rootPath, doc.RootElement, list, ct);
        return list;
    }

    private static void Flatten(string? parentPath, JsonElement node, List<AdoIterationDto> list, CancellationToken ct)
    {
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var path = child.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            var name = child.TryGetProperty("name", out var n) ? n.GetString() ?? path : path;
            var id = child.TryGetProperty("identifier", out var i) ? i.GetString() : null;
            DateTimeOffset? start = null;
            DateTimeOffset? end = null;
            if (child.TryGetProperty("attributes", out var attrs))
            {
                start = ReadDate(attrs, "startDate");
                end = ReadDate(attrs, "finishDate");
            }

            if (path.Length > 0)
            {
                list.Add(new AdoIterationDto
                {
                    ExternalId = string.IsNullOrWhiteSpace(id) ? path : id,
                    Path = AdoMapper.NormalizePath(path),
                    ParentPath = string.IsNullOrWhiteSpace(parentPath) ? null : AdoMapper.NormalizePath(parentPath),
                    Name = name,
                    StartAt = start,
                    EndAt = end,
                });
            }

            Flatten(path, child, list, ct);
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement attrs, string name)
    {
        if (!attrs.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private async Task<IReadOnlyList<long>> QueryWorkItemIdsAsync(
        string root,
        string projectName,
        string pat,
        AdoFetchOptions? options,
        CancellationToken ct)
    {
        var query = BuildWiql(projectName, options);
        using var doc = await SendAsync(
            HttpMethod.Post,
            $"{AdoMapper.ProjectUrl(root, projectName)}/_apis/wit/wiql?api-version={ApiVersion}",
            pat,
            new { query },
            ct);

        var ids = new List<long>();
        if (doc.RootElement.TryGetProperty("workItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    /// <summary>按同步范围拼 WIQL。指派人 <c>@Me</c> 交给服务端解析；迭代用别名口径（工作项路径不含迭代根节点）。</summary>
    public static string BuildWiql(string projectName, AdoFetchOptions? options)
    {
        var conditions = new List<string>
        {
            $"[System.TeamProject] = '{Escape(projectName)}'",
        };

        var excludedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Removed", "已删除" };
        if (options?.SkipClosed == true)
        {
            foreach (var state in new[] { "Closed", "已关闭", "Done", "Resolved", "已解决" })
            {
                excludedStates.Add(state);
            }
        }

        conditions.AddRange(excludedStates.Select(state => $"[System.State] <> '{Escape(state)}'"));

        if (!string.IsNullOrWhiteSpace(options?.AssignedTo))
        {
            var who = options.AssignedTo.Trim();
            conditions.Add(who.Equals("@Me", StringComparison.OrdinalIgnoreCase)
                ? "[System.AssignedTo] = @Me"
                : $"[System.AssignedTo] = '{Escape(who)}'");
        }

        if (options is { IterationPaths.Count: > 0 })
        {
            var paths = options.IterationPaths
                .Select(AdoMapper.NormalizePath)
                .Where(path => path.Length > 0)
                .Select(path => AdoMapper.IterationAlias(path) ?? path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count > 0)
            {
                var any = paths
                    .Select(path => $"([System.IterationPath] = '{Escape(path)}' OR [System.IterationPath] UNDER '{Escape(path)}')")
                    .ToList();
                conditions.Add("(" + string.Join(" OR ", any) + ")");
            }
        }

        return "SELECT [System.Id] FROM WorkItems WHERE "
            + string.Join(" AND ", conditions)
            + " ORDER BY [System.Id]";
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private async Task<IReadOnlyList<AdoWorkItemDto>> FetchWorkItemsAsync(
        string root,
        ProjectInfo project,
        string pat,
        AdoFetchOptions? options,
        CancellationToken ct)
    {
        var ids = await QueryWorkItemIdsAsync(root, project.Name, pat, options, ct);
        var items = new List<AdoWorkItemDto>(ids.Count);
        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{root}/_apis/wit/workitems?ids={string.Join(',', chunk)}&api-version={ApiVersion}";
            using var doc = await SendAsync(HttpMethod.Get, url, pat, null, ct);
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var element in value.EnumerateArray())
            {
                var mapped = MapWorkItem(root, project.Name, element);
                if (mapped is not null)
                {
                    items.Add(mapped);
                }
            }
        }

        return items;
    }

    private static AdoWorkItemDto? MapWorkItem(string root, string projectName, JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
        {
            return null;
        }

        if (!element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = FieldString(fields, "System.State");
        if (state is "Removed" or "已删除")
        {
            return null;
        }

        var title = FieldString(fields, "System.Title") ?? $"#{id}";
        return new AdoWorkItemDto
        {
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            Rev = element.TryGetProperty("rev", out var rev) && rev.TryGetInt32(out var revValue) ? revValue : 0,
            Title = title,
            Notes = AdoMapper.HtmlToText(FieldString(fields, "System.Description")),
            Type = FieldString(fields, "System.WorkItemType") ?? "Task",
            State = state ?? "New",
            AdoPriority = FieldInt(fields, "Microsoft.VSTS.Common.Priority"),
            IterationPath = FieldString(fields, "System.IterationPath"),
            AssignedTo = ReadAssignedTo(fields),
            Tags = FieldString(fields, "System.Tags"),
            ParentExternalId = FieldLong(fields, "System.Parent")?.ToString(CultureInfo.InvariantCulture),
            ChangedAt = ReadDateTime(fields, "System.ChangedDate"),
            Url = AdoMapper.WorkItemUrl(root, projectName, id),
        };
    }

    private static string? ReadAssignedTo(JsonElement fields)
    {
        if (!fields.TryGetProperty("System.AssignedTo", out var assigned))
        {
            return null;
        }

        return assigned.ValueKind switch
        {
            JsonValueKind.String => assigned.GetString(),
            JsonValueKind.Object when assigned.TryGetProperty("displayName", out var display) => display.GetString(),
            _ => null,
        };
    }

    private static string? FieldString(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? FieldInt(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(v.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static long? FieldLong(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(v.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadDateTime(JsonElement fields, string name)
    {
        var raw = FieldString(fields, name);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string url,
        string pat,
        object? body,
        CancellationToken ct)
        => await SendAsync(method, url, pat, body, "application/json", ct);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string url,
        string pat,
        object? body,
        string mediaType,
        CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(method, url, pat, body, mediaType, ct);
        if (status is < 200 or >= 300)
        {
            throw new PlannerException(ErrorCodes.AdoUnavailable, $"Azure DevOps 返回 HTTP {status}：{Snippet(text)}", HintFor(status));
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new PlannerException(ErrorCodes.AdoUnavailable, $"Azure DevOps 返回了非 JSON 内容：{ex.Message}");
        }
    }

    /// <summary>发请求并拿回状态码与正文；调用方自己决定怎么解释错误（工作项 PATCH 要按状态重试）。</summary>
    private async Task<(int Status, string Text)> SendRawAsync(
        HttpMethod method,
        string url,
        string pat,
        object? body,
        string mediaType,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)));
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, mediaType);
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, text);
    }

    private static string? HintFor(int status) => status switch
    {
        401 => "PAT 无效、已过期或没有该项目的读取权限",
        403 => "PAT 缺少「工作项（读取）」权限",
        404 => "集合地址或项目名不对",
        _ => null,
    };

    private static string Snippet(string text)
    {
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 200 ? clean : clean[..200];
    }

    internal static string NormalizeCollection(string? url) => (url ?? string.Empty).Trim().TrimEnd('/');
}
