using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 领域门面：UI 与 MCP 都只调这里。**零模型凭证**——本类里不出现任何模型 / Key / HttpClient。
///
/// 关键约定：
/// 1. 所有写操作走 <see cref="PlannerStore"/>，一个事务同时落正式表 + FTS + outbox + op_log；
/// 2. 更新/删除/完成必须带 expectedRevision，冲突抛 <see cref="PlannerException.Conflict"/>；
/// 3. 草稿只落草稿区，采纳才写 todo（PL-09）；
/// 4. 时间一律 UTC 进、UTC 出，显示层再转本地。
/// </summary>
public sealed class PlannerService
{
    private readonly PlannerStore _store;
    private readonly TimeProvider _time;

    public PlannerService(PlannerStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>任何成功写入后触发；UI 订阅它刷新列表与组件。</summary>
    public event EventHandler? Changed;

    public PlannerStore Store => _store;

    // ── 清单 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ListDto> GetLists() =>
        _store.GetLists().Select(PlannerMapper.ToList).ToList();

    public ListDto CreateList(string name, string? color = null)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 100)
        {
            throw PlannerException.Validation("清单名必须在 1–100 字符之间");
        }

        if (color is { Length: > 32 })
        {
            throw PlannerException.Validation("颜色值超过 32 字符");
        }

        var row = new ListRow
        {
            Id = NewId("li"),
            Name = trimmed,
            Color = color,
            IsDefault = false,
            Sort = _store.GetLists().Count,
            CreatedAt = NowUtc(),
            UpdatedAt = NowUtc(),
        };

        _store.InsertList(row);
        return PlannerMapper.ToList(row);
    }

    // ── 待办读 ────────────────────────────────────────────────────────────

    public TodoDto? GetTodo(string id, bool includeDeleted = false)
    {
        var row = _store.GetTodo(id, includeDeleted);
        if (row is null)
        {
            return null;
        }

        return MapTodos([row], includeDeleted).First();
    }

    public IReadOnlyList<TodoDto> ListTodos(TodoQuery query)
    {
        var status = NormalizeStatus(query.Status);
        var tagId = query.Tag is { Length: > 0 } tag ? _store.FindTagId(tag) : null;
        if (query.Tag is { Length: > 0 } && tagId is null)
        {
            return [];
        }

        var rows = _store.QueryTodos(new TodoFilter
        {
            Status = status,
            ListId = query.ListId,
            DueBeforeUtc = PlannerMapper.ToUtc(query.DueBefore),
            DueAfterUtc = PlannerMapper.ToUtc(query.DueAfter),
            TagId = tagId,
            Keyword = query.Q?.Trim(),
            Limit = Math.Clamp(query.Limit, 1, PlannerConstants.MaxListLimit),
            IncludeDeleted = query.IncludeDeleted,
        });

        return MapTodos(rows);
    }

    public IReadOnlyList<TodoDto> ListDeletedTodos(int limit = PlannerConstants.MaxListLimit) =>
        MapTodos(_store.QueryDeletedTodos(limit), includeDeleted: true);

    /// <summary>回收站里的日程（与 todo 分开，接口也分开）。</summary>
    public IReadOnlyList<EventDto> ListDeletedEvents(int limit = PlannerConstants.MaxListLimit) =>
        MapEvents(_store.QueryDeletedEvents(limit));

    public StatsDto Stats()
    {
        var nowUtc = NowUtc();
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local).Date;
        var startTodayUtc = DateTime.SpecifyKind(localToday, DateTimeKind.Unspecified).ToUniversalTime();
        var startTomorrowUtc = DateTime.SpecifyKind(localToday.AddDays(1), DateTimeKind.Unspecified).ToUniversalTime();
        var sevenDaysAgo = nowUtc.AddDays(-7);

        var raw = _store.GetStats(startTodayUtc, startTomorrowUtc, sevenDaysAgo);
        var rate = raw.CreatedLast7Days == 0
            ? 0
            : Math.Round((double)raw.DoneLast7Days / raw.CreatedLast7Days, 4);

        return new StatsDto
        {
            DueToday = raw.DueToday,
            Overdue = raw.Overdue,
            OpenTotal = raw.OpenTotal,
            DoneToday = raw.DoneToday,
            DoneLast7Days = raw.DoneLast7Days,
            CreatedLast7Days = raw.CreatedLast7Days,
            CompletionRateLast7Days = rate,
            ByList = raw.ByList.Select(x => new ListCountDto
            {
                ListId = x.ListId,
                ListName = x.ListName,
                OpenCount = x.OpenCount,
            }).ToList(),
        };
    }

    // ── 待办写 ────────────────────────────────────────────────────────────

    public TodoBatchResult CreateTodos(IReadOnlyList<TodoCreateInput> items, WriteContext context)
    {
        if (items.Count == 0)
        {
            throw PlannerException.Validation("至少需要一条待办");
        }

        if (items.Count > PlannerConstants.MaxCreateItems)
        {
            throw PlannerException.Limit($"单次最多创建 {PlannerConstants.MaxCreateItems} 条");
        }

        var now = NowUtc();
        var warnings = new List<string>();
        var candidates = _store.QueryOpenTitles(200)
            .Select(c => (c.Id, c.Title, c.Notes))
            .ToList();
        var pendingTitles = new HashSet<string>(StringComparer.Ordinal);
        var writes = new List<TodoWrite>();

        foreach (var input in items)
        {
            var title = RequireTitle(input.Title);
            var normalized = Similarity.Normalize(title);
            if (!pendingTitles.Add(normalized))
            {
                warnings.Add($"「{title}」在本批次中重复，已跳过");
                continue;
            }

            var duplicate = Similarity.Find(title, candidates);
            if (duplicate is { Exact: true } match && context.Source is TodoSource.Ai or TodoSource.Meeting)
            {
                throw PlannerException.DuplicateSuspect(title, match.Id);
            }

            if (duplicate is { Exact: false } suspect)
            {
                warnings.Add($"「{title}」与「{suspect.Title}」疑似重复");
            }

            var row = new TodoRow
            {
                Id = NewId("td"),
                Title = title,
                Notes = NormalizeNotes(input.Notes),
                Status = "open",
                Priority = NormalizePriority(input.Priority),
                DueAt = PlannerMapper.ToUtc(input.DueAt),
                ListId = EnsureList(input.ListId),
                Revision = 1,
                Source = PlannerMapper.ToStorage(context.Source),
                SourceSessionId = NormalizeSession(context.SessionId),
                SourceExcerpt = NormalizeExcerpt(context.Excerpt),
                CreatedAt = now,
                UpdatedAt = now,
            };

            writes.Add(new TodoWrite
            {
                Row = row,
                Op = "create",
                ExpectedRevision = 0,
                Tags = NormalizeTags(input.Tags),
                Links = NormalizeLinks(input.Links),
                Actor = ActorOf(context.Source),
                RequestId = context.RequestId,
            });

            candidates.Add((row.Id, row.Title, row.Notes));
        }

        if (writes.Count == 0)
        {
            throw PlannerException.Validation("没有任何可写入的条目");
        }

        _store.WriteTodos(writes);
        RaiseChanged();

        return new TodoBatchResult(MapTodos(writes.Select(w => w.Row).ToList()), warnings);
    }

    public TodoWriteResult UpdateTodo(string id, int expectedRevision, TodoPatch patch, WriteContext context)
    {
        var row = _store.GetTodo(id) ?? throw PlannerException.NotFound($"没有找到待办「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var existingTags = _store.GetTodoTags(id);
        var existingLinks = _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [];
        var updated = CloneTodo(row);
        ApplyTodoPatch(updated, patch, NowUtc());

        var warnings = new List<string>();
        if (patch.Title is not null)
        {
            var duplicate = Similarity.Find(updated.Title, _store.QueryOpenTitles(200)
                .Where(c => c.Id != id)
                .Select(c => (c.Id, c.Title, c.Notes))
                .ToList());
            if (duplicate is { Exact: false } suspect)
            {
                warnings.Add($"「{updated.Title}」与「{suspect.Title}」疑似重复");
            }
        }

        updated.Revision = row.Revision + 1;
        updated.UpdatedAt = NowUtc();

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            Tags = patch.Tags is null ? existingTags : NormalizeTags(patch.Tags),
            Links = patch.Links is null ? existingLinks : NormalizeLinks(patch.Links),
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new TodoWriteResult(MapTodos([updated]).First(), warnings);
    }

    /// <summary>勾完成是幂等的：已完成再调一次原样返回，不涨 revision。</summary>
    public TodoWriteResult CompleteTodo(string id, int expectedRevision, WriteContext context)
    {
        var row = _store.GetTodo(id) ?? throw PlannerException.NotFound($"没有找到待办「{id}」");
        if (row.Status == "done")
        {
            return new TodoWriteResult(MapTodos([row]).First(), []);
        }

        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var updated = CloneTodo(row);
        updated.Status = "done";
        updated.CompletedAt = now;
        updated.UpdatedAt = now;
        updated.Revision = row.Revision + 1;

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "complete",
            ExpectedRevision = expectedRevision,
            Tags = _store.GetTodoTags(id),
            Links = _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new TodoWriteResult(MapTodos([updated]).First(), []);
    }

    public TodoWriteResult ReopenTodo(string id, int expectedRevision, WriteContext context)
    {
        var row = _store.GetTodo(id) ?? throw PlannerException.NotFound($"没有找到待办「{id}」");
        if (row.Status != "done")
        {
            return new TodoWriteResult(MapTodos([row]).First(), []);
        }

        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var updated = CloneTodo(row);
        updated.Status = "open";
        updated.CompletedAt = null;
        updated.UpdatedAt = now;
        updated.Revision = row.Revision + 1;

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "reopen",
            ExpectedRevision = expectedRevision,
            Tags = _store.GetTodoTags(id),
            Links = _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new TodoWriteResult(MapTodos([updated]).First(), []);
    }

    public DeleteTodoResult DeleteTodo(string id, int expectedRevision, string? reason, WriteContext context)
    {
        var row = _store.GetTodo(id) ?? throw PlannerException.NotFound($"没有找到待办「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var restorableUntil = now + PlannerConstants.RecycleBinRetention;
        var updated = CloneTodo(row);
        updated.DeletedAt = now;
        updated.RestorableUntil = restorableUntil;
        updated.UpdatedAt = now;
        updated.Revision = row.Revision + 1;

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "delete",
            ExpectedRevision = expectedRevision,
            Tags = _store.GetTodoTags(id),
            Links = _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
            Reason = string.IsNullOrWhiteSpace(reason) ? "未填写原因" : reason.Trim(),
        }]);
        RaiseChanged();

        return new DeleteTodoResult(id, new DateTimeOffset(now, TimeSpan.Zero), new DateTimeOffset(restorableUntil, TimeSpan.Zero), updated.Revision);
    }

    public TodoWriteResult RestoreTodo(string id, WriteContext context)
    {
        var row = _store.GetTodoIncludingDeleted(id) ?? throw PlannerException.NotFound($"没有找到待办「{id}」");
        if (row.DeletedAt is null)
        {
            throw PlannerException.Validation("该条目没有被删除，无需恢复");
        }

        if (row.RestorableUntil is not null && row.RestorableUntil < NowUtc())
        {
            throw PlannerException.NotFound("回收站保留期已过，条目已被清理");
        }

        var now = NowUtc();
        var updated = CloneTodo(row);
        updated.DeletedAt = null;
        updated.RestorableUntil = null;
        updated.UpdatedAt = now;
        updated.Revision = row.Revision + 1;

        _store.WriteTodos([new TodoWrite
        {
            Row = updated,
            Op = "restore",
            ExpectedRevision = row.Revision,
            Tags = _store.GetTodoTags(id),
            Links = _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new TodoWriteResult(MapTodos([updated]).First(), []);
    }

    public BulkTodoResult BulkUpdate(IReadOnlyList<string> ids, TodoPatch patch, WriteContext context)
    {
        if (ids.Count == 0)
        {
            throw PlannerException.Validation("ids 为空");
        }

        if (ids.Count > PlannerConstants.MaxListLimit)
        {
            throw PlannerException.Limit($"单次最多批量修改 {PlannerConstants.MaxListLimit} 条");
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var now = NowUtc();
        var warnings = new List<string>();
        var writes = new List<TodoWrite>();

        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var row = _store.GetTodo(id);
            if (row is null)
            {
                warnings.Add($"跳过不存在的条目 {id}");
                continue;
            }

            var updated = CloneTodo(row);
            ApplyTodoPatch(updated, patch, now);
            updated.Revision = row.Revision + 1;
            updated.UpdatedAt = now;

            writes.Add(new TodoWrite
            {
                Row = updated,
                Op = "bulk_update",
                ExpectedRevision = row.Revision,
                Tags = patch.Tags is null ? _store.GetTodoTags(id) : NormalizeTags(patch.Tags),
                Links = patch.Links is null ? _store.GetLinks("todo", [id]).GetValueOrDefault(id) ?? [] : NormalizeLinks(patch.Links),
                Actor = ActorOf(context.Source),
                RequestId = context.RequestId,
            });
        }

        if (writes.Count == 0)
        {
            throw PlannerException.NotFound("没有找到可修改的条目");
        }

        _store.WriteTodos(writes);
        RaiseChanged();

        return new BulkTodoResult(
            writes.Count,
            MapTodos(writes.Select(w => w.Row).ToList()),
            warnings);
    }

    public ClearListResult ClearList(string listId, WriteContext context)
    {
        var list = _store.GetList(listId) ?? throw PlannerException.NotFound($"没有找到清单「{listId}」");
        var rows = _store.QueryTodos(new TodoFilter
        {
            Status = "all",
            ListId = listId,
            Limit = PlannerConstants.MaxListLimit,
        });

        if (rows.Count == 0)
        {
            return new ClearListResult(0, list.Id);
        }

        var now = NowUtc();
        var restorableUntil = now + PlannerConstants.RecycleBinRetention;
        var writes = rows.Select(row =>
        {
            var updated = CloneTodo(row);
            updated.DeletedAt = now;
            updated.RestorableUntil = restorableUntil;
            updated.UpdatedAt = now;
            updated.Revision = row.Revision + 1;
            return new TodoWrite
            {
                Row = updated,
                Op = "clear_list",
                ExpectedRevision = row.Revision,
                Tags = _store.GetTodoTags(row.Id),
                Links = _store.GetLinks("todo", [row.Id]).GetValueOrDefault(row.Id) ?? [],
                Actor = ActorOf(context.Source),
                RequestId = context.RequestId,
                Reason = $"清空清单「{list.Name}」",
            };
        }).ToList();

        _store.WriteTodos(writes);
        RaiseChanged();
        return new ClearListResult(writes.Count, list.Id);
    }

    // ── 搜索 ──────────────────────────────────────────────────────────────

    public SearchResultDto Search(string? q, int limit = PlannerConstants.DefaultListLimit)
    {
        var query = (q ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return new SearchResultDto { Query = string.Empty };
        }

        var tokens = TodoFts.TokenizeQuery(query);
        if (tokens.Count == 0)
        {
            return new SearchResultDto { Query = query };
        }

        var bounded = Math.Clamp(limit, 1, PlannerConstants.MaxListLimit);
        var hits = _store.SearchTodos(TodoFts.AndQuery(tokens), bounded);
        var widened = false;

        if (hits.Count == 0 && tokens.Count > 1)
        {
            hits = _store.SearchTodos(TodoFts.OrQuery(tokens), bounded);
            widened = true;
        }

        if (hits.Count == 0 && tokens.Any(t => t.Length == 1))
        {
            hits = _store.SearchTodosLike(tokens[0], bounded);
            widened = true;
        }

        var results = new List<SearchHitDto>();
        foreach (var hit in hits)
        {
            var row = _store.GetTodo(hit.Id);
            if (row is null)
            {
                continue;
            }

            results.Add(new SearchHitDto
            {
                Id = row.Id,
                Title = row.Title,
                Snippet = BuildSnippet(row, query, tokens),
                DueAt = PlannerMapper.ToDto(row.DueAt),
                Status = PlannerMapper.ParseStatus(row.Status),
                ListId = row.ListId,
                Score = hit.Score,
            });
        }

        return new SearchResultDto
        {
            Query = query,
            Hits = results,
            Widened = widened,
        };
    }

    // ── 日程 ──────────────────────────────────────────────────────────────

    public EventDto? GetEvent(string id) => _store.GetEvent(id) is { } row ? MapEvents([row]).First() : null;

    public IReadOnlyList<EventDto> ListEvents(DateTimeOffset from, DateTimeOffset to)
    {
        var (fromUtc, toUtc) = NormalizeRange(from, to);
        return MapEvents(_store.QueryEvents(fromUtc.UtcDateTime, toUtc.UtcDateTime));
    }

    public CalendarRangeDto CalendarRange(DateTimeOffset from, DateTimeOffset to)
    {
        var (fromUtc, toUtc) = NormalizeRange(from, to);
        var todos = _store.QueryTodosDueRange(fromUtc.UtcDateTime, toUtc.UtcDateTime);
        var events = _store.QueryEvents(fromUtc.UtcDateTime, toUtc.UtcDateTime);

        return new CalendarRangeDto
        {
            From = fromUtc,
            To = toUtc,
            Todos = MapTodos(todos),
            Events = MapEvents(events),
        };
    }

    public EventWriteResult CreateEvent(EventCreateInput input, WriteContext context)
    {
        var title = RequireTitle(input.Title);
        var (start, end) = NormalizeEventTimes(input.StartAt, input.EndAt);
        EnsureLinkedTodo(input.LinkedTodoId);
        var now = NowUtc();

        var row = new EventRow
        {
            Id = NewId("ev"),
            Title = title,
            Notes = NormalizeNotes(input.Notes),
            StartAt = start.UtcDateTime,
            EndAt = end.UtcDateTime,
            Location = NormalizeLocation(input.Location),
            ParticipantsJson = PlannerMapper.SerializeParticipants(NormalizeParticipants(input.Participants)),
            LinkedTodoId = input.LinkedTodoId,
            ListId = EnsureList(input.ListId),
            Revision = 1,
            Source = PlannerMapper.ToStorage(context.Source),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var conflicts = DetectEventConflicts(null, row);
        _store.WriteEvents([new EventWrite
        {
            Row = row,
            Op = "create",
            ExpectedRevision = 0,
            Links = NormalizeLinks(input.Links),
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new EventWriteResult(MapEvents([row]).First(), conflicts);
    }

    public EventWriteResult UpdateEvent(string id, int expectedRevision, EventPatch patch, WriteContext context)
    {
        var row = _store.GetEvent(id) ?? throw PlannerException.NotFound($"没有找到日程「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var existingLinks = _store.GetLinks("event", [id]).GetValueOrDefault(id) ?? [];
        var updated = CloneEvent(row);
        var now = NowUtc();

        if (patch.Title is not null)
        {
            updated.Title = RequireTitle(patch.Title);
        }

        if (patch.ClearNotes)
        {
            updated.Notes = null;
        }
        else if (patch.Notes is not null)
        {
            updated.Notes = NormalizeNotes(patch.Notes);
        }

        var start = patch.StartAt is null ? ToOffset(row.StartAt) : patch.StartAt.Value.ToUniversalTime();
        var end = patch.EndAt is null ? ToOffset(row.EndAt) : patch.EndAt.Value.ToUniversalTime();
        var (normalizedStart, normalizedEnd) = NormalizeEventTimes(start, end);
        updated.StartAt = normalizedStart.UtcDateTime;
        updated.EndAt = normalizedEnd.UtcDateTime;

        if (patch.ClearLocation)
        {
            updated.Location = null;
        }
        else if (patch.Location is not null)
        {
            updated.Location = NormalizeLocation(patch.Location);
        }

        if (patch.Participants is not null)
        {
            updated.ParticipantsJson = PlannerMapper.SerializeParticipants(NormalizeParticipants(patch.Participants));
        }

        if (patch.ClearLinkedTodo)
        {
            updated.LinkedTodoId = null;
        }
        else if (patch.LinkedTodoId is not null)
        {
            EnsureLinkedTodo(patch.LinkedTodoId);
            updated.LinkedTodoId = patch.LinkedTodoId;
        }

        if (patch.ListId is not null)
        {
            updated.ListId = EnsureList(patch.ListId);
        }

        updated.Revision = row.Revision + 1;
        updated.UpdatedAt = now;

        var conflicts = DetectEventConflicts(id, updated);
        _store.WriteEvents([new EventWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            Links = patch.Links is null ? existingLinks : NormalizeLinks(patch.Links),
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new EventWriteResult(MapEvents([updated]).First(), conflicts);
    }

    public EventDeleteResult DeleteEvent(string id, int expectedRevision, string? reason, WriteContext context)
    {
        var row = _store.GetEvent(id) ?? throw PlannerException.NotFound($"没有找到日程「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var restorableUntil = now + PlannerConstants.RecycleBinRetention;
        var updated = CloneEvent(row);
        updated.DeletedAt = now;
        updated.RestorableUntil = restorableUntil;
        updated.UpdatedAt = now;
        updated.Revision = row.Revision + 1;

        _store.WriteEvents([new EventWrite
        {
            Row = updated,
            Op = "delete",
            ExpectedRevision = expectedRevision,
            Links = _store.GetLinks("event", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
            Reason = string.IsNullOrWhiteSpace(reason) ? "未填写原因" : reason.Trim(),
        }]);
        RaiseChanged();

        return new EventDeleteResult(id, new DateTimeOffset(now, TimeSpan.Zero), new DateTimeOffset(restorableUntil, TimeSpan.Zero), updated.Revision);
    }

    public EventWriteResult RestoreEvent(string id, WriteContext context)
    {
        var row = _store.GetEventIncludingDeleted(id) ?? throw PlannerException.NotFound($"没有找到日程「{id}」");
        if (row.DeletedAt is null)
        {
            throw PlannerException.Validation("该日程没有被删除，无需恢复");
        }

        if (row.RestorableUntil is not null && row.RestorableUntil < NowUtc())
        {
            throw PlannerException.NotFound("回收站保留期已过，条目已被清理");
        }

        var updated = CloneEvent(row);
        updated.DeletedAt = null;
        updated.RestorableUntil = null;
        updated.UpdatedAt = NowUtc();
        updated.Revision = row.Revision + 1;

        _store.WriteEvents([new EventWrite
        {
            Row = updated,
            Op = "restore",
            ExpectedRevision = row.Revision,
            Links = _store.GetLinks("event", [id]).GetValueOrDefault(id) ?? [],
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        }]);
        RaiseChanged();

        return new EventWriteResult(MapEvents([updated]).First(), DetectEventConflicts(id, updated));
    }

    // ── 草稿（AI 初版 → 人审阅）──────────────────────────────────────────

    public DraftDto PlanFromText(string text, string? hint, string? listId, WriteContext context)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw PlannerException.Validation("text 为空");
        }

        if (raw.Length > PlannerConstants.MaxPlanTextChars)
        {
            throw PlannerException.Limit($"text 超过 {PlannerConstants.MaxPlanTextChars} 字符");
        }

        var segments = DraftTextSplitter.Split(raw);
        if (segments.Count == 0)
        {
            throw PlannerException.Validation("没有可拆分的条目");
        }

        var resolvedList = EnsureList(listId);
        var now = NowUtc();
        var draftId = NewId("dr");
        var candidates = _store.QueryOpenTitles(200)
            .Select(c => (c.Id, c.Title, c.Notes))
            .ToList();

        var items = new List<DraftItemRow>();
        var questions = new List<string>();
        var truncated = 0;
        var suspects = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            var (title, excerpt) = segments[index];
            if (title.Length >= PlannerConstants.MaxTitleChars)
            {
                truncated++;
            }

            var duplicate = Similarity.Find(title, candidates);
            if (duplicate is not null)
            {
                suspects++;
            }

            items.Add(new DraftItemRow
            {
                Id = NewId("di"),
                DraftId = draftId,
                Title = title,
                Notes = hint is { Length: > 0 } && index == 0 ? hint.Trim() : null,
                DueAt = null,
                Priority = 2,
                Excerpt = excerpt,
                SuspectDuplicateOf = duplicate?.Id,
                Sort = index,
                Decision = "pending",
            });
        }

        if (truncated > 0)
        {
            questions.Add($"{truncated} 条标题超过 {PlannerConstants.MaxTitleChars} 字，已截断，采纳前可编辑");
        }

        if (suspects > 0)
        {
            questions.Add($"{suspects} 条与已有条目疑似重复，请逐条确认");
        }

        if (items.Count >= PlannerConstants.DraftAcceptApprovalThreshold)
        {
            questions.Add($"本次草稿共 {items.Count} 条，采纳 ≥{PlannerConstants.DraftAcceptApprovalThreshold} 条会触发一次确认");
        }

        if (raw.Length > 4000)
        {
            questions.Add("原文较长，只保留每条待办对应的片段作为来源");
        }

        var draftRow = new DraftRow
        {
            Id = draftId,
            Status = "pending",
            Source = PlannerMapper.ToStorage(context.Source is TodoSource.Manual ? TodoSource.Ai : context.Source),
            SourceSessionId = NormalizeSession(context.SessionId),
            ListId = resolvedList,
            CreatedAt = now,
        };

        _store.InsertDraft(draftRow, items);
        return PlannerMapper.ToDraft(draftRow, items, questions);
    }

    public DraftDto? GetDraft(string draftId)
    {
        var draft = _store.GetDraft(draftId);
        if (draft is null)
        {
            return null;
        }

        return PlannerMapper.ToDraft(draft, _store.GetDraftItems(draftId), []);
    }

    public IReadOnlyList<DraftDto> ListDrafts(bool pendingOnly = true) =>
        _store.QueryDrafts(pendingOnly, 20)
            .Select(d => PlannerMapper.ToDraft(d, _store.GetDraftItems(d.Id), []))
            .ToList();

    public DraftAcceptResult AcceptDraft(DraftAcceptRequest request, WriteContext context)
    {
        var draft = _store.GetDraft(request.DraftId) ?? throw PlannerException.DraftNotFound(request.DraftId);
        if (draft.Status != "pending")
        {
            throw PlannerException.Validation("该草稿已被处理，不能重复采纳");
        }

        var items = _store.GetDraftItems(request.DraftId);
        if (items.Count == 0)
        {
            throw PlannerException.Validation("草稿没有条目");
        }

        var decisions = (request.Items ?? [])
            .ToDictionary(i => i.DraftItemId, StringComparer.Ordinal);

        var knownIds = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in decisions.Keys.Where(id => !knownIds.Contains(id)))
        {
            throw PlannerException.Validation($"草稿里没有条目「{unknown}」");
        }

        var now = NowUtc();
        var warnings = new List<string>();
        var writes = new List<TodoWrite>();
        var accepted = new List<DraftItemRow>();
        var rejected = 0;

        foreach (var item in items)
        {
            var decision = decisions.GetValueOrDefault(item.Id);
            if (decision is { Reject: true })
            {
                item.Decision = "rejected";
                rejected++;
                continue;
            }

            var title = RequireTitle(decision?.Title ?? item.Title);
            var dueAt = decision?.DueAt is { } chosenDue
                ? chosenDue.ToUniversalTime()
                : PlannerMapper.ToDto(item.DueAt);

            var row = new TodoRow
            {
                Id = NewId("td"),
                Title = title,
                Notes = NormalizeNotes(decision?.Notes ?? item.Notes),
                Status = "open",
                Priority = NormalizePriority(decision?.Priority ?? item.Priority),
                DueAt = PlannerMapper.ToUtc(dueAt),
                ListId = EnsureList(request.ListId ?? draft.ListId),
                Revision = 1,
                Source = draft.Source,
                SourceSessionId = draft.SourceSessionId,
                SourceExcerpt = NormalizeExcerpt(item.Excerpt),
                CreatedAt = now,
                UpdatedAt = now,
            };

            if (item.SuspectDuplicateOf is not null)
            {
                warnings.Add($"「{row.Title}」曾被判为疑似重复，已按用户采纳写入");
            }

            writes.Add(new TodoWrite
            {
                Row = row,
                Op = "accept_draft",
                ExpectedRevision = 0,
                Actor = ActorOf(PlannerMapper.ParseSource(draft.Source)),
                RequestId = context.RequestId,
            });

            item.Decision = "accepted";
            accepted.Add(item);
        }

        var acceptedAny = writes.Count > 0;
        var updatedDraft = new DraftRow
        {
            Id = draft.Id,
            Status = acceptedAny ? "accepted" : "rejected",
            Source = draft.Source,
            SourceSessionId = draft.SourceSessionId,
            ListId = request.ListId ?? draft.ListId,
            CreatedAt = draft.CreatedAt,
            AcceptedAt = now,
        };

        // 先把草稿状态与逐条决策落库，再写正式表；两者在同一事务里。
        _store.AcceptDraft(updatedDraft, items, writes);
        RaiseChanged();

        var created = MapTodos(writes.Select(w => w.Row).ToList());
        return new DraftAcceptResult(draft.Id, acceptedAny, created, rejected, warnings);
    }

    public DraftDto RejectDraft(string draftId, WriteContext context)
    {
        var draft = _store.GetDraft(draftId) ?? throw PlannerException.DraftNotFound(draftId);
        if (draft.Status != "pending")
        {
            throw PlannerException.Validation("该草稿已被处理，不能重复拒绝");
        }

        var items = _store.GetDraftItems(draftId);
        foreach (var item in items)
        {
            item.Decision = "rejected";
        }

        var updated = new DraftRow
        {
            Id = draft.Id,
            Status = "rejected",
            Source = draft.Source,
            SourceSessionId = draft.SourceSessionId,
            ListId = draft.ListId,
            CreatedAt = draft.CreatedAt,
            AcceptedAt = NowUtc(),
        };

        _store.RejectDraft(updated, items);
        RaiseChanged();
        return PlannerMapper.ToDraft(updated, items, []);
    }

    // ── ICS / 维护 ────────────────────────────────────────────────────────

    public string ExportIcs(IcsExportOptions? options = null)
    {
        var opts = options ?? new IcsExportOptions();
        var todos = _store.QueryAllTodos(opts.IncludeCompleted);
        var tags = _store.GetTags(todos.Select(t => t.Id).ToList());
        var events = _store.QueryAllEvents();
        return IcsExporter.Export(todos, tags, events, opts, _time.GetUtcNow());
    }

    public string ExportIcsToFile(string path, IcsExportOptions? options = null)
    {
        var content = ExportIcs(options);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    /// <summary>回收站到期清理；返回被物理删除的条数。</summary>
    public int PurgeExpired()
    {
        var count = _store.PurgeExpired(NowUtc());
        if (count > 0)
        {
            RaiseChanged();
        }

        return count;
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private DateTime NowUtc() => _time.GetUtcNow().UtcDateTime;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..20];

    private static string ActorOf(TodoSource source) => source switch
    {
        TodoSource.Ai => "ai",
        TodoSource.Meeting => "meeting",
        TodoSource.Import => "import",
        TodoSource.Ics => "ics",
        _ => "manual",
    };

    private static string NormalizeStatus(string? status)
    {
        var value = (status ?? "open").Trim().ToLowerInvariant();
        return value switch
        {
            "open" or "doing" or "done" or "all" or "deleted" => value,
            _ => throw PlannerException.Validation("status 只能是 open / doing / done / all"),
        };
    }

    private static string RequireTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw PlannerException.Validation("标题不能为空");
        }

        if (trimmed.Length > PlannerConstants.MaxTitleChars)
        {
            throw PlannerException.Validation($"标题超过 {PlannerConstants.MaxTitleChars} 字符");
        }

        return trimmed;
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var trimmed = notes.Trim();
        if (trimmed.Length > PlannerConstants.MaxNotesChars)
        {
            throw PlannerException.Validation($"备注超过 {PlannerConstants.MaxNotesChars} 字符");
        }

        return trimmed;
    }

    private static string? NormalizeSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var trimmed = sessionId.Trim();
        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    private static string? NormalizeExcerpt(string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return null;
        }

        var trimmed = excerpt.Trim();
        return trimmed.Length > 1024 ? trimmed[..1024] : trimmed;
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var trimmed = location.Trim();
        if (trimmed.Length > 256)
        {
            throw PlannerException.Validation("地点超过 256 字符");
        }

        return trimmed;
    }

    private static IReadOnlyList<string> NormalizeParticipants(IReadOnlyList<string>? participants)
    {
        if (participants is null || participants.Count == 0)
        {
            return [];
        }

        if (participants.Count > PlannerConstants.MaxEventParticipants)
        {
            throw PlannerException.Limit($"参与人最多 {PlannerConstants.MaxEventParticipants} 个");
        }

        return participants
            .Select(p => (p ?? string.Empty).Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.Length > 100 ? p[..100] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int NormalizePriority(int priority)
    {
        if (priority is < 0 or > 4)
        {
            throw PlannerException.Validation("priority 必须在 0–4 之间");
        }

        return priority;
    }

    /// <summary>标签：≤10 个、每个 ≤32 字符，小写去重。</summary>
    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        if (tags.Count > PlannerConstants.MaxTagsPerTodo)
        {
            throw PlannerException.Limit($"每个条目最多 {PlannerConstants.MaxTagsPerTodo} 个标签");
        }

        var result = new List<string>();
        foreach (var tag in tags)
        {
            var trimmed = (tag ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length > PlannerConstants.MaxTagChars)
            {
                throw PlannerException.Validation($"标签「{trimmed}」超过 {PlannerConstants.MaxTagChars} 字符");
            }

            if (!result.Contains(trimmed, StringComparer.Ordinal))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeLinks(IReadOnlyList<string>? links)
    {
        if (links is null || links.Count == 0)
        {
            return [];
        }

        if (links.Count > PlannerConstants.MaxLinksPerTodo)
        {
            throw PlannerException.Limit($"每个条目最多 {PlannerConstants.MaxLinksPerTodo} 个引用");
        }

        var result = new List<string>();
        foreach (var link in links)
        {
            var trimmed = (link ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!UriRef.IsAcceptableLink(trimmed, out var reason))
            {
                throw PlannerException.InvalidUri(trimmed, reason);
            }

            if (!result.Contains(trimmed, StringComparer.Ordinal))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private string EnsureList(string? listId)
    {
        if (string.IsNullOrWhiteSpace(listId))
        {
            return PlannerStore.DefaultListId;
        }

        return _store.GetList(listId.Trim())?.Id
               ?? throw PlannerException.NotFound($"没有找到清单「{listId}」");
    }

    private void EnsureLinkedTodo(string? linkedTodoId)
    {
        if (string.IsNullOrWhiteSpace(linkedTodoId))
        {
            return;
        }

        var linked = _store.GetTodo(linkedTodoId);
        if (linked is null)
        {
            throw PlannerException.NotFound($"没有找到关联待办「{linkedTodoId}」");
        }
    }

    private static (DateTimeOffset From, DateTimeOffset To) NormalizeRange(DateTimeOffset from, DateTimeOffset to)
    {
        var start = from.ToUniversalTime();
        var end = to.ToUniversalTime();
        if (end <= start)
        {
            throw PlannerException.Validation("区间结束必须晚于开始");
        }

        if (end - start > TimeSpan.FromDays(400))
        {
            throw PlannerException.Limit("单次查询区间不能超过 400 天");
        }

        return (start, end);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) NormalizeEventTimes(DateTimeOffset start, DateTimeOffset end)
    {
        var startUtc = start.ToUniversalTime();
        var endUtc = end.ToUniversalTime();
        if (endUtc <= startUtc)
        {
            throw PlannerException.Validation("日程结束时间必须晚于开始时间");
        }

        if (endUtc - startUtc > TimeSpan.FromDays(30))
        {
            throw PlannerException.Validation("单条日程不能超过 30 天");
        }

        return (startUtc, endUtc);
    }

    private void ApplyTodoPatch(TodoRow target, TodoPatch patch, DateTime now)
    {
        if (patch.Title is not null)
        {
            target.Title = RequireTitle(patch.Title);
        }

        if (patch.ClearNotes)
        {
            target.Notes = null;
        }
        else if (patch.Notes is not null)
        {
            target.Notes = NormalizeNotes(patch.Notes);
        }

        if (patch.Status is { } status)
        {
            target.Status = PlannerMapper.ToStorage(status);
            target.CompletedAt = status == TodoStatus.Done
                ? target.CompletedAt ?? now
                : null;
        }

        if (patch.Priority is { } priority)
        {
            target.Priority = NormalizePriority(priority);
        }

        if (patch.ClearDueAt)
        {
            target.DueAt = null;
        }
        else if (patch.DueAt is { } dueAt)
        {
            target.DueAt = dueAt.ToUniversalTime().UtcDateTime;
        }

        if (patch.ListId is not null)
        {
            target.ListId = EnsureList(patch.ListId);
        }
    }

    private IReadOnlyList<EventConflictDto> DetectEventConflicts(string? excludeId, EventRow candidate)
    {
        var existing = _store.QueryEvents(candidate.StartAt, candidate.EndAt);
        return existing
            .Where(e => excludeId is null || !string.Equals(e.Id, excludeId, StringComparison.Ordinal))
            .Where(e => e.StartAt < candidate.EndAt && e.EndAt > candidate.StartAt)
            .Select(e => new EventConflictDto(
                e.Id,
                e.Title,
                ToOffset(e.StartAt),
                ToOffset(e.EndAt)))
            .ToList();
    }

    private IReadOnlyList<TodoDto> MapTodos(IReadOnlyList<TodoRow> rows, bool includeDeleted = false)
    {
        var valid = includeDeleted ? rows.ToList() : rows.Where(r => r.DeletedAt is null).ToList();
        if (valid.Count == 0)
        {
            return [];
        }

        var ids = valid.Select(r => r.Id).ToList();
        var tags = _store.GetTags(ids);
        var links = _store.GetLinks("todo", ids);
        var lists = _store.GetLists().ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);

        return valid.Select(row => PlannerMapper.ToTodo(
            row,
            tags.GetValueOrDefault(row.Id) ?? [],
            links.GetValueOrDefault(row.Id) ?? [],
            row.ListId is null ? null : lists.GetValueOrDefault(row.ListId))).ToList();
    }

    private IReadOnlyList<EventDto> MapEvents(IReadOnlyList<EventRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var links = _store.GetLinks("event", ids);
        return rows.Select(row => PlannerMapper.ToEvent(
            row,
            links.GetValueOrDefault(row.Id) ?? [])).ToList();
    }

    private static TodoRow CloneTodo(TodoRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Notes = row.Notes,
        Status = row.Status,
        Priority = row.Priority,
        DueAt = row.DueAt,
        CompletedAt = row.CompletedAt,
        ListId = row.ListId,
        Revision = row.Revision,
        Source = row.Source,
        SourceSessionId = row.SourceSessionId,
        SourceExcerpt = row.SourceExcerpt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        RestorableUntil = row.RestorableUntil,
        FtsRowId = row.FtsRowId,
    };

    private static EventRow CloneEvent(EventRow row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Notes = row.Notes,
        StartAt = row.StartAt,
        EndAt = row.EndAt,
        Location = row.Location,
        ParticipantsJson = row.ParticipantsJson,
        LinkedTodoId = row.LinkedTodoId,
        ListId = row.ListId,
        Revision = row.Revision,
        Source = row.Source,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        RestorableUntil = row.RestorableUntil,
    };

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? BuildSnippet(TodoRow row, string query, IReadOnlyList<string> tokens)
    {
        var notes = row.Notes;
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var index = notes.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            foreach (var token in tokens)
            {
                index = notes.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    break;
                }
            }
        }

        if (index < 0)
        {
            return null;
        }

        var start = Math.Max(0, index - 30);
        var end = Math.Min(notes.Length, index + query.Length + 50);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < notes.Length ? "…" : string.Empty;
        return prefix + notes[start..end] + suffix;
    }
}
