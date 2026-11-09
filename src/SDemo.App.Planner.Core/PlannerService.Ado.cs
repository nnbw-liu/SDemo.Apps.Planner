using System.Globalization;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// Azure DevOps 接入的写入口：连接配置（集合地址 + PAT）+ 项目级绑定 + 快照落库（只读镜像，ADO 为权威源）。
/// 落库全部走 <see cref="PlannerStore"/> 的写事务，`Revision + 1`、`Revision=1` 新建，
/// 不修改本地手工条目（只处理 <c>ExternalSource = azure-devops</c> 的行）。
/// </summary>
public sealed partial class PlannerService
{
    private const string AdoEnabledKey = "ado.enabled";
    private const string AdoCollectionKey = "ado.collectionUrl";
    private const string AdoPatKey = "ado.pat";

    /// <summary>项目级绑定的设置前缀，后接本地项目 id，例如 <c>ado.bind.pj_xxx.project</c>。</summary>
    private const string AdoBindingPrefix = "ado.bind.";

    private const string AdoDefaultColor = "#2F6FDB";

    public AdoConnectionDto GetAdoConnection()
    {
        var settings = _store.GetSettings("ado.");
        var pat = SettingValue(settings, AdoPatKey);
        return new AdoConnectionDto
        {
            Enabled = SettingValue(settings, AdoEnabledKey) != "false",
            CollectionUrl = SettingValue(settings, AdoCollectionKey) ?? string.Empty,
            HasPat = !string.IsNullOrWhiteSpace(pat),
        };
    }

    public AdoConnectionDto SaveAdoConnection(AdoConnectionInput input)
    {
        var collectionUrl = AdoMapper.NormalizeCollectionUrl(input.CollectionUrl);
        if (collectionUrl.Length > 0 && !collectionUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw PlannerException.Validation("集合地址必须是 http(s) 绝对地址");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AdoEnabledKey] = input.Enabled ? "true" : "false",
            [AdoCollectionKey] = collectionUrl,
        };

        if (input.ClearPat)
        {
            values[AdoPatKey] = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(input.Pat))
        {
            values[AdoPatKey] = input.Pat.Trim();
        }

        _store.SetSettings(values, NowUtc());
        RaiseChanged();
        return GetAdoConnection();
    }

    public AdoBindingDto GetAdoBinding(string projectId)
    {
        var prefix = AdoBindingPrefix + projectId + ".";
        var settings = _store.GetSettings(prefix);
        var lastSyncAt = SettingValue(settings, prefix + "lastSyncAt");
        return new AdoBindingDto
        {
            ProjectId = projectId,
            AdoProject = SettingValue(settings, prefix + "project") ?? string.Empty,
            AssignedTo = NullIfEmpty(SettingValue(settings, prefix + "assignedTo")),
            IterationPaths = SplitIterationPaths(SettingValue(settings, prefix + "iterations")),
            SkipClosed = SettingValue(settings, prefix + "skipClosed") == "true",
            LastSyncAt = DateTimeOffset.TryParse(lastSyncAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null,
            LastSyncMessage = SettingValue(settings, prefix + "lastSyncMessage"),
            LastSyncOk = SettingValue(settings, prefix + "lastSyncOk") is not "false",
        };
    }

    /// <summary>保存项目级绑定；<see cref="AdoBindingInput.AdoProject"/> 为空表示解除绑定。</summary>
    public AdoBindingDto SaveAdoBinding(string projectId, AdoBindingInput input)
    {
        var project = _store.GetProject(projectId)
            ?? throw PlannerException.NotFound("项目不存在，无法设置云端项目");

        var prefix = AdoBindingPrefix + projectId + ".";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + "project"] = (input.AdoProject ?? string.Empty).Trim(),
            [prefix + "assignedTo"] = (input.AssignedTo ?? string.Empty).Trim(),
            [prefix + "iterations"] = JoinIterationPaths(input.IterationPaths),
            [prefix + "skipClosed"] = input.SkipClosed ? "true" : "false",
        };
        _store.SetSettings(values, NowUtc());
        RaiseChanged();
        return GetAdoBinding(project.Id);
    }

    /// <summary>PAT 明文只留在设置表里，只给 Core 内部（<c>AdoImportService</c>）用。</summary>
    internal string? GetAdoPat() => _store.GetSetting(AdoPatKey);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>迭代路径用换行分隔存一条设置（路径本身不含换行）。</summary>
    private static string JoinIterationPaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", paths
            .Select(AdoMapper.NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitIterationPaths(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AdoMapper.NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal void RecordAdoSync(string projectId, AdoSyncResultDto result)
    {
        var prefix = AdoBindingPrefix + projectId + ".";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + "lastSyncAt"] = result.SyncedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            [prefix + "lastSyncMessage"] = result.Message ?? string.Empty,
            [prefix + "lastSyncOk"] = result.Ok ? "true" : "false",
        };
        _store.SetSettings(values, NowUtc());
    }

    /// <summary>
    /// 把一次 ADO 快照幂等落库到指定本地项目：迭代 / 工作项都按 <c>(ExternalSource, ExternalId)</c> 匹配。
    /// 工作项 <c>ExternalRev</c> 没变就跳过；本地手工条目一律不碰。
    /// </summary>
    public AdoSyncResultDto ApplyAdoSnapshot(AdoProjectSnapshot snapshot, string projectId, WriteContext? context = null)
    {
        var now = NowUtc();
        var result = new AdoSyncResultDto { Ok = true, SyncedAt = now, Warnings = [] };
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        var projectRow = _store.GetProject(projectId)
            ?? throw PlannerException.NotFound("要同步的项目不存在");

        projectRow = EnsureProjectLinked(projectRow, snapshot, now, result);

        var iterationRows = UpsertAdoIterations(snapshot, projectRow.Id, now, result, warnings);

        UpsertAdoWorkItems(snapshot, projectRow.Id, iterationRows, now, result, warnings);

        result.ProjectId = projectRow.Id;
        result.Message = $"「{projectRow.Name}」← ADO「{snapshot.Name}」：迭代 {result.IterationsCreated} 新建 / {result.IterationsUpdated} 更新，"
            + $"任务 {result.TodosCreated} 新建 / {result.TodosUpdated} 更新 / {result.TodosSkipped} 未变。";
        result.Warnings = warnings.ToList();
        RaiseChanged();
        return result;
    }

    /// <summary>本地项目第一次绑上 ADO 时补上外部标识（徽标、外链、二次识别都靠它）。</summary>
    private ProjectRow EnsureProjectLinked(ProjectRow project, AdoProjectSnapshot snapshot, DateTime now, AdoSyncResultDto result)
    {
        if (project.ExternalSource == AdoMapper.SourceName && project.ExternalId == snapshot.ExternalId)
        {
            return project;
        }

        var updated = CloneAdoProject(project);
        updated.ExternalSource = AdoMapper.SourceName;
        updated.ExternalId = snapshot.ExternalId;
        updated.ExternalUrl = snapshot.Url;
        if (string.IsNullOrWhiteSpace(updated.Description) && !string.IsNullOrWhiteSpace(snapshot.Description))
        {
            updated.Description = NormalizeNotes(snapshot.Description);
        }

        updated.Revision = project.Revision + 1;
        updated.UpdatedAt = now;
        _store.WriteProject(new ProjectWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = project.Revision,
            Actor = "import",
        });
        result.ProjectLinked = true;
        return updated;
    }

    /// <summary>返回 ADO 迭代 path → 本地迭代行（含新建/更新的最终行）。</summary>
    private Dictionary<string, IterationRow> UpsertAdoIterations(
        AdoProjectSnapshot snapshot,
        string projectId,
        DateTime now,
        AdoSyncResultDto result,
        HashSet<string> warnings)
    {
        var byPath = new Dictionary<string, AdoIterationDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var iteration in snapshot.Iterations)
        {
            if (!string.IsNullOrWhiteSpace(iteration.Path))
            {
                byPath[AdoMapper.NormalizePath(iteration.Path)] = iteration;
            }
        }

        var rowsByPath = new Dictionary<string, IterationRow>(StringComparer.OrdinalIgnoreCase);

        void Register(string path, IterationRow row)
        {
            var key = AdoMapper.NormalizePath(path);
            rowsByPath[key] = row;

            // ADO 工作项的 System.IterationPath 不含迭代根节点（例如「迭代」），补一条别名。
            var alias = AdoIterationAlias(key);
            if (alias is not null)
            {
                rowsByPath[alias] = row;
            }
        }
        var ordered = snapshot.Iterations
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .OrderBy(i => AdoDepth(i, byPath))
            .ToList();

        foreach (var iteration in ordered)
        {
            // 只允许两层：找到最近的顶层祖先（depth == 1）当父迭代，更深的层级被拍平。
            IterationRow? parent = null;
            if (!string.IsNullOrWhiteSpace(iteration.ParentPath))
            {
                var ancestorPath = FindTopAncestorPath(iteration, byPath);
                if (ancestorPath is not null && rowsByPath.TryGetValue(ancestorPath, out var topAncestor))
                {
                    parent = topAncestor;
                }

                if (parent is null && warnings.Add($"迭代「{iteration.Name}」的上层节点不在同步范围内，已作为大迭代导入"))
                {
                }
            }

            var level = parent is null ? "big" : "small";
            var status = AdoMapper.ToIterationStatus(iteration.StartAt, iteration.EndAt, new DateTimeOffset(now, TimeSpan.Zero));
            var startAt = iteration.StartAt?.ToUniversalTime().UtcDateTime;
            var endAt = iteration.EndAt?.ToUniversalTime().UtcDateTime;
            var name = RequireTitle(iteration.Name);

            var existing = _store.GetIterationByExternal(AdoMapper.SourceName, iteration.ExternalId);
            if (existing is null)
            {
                var row = new IterationRow
                {
                    Id = NewId("it"),
                    ProjectId = projectId,
                    ParentId = parent?.Id,
                    Level = level,
                    Name = name,
                    Status = status,
                    StartAt = startAt,
                    EndAt = endAt,
                    Sort = _store.QueryIterations(projectId).Count + result.IterationsCreated,
                    Revision = 1,
                    ExternalSource = AdoMapper.SourceName,
                    ExternalId = iteration.ExternalId,
                    ExternalUrl = AdoMapper.IterationUrl(snapshot.Url, iteration.Path),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _store.WriteIteration(new IterationWrite
                {
                    Row = row,
                    Op = "create",
                    ExpectedRevision = 0,
                    Actor = "import",
                });
                result.IterationsCreated++;
                Register(iteration.Path, row);
                continue;
            }

            Register(iteration.Path, existing);
            if (existing.ProjectId != projectId
                || existing.ParentId != parent?.Id
                || existing.Level != level
                || existing.Name != name
                || existing.Status != status
                || existing.StartAt != startAt
                || existing.EndAt != endAt
                || existing.ExternalUrl != AdoMapper.IterationUrl(snapshot.Url, iteration.Path))
            {
                var updated = CloneAdoIteration(existing);
                updated.ProjectId = projectId;
                updated.ParentId = parent?.Id;
                updated.Level = level;
                updated.Name = name;
                updated.Status = status;
                updated.StartAt = startAt;
                updated.EndAt = endAt;
                updated.ExternalUrl = AdoMapper.IterationUrl(snapshot.Url, iteration.Path);
                updated.Revision = existing.Revision + 1;
                updated.UpdatedAt = now;
                if (status == "done" && updated.CompletedAt is null)
                {
                    updated.CompletedAt = now;
                }

                _store.WriteIteration(new IterationWrite
                {
                    Row = updated,
                    Op = "update",
                    ExpectedRevision = existing.Revision,
                    Actor = "import",
                });
                result.IterationsUpdated++;
                Register(iteration.Path, updated);
            }
        }

        return rowsByPath;
    }

    /// <summary>层级深度（顶层 = 1）；用于按祖先先于子节点落库。</summary>
    private static int AdoDepth(AdoIterationDto iteration, Dictionary<string, AdoIterationDto> byPath)
    {
        var depth = 1;
        var parentPath = iteration.ParentPath;
        while (depth < 16
               && parentPath is { Length: > 0 }
               && byPath.TryGetValue(parentPath, out var parent))
        {
            depth++;
            parentPath = AdoMapper.NormalizePath(parent.ParentPath);
        }

        return depth;
    }

    /// <summary>工作项里的迭代路径去掉「项目\迭代根」中的根节点层（ADO 的 System.IterationPath 规则）。</summary>
    private static string? AdoIterationAlias(string normalizedPath)
    {
        var parts = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 3 ? null : parts[0] + "\\" + string.Join("\\", parts.Skip(2));
    }

    /// <summary>最近的顶层祖先 path（事序只允许两层，更深的节点挂到它下面）。</summary>
    private static string? FindTopAncestorPath(AdoIterationDto iteration, Dictionary<string, AdoIterationDto> byPath)
    {
        string? top = null;
        var path = AdoMapper.NormalizePath(iteration.ParentPath);
        while (path.Length > 0 && byPath.TryGetValue(path, out var parent))
        {
            top = AdoMapper.NormalizePath(parent.Path);
            path = AdoMapper.NormalizePath(parent.ParentPath);
        }

        return top;
    }

    private void UpsertAdoWorkItems(
        AdoProjectSnapshot snapshot,
        string projectId,
        Dictionary<string, IterationRow> iterationsByPath,
        DateTime now,
        AdoSyncResultDto result,
        HashSet<string> warnings)
    {
        // 先建 externalId → 本地 todoId 的映射，父项在同一批里时也能一次写对。
        var idByExternal = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new Dictionary<string, TodoRow>(StringComparer.Ordinal);
        foreach (var item in snapshot.WorkItems)
        {
            var existing = _store.GetTodoByExternal(AdoMapper.SourceName, item.ExternalId);
            if (existing is not null)
            {
                idByExternal[item.ExternalId] = existing.Id;
                rows[item.ExternalId] = existing;
            }
            else
            {
                idByExternal[item.ExternalId] = NewId("td");
            }
        }

        var writes = new List<TodoWrite>();
        foreach (var item in snapshot.WorkItems)
        {
            var status = AdoMapper.ToStorageStatus(item.State);
            var kind = AdoMapper.ToStorageKind(item.Type);
            var priority = AdoMapper.ToPriority(item.AdoPriority);
            var notes = string.IsNullOrWhiteSpace(item.Notes) ? null : NormalizeNotes(item.Notes);
            var title = RequireTitle(item.Title);
            string? iterationId = null;
            if (item.IterationPath is { Length: > 0 } path && iterationsByPath.TryGetValue(AdoMapper.NormalizePath(path), out var iteration))
            {
                iterationId = iteration.Id;
            }

            string? parentId = null;
            if (item.ParentExternalId is { Length: > 0 } parentExternal)
            {
                if (idByExternal.TryGetValue(parentExternal, out var mappedParent))
                {
                    parentId = mappedParent;
                }
                else if (warnings.Add($"部分工作项的父项 #{parentExternal} 在 ADO 里已不可见，父任务关系留空：{item.Title}"))
                {
                }
            }

            if (!rows.TryGetValue(item.ExternalId, out var existing))
            {
                var row = new TodoRow
                {
                    Id = idByExternal[item.ExternalId],
                    Title = title,
                    Notes = notes,
                    Status = status,
                    Priority = priority,
                    ListId = PlannerStore.DefaultListId,
                    ProjectId = projectId,
                    IterationId = iterationId,
                    Kind = kind,
                    ParentTodoId = parentId,
                    Sort = 0,
                    Revision = 1,
                    Source = "import",
                    CompletedAt = IsTerminalStatus(status) ? AdoTerminalTime(item, now) : null,
                    ExternalSource = AdoMapper.SourceName,
                    ExternalId = item.ExternalId,
                    ExternalRev = item.Rev,
                    ExternalUrl = item.Url,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                writes.Add(new TodoWrite
                {
                    Row = row,
                    Op = "create",
                    ExpectedRevision = 0,
                    Tags = ParseAdoTags(item.Tags),
                    Actor = "import",
                });
                result.TodosCreated++;
                continue;
            }

            if (existing.ExternalRev == item.Rev
                && existing.Title == title
                && existing.Status == status
                && existing.Priority == priority
                && existing.Kind == kind
                && existing.IterationId == iterationId
                && existing.DeletedAt is null)
            {
                result.TodosSkipped++;
                continue;
            }

            var updated = CloneAdoTodo(existing);
            updated.Title = title;
            updated.Notes = notes;
            updated.Status = status;
            updated.Priority = priority;
            updated.ProjectId = projectId;
            updated.IterationId = iterationId;
            updated.Kind = kind;
            updated.ParentTodoId = parentId;
            updated.ExternalSource = AdoMapper.SourceName;
            updated.ExternalId = item.ExternalId;
            updated.ExternalRev = item.Rev;
            updated.ExternalUrl = item.Url;
            updated.Revision = existing.Revision + 1;
            updated.UpdatedAt = now;
            updated.CompletedAt = IsTerminalStatus(status)
                ? existing.CompletedAt ?? AdoTerminalTime(item, now)
                : null;

            writes.Add(new TodoWrite
            {
                Row = updated,
                Op = "update",
                ExpectedRevision = existing.Revision,
                Tags = _store.GetTodoTags(existing.Id),
                Links = _store.GetLinks("todo", [existing.Id]).GetValueOrDefault(existing.Id) ?? [],
                Actor = "import",
            });
            result.TodosUpdated++;
        }

        if (writes.Count > 0)
        {
            _store.WriteTodos(writes);
        }
    }

    private static DateTime AdoTerminalTime(AdoWorkItemDto item, DateTime now) =>
        item.ChangedAt?.ToUniversalTime().UtcDateTime ?? now;

    private static IReadOnlyList<string> ParseAdoTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return [];
        }

        return tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
    }

    private static string? SettingValue(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static ProjectRow CloneAdoProject(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Color = row.Color,
        Status = row.Status,
        StartAt = row.StartAt,
        TargetAt = row.TargetAt,
        Sort = row.Sort,
        Revision = row.Revision,
        ExternalSource = row.ExternalSource,
        ExternalId = row.ExternalId,
        ExternalUrl = row.ExternalUrl,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static IterationRow CloneAdoIteration(IterationRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ParentId = row.ParentId,
        Level = row.Level,
        Name = row.Name,
        Goal = row.Goal,
        Status = row.Status,
        StartAt = row.StartAt,
        EndAt = row.EndAt,
        CompletedAt = row.CompletedAt,
        CompletionNote = row.CompletionNote,
        Sort = row.Sort,
        Revision = row.Revision,
        ExternalSource = row.ExternalSource,
        ExternalId = row.ExternalId,
        ExternalUrl = row.ExternalUrl,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static TodoRow CloneAdoTodo(TodoRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Notes = row.Notes,
        Status = row.Status,
        Priority = row.Priority,
        DueAt = row.DueAt,
        CompletedAt = row.CompletedAt,
        CompletionNote = row.CompletionNote,
        ArtifactsJson = row.ArtifactsJson,
        ListId = row.ListId,
        ProjectId = row.ProjectId,
        IterationId = row.IterationId,
        Kind = row.Kind,
        ParentTodoId = row.ParentTodoId,
        Sort = row.Sort,
        Revision = row.Revision,
        Source = row.Source,
        SourceSessionId = row.SourceSessionId,
        SourceExcerpt = row.SourceExcerpt,
        ExternalSource = row.ExternalSource,
        ExternalId = row.ExternalId,
        ExternalRev = row.ExternalRev,
        ExternalUrl = row.ExternalUrl,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        RestorableUntil = row.RestorableUntil,
        FtsRowId = row.FtsRowId,
    };

    // ── 单条工作项：拉取落库 / 推送后回写 rev ────────────────────────────────

    /// <summary>
    /// 单条工作项（任务详情的「从云端同步」）：按 <c>(ExternalSource, ExternalId)</c> upsert，
    /// 迭代按路径末段名字匹配本地迭代（单条拉取没有完整迭代树可用）。
    /// </summary>
    public TodoDto ApplyAdoWorkItem(AdoWorkItemDto item, string projectId, WriteContext? context = null)
    {
        var now = NowUtc();
        var status = AdoMapper.ToStorageStatus(item.State);
        var kind = AdoMapper.ToStorageKind(item.Type);
        var priority = AdoMapper.ToPriority(item.AdoPriority);
        var notes = string.IsNullOrWhiteSpace(item.Notes) ? null : NormalizeNotes(item.Notes);
        var title = RequireTitle(item.Title);
        var iterationId = MatchAdoIterationId(projectId, item.IterationPath);

        var existing = _store.GetTodoByExternal(AdoMapper.SourceName, item.ExternalId);
        if (existing is null)
        {
            var row = new TodoRow
            {
                Id = NewId("td"),
                Title = title,
                Notes = notes,
                Status = status,
                Priority = priority,
                ListId = PlannerStore.DefaultListId,
                ProjectId = projectId,
                IterationId = iterationId,
                Kind = kind,
                ParentTodoId = null,
                Sort = 0,
                Revision = 1,
                Source = "import",
                CompletedAt = IsTerminalStatus(status) ? AdoTerminalTime(item, now) : null,
                ExternalSource = AdoMapper.SourceName,
                ExternalId = item.ExternalId,
                ExternalRev = item.Rev,
                ExternalUrl = item.Url,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _store.WriteTodos([new TodoWrite
            {
                Row = row,
                Op = "create",
                ExpectedRevision = 0,
                Tags = ParseAdoTags(item.Tags),
                Actor = "import",
            }]);
            RaiseChanged();
            return GetTodo(row.Id) ?? throw PlannerException.NotFound("同步后没读到任务");
        }

        var updated = CloneAdoTodo(existing);
        updated.Title = title;
        updated.Notes = notes;
        updated.Status = status;
        updated.Priority = priority;
        updated.ProjectId = projectId;
        updated.IterationId = iterationId;
        updated.Kind = kind;
        updated.ExternalSource = AdoMapper.SourceName;
        updated.ExternalId = item.ExternalId;
        updated.ExternalRev = item.Rev;
        updated.ExternalUrl = item.Url;
        updated.Revision = existing.Revision + 1;
        updated.UpdatedAt = now;
        updated.CompletedAt = IsTerminalStatus(status)
            ? existing.CompletedAt ?? AdoTerminalTime(item, now)
            : null;

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = existing.Revision,
            Tags = _store.GetTodoTags(existing.Id),
            Links = _store.GetLinks("todo", [existing.Id]).GetValueOrDefault(existing.Id) ?? [],
            Actor = "import",
        }]);
        RaiseChanged();
        return GetTodo(updated.Id) ?? throw PlannerException.NotFound("同步后没读到任务");
    }

    /// <summary>推送成功后只更新外部字段（rev / 链接），推送方向以事序为准，不改本地内容。</summary>
    internal void MarkAdoPushed(string todoId, string externalId, int? rev, string? url)
    {
        _store.UpdateTodoExternal(todoId, AdoMapper.SourceName, externalId, rev, url, NowUtc());
        RaiseChanged();
    }

    /// <summary>工作项的迭代路径（末段名字）匹配本地迭代：小迭代优先，其次大迭代。</summary>
    private string? MatchAdoIterationId(string projectId, string? iterationPath)
    {
        var segments = AdoMapper.SplitPath(iterationPath);
        if (segments.Length == 0)
        {
            return null;
        }

        var name = segments[^1];
        var candidates = _store.QueryIterations(projectId)
            .Where(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.FirstOrDefault(row => row.Level == "small")?.Id
            ?? candidates.FirstOrDefault()?.Id;
    }
}