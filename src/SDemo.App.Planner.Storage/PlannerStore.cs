using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// 唯一的持久化入口。每个公开方法自己开一个短生命周期 DbContext：
/// UI 线程与 MCP 监听线程会并发访问，长生命周期上下文不是线程安全的。
/// 写操作在**一个事务**里同时落正式表、tag/link、FTS 索引、outbox（P5 同步）与 op_log（审计/撤销）。
/// </summary>
public sealed partial class PlannerStore
{
    public const string DefaultListId = "li_inbox";
    public const string DefaultListName = "收件箱";

    private readonly DbContextOptions<PlannerDbContext> _options;

    public PlannerStore(string databasePath)
    {
        var resolved = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        DatabasePath = resolved;
        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite($"Data Source={resolved}")
            .Options;
    }

    public string DatabasePath { get; }

    public PlannerDbContext Open() => new(_options);

    /// <summary>启动时单实例迁移 + 默认清单种子。</summary>
    public void Migrate()
    {
        using var db = Open();
        db.Database.Migrate();

        if (db.Lists.Any())
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Lists.Add(new ListRow
        {
            Id = DefaultListId,
            Name = DefaultListName,
            Color = "#4C8DFF",
            IsDefault = true,
            Sort = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    // ── 读 ────────────────────────────────────────────────────────────────

    public TodoRow? GetTodo(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.Todos.AsNoTracking().FirstOrDefault(t => t.Id == id);
        if (row is null || (!includeDeleted && row.DeletedAt is not null))
        {
            return null;
        }

        return row;
    }

    public EventRow? GetEvent(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.Events.AsNoTracking().FirstOrDefault(e => e.Id == id);
        if (row is null || (!includeDeleted && row.DeletedAt is not null))
        {
            return null;
        }

        return row;
    }

    public EventRow? GetEventIncludingDeleted(string id)
    {
        using var db = Open();
        return db.Events.AsNoTracking().FirstOrDefault(e => e.Id == id);
    }

    public IReadOnlyList<EventRow> QueryDeletedEvents(int limit)
    {
        using var db = Open();
        return db.Events.AsNoTracking()
            .Where(e => e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .Take(Math.Clamp(limit, 1, PlannerConstants.MaxListLimit))
            .ToList();
    }

    public IReadOnlyList<TodoRow> QueryTodos(TodoFilter filter)
    {
        using var db = Open();
        var query = db.Todos.AsNoTracking();

        query = filter.IncludeDeleted
            ? filter.Status == "deleted"
                ? query.Where(t => t.DeletedAt != null)
                : query
            : query.Where(t => t.DeletedAt == null);

        if (filter.ListId is { Length: > 0 } listId)
        {
            query = query.Where(t => t.ListId == listId);
        }

        if (filter.ProjectId is { Length: > 0 } projectId)
        {
            query = query.Where(t => t.ProjectId == projectId);
        }

        if (filter.NoProject)
        {
            query = query.Where(t => t.ProjectId == null);
        }

        if (filter.IterationId is { Length: > 0 } iterationId)
        {
            query = query.Where(t => t.IterationId == iterationId);
        }

        if (filter.Kind is { Length: > 0 } kind)
        {
            query = query.Where(t => t.Kind == kind);
        }

        switch (filter.Status)
        {
            case "open":
            case "active":
                query = query.Where(t => t.Status == "open" || t.Status == "doing");
                break;
            case "new":
                query = query.Where(t => t.Status == "open");
                break;
            case "doing":
                query = query.Where(t => t.Status == "doing");
                break;
            case "done":
                query = query.Where(t => t.Status == "done");
                break;
            case "verified":
                query = query.Where(t => t.Status == "verified");
                break;
            case "closed":
                query = query.Where(t => t.Status == "closed");
                break;
            case "canceled":
                query = query.Where(t => t.Status == "canceled");
                break;
        }

        if (filter.DueBeforeUtc is { } before)
        {
            query = query.Where(t => t.DueAt != null && t.DueAt < before);
        }

        if (filter.DueAfterUtc is { } after)
        {
            query = query.Where(t => t.DueAt != null && t.DueAt >= after);
        }

        if (filter.TagId is { Length: > 0 } tagId)
        {
            var tagged = db.TodoTags.AsNoTracking().Where(x => x.TagId == tagId).Select(x => x.TodoId);
            query = query.Where(t => tagged.Contains(t.Id));
        }

        if (filter.Keyword is { Length: > 0 } keyword)
        {
            var needle = keyword.ToLowerInvariant();
            query = query.Where(t => t.Title.ToLower().Contains(needle)
                                     || (t.Notes != null && t.Notes.ToLower().Contains(needle))
                                     || (t.CompletionNote != null && t.CompletionNote.ToLower().Contains(needle)));
        }

        var limit = Math.Clamp(filter.Limit, 1, PlannerConstants.MaxListLimit);
        var grouped = query.OrderBy(t =>
            t.Status == "done" || t.Status == "verified" || t.Status == "closed" || t.Status == "canceled");

        var ordered = filter.Sort switch
        {
            "priority" => grouped
                .ThenBy(t => t.Priority)
                .ThenBy(t => t.DueAt == null)
                .ThenBy(t => t.DueAt)
                .ThenByDescending(t => t.UpdatedAt),
            "updated" => grouped.ThenByDescending(t => t.UpdatedAt),
            "created" => grouped.ThenByDescending(t => t.CreatedAt),
            _ => grouped
                .ThenBy(t => t.DueAt == null)
                .ThenBy(t => t.DueAt)
                .ThenBy(t => t.Priority)
                .ThenByDescending(t => t.UpdatedAt),
        };
        return ordered.Take(limit).ToList();
    }

    public IReadOnlyList<TodoRow> QueryDeletedTodos(int limit)
    {
        using var db = Open();
        return db.Todos.AsNoTracking()
            .Where(t => t.DeletedAt != null)
            .OrderByDescending(t => t.DeletedAt)
            .Take(Math.Clamp(limit, 1, PlannerConstants.MaxListLimit))
            .ToList();
    }

    /// <summary>日历区间：按 dueAt 落在 [from, to) 取 todo（上限 500，防止一次拉爆 UI）。</summary>
    public IReadOnlyList<TodoRow> QueryTodosDueRange(DateTime fromUtc, DateTime toUtc)
    {
        using var db = Open();
        return db.Todos.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.DueAt != null && t.DueAt >= fromUtc && t.DueAt < toUtc)
            .OrderBy(t => t.DueAt)
            .Take(500)
            .ToList();
    }

    public IReadOnlyList<TodoRow> QueryAllTodos(bool includeCompleted)
    {        using var db = Open();
        var query = db.Todos.AsNoTracking().Where(t => t.DeletedAt == null);
        if (!includeCompleted)
        {
            query = query.Where(t => t.Status != "done");
        }

        return query.OrderBy(t => t.DueAt).ThenByDescending(t => t.Priority).ToList();
    }

    public IReadOnlyList<EventRow> QueryEvents(DateTime fromUtc, DateTime toUtc)
    {
        using var db = Open();
        return db.Events.AsNoTracking()
            .Where(e => e.DeletedAt == null && e.StartAt < toUtc && e.EndAt > fromUtc)
            .OrderBy(e => e.StartAt)
            .ToList();
    }

    public IReadOnlyList<EventRow> QueryAllEvents()
    {
        using var db = Open();
        return db.Events.AsNoTracking()
            .Where(e => e.DeletedAt == null)
            .OrderBy(e => e.StartAt)
            .ToList();
    }

    public IReadOnlyList<ListRow> GetLists()
    {
        using var db = Open();
        return db.Lists.AsNoTracking().OrderBy(l => l.Sort).ThenBy(l => l.Name).ToList();
    }

    public ListRow? GetList(string id)
    {
        using var db = Open();
        return db.Lists.AsNoTracking().FirstOrDefault(l => l.Id == id);
    }

    public string? FindTagId(string name)
    {
        var normalized = NormalizeTag(name);
        if (normalized.Length == 0)
        {
            return null;
        }

        using var db = Open();
        return db.Tags.AsNoTracking().Where(t => t.Name == normalized).Select(t => t.Id).FirstOrDefault();
    }

    public IReadOnlyList<string> GetTodoTags(string todoId)
    {
        using var db = Open();
        return (from link in db.TodoTags.AsNoTracking()
                join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                where link.TodoId == todoId
                select tag.Name)
            .OrderBy(name => name)
            .ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTags(IReadOnlyCollection<string> todoIds)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (todoIds.Count == 0)
        {
            return result;
        }

        using var db = Open();
        var rows = (from link in db.TodoTags.AsNoTracking()
                    join tag in db.Tags.AsNoTracking() on link.TagId equals tag.Id
                    where todoIds.Contains(link.TodoId)
                    select new { link.TodoId, tag.Name })
            .ToList();

        foreach (var group in rows.GroupBy(x => x.TodoId, StringComparer.Ordinal))
        {
            result[group.Key] = group.Select(x => x.Name).OrderBy(x => x).ToList();
        }

        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetLinks(string ownerType, IReadOnlyCollection<string> ownerIds)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (ownerIds.Count == 0)
        {
            return result;
        }

        using var db = Open();
        var rows = db.Links.AsNoTracking()
            .Where(l => l.OwnerType == ownerType && ownerIds.Contains(l.OwnerId))
            .OrderBy(l => l.CreatedAt)
            .ToList();

        foreach (var group in rows.GroupBy(x => x.OwnerId, StringComparer.Ordinal))
        {
            result[group.Key] = group.Select(x => x.Uri).ToList();
        }

        return result;
    }

    public IReadOnlyList<TodoTitleRow> QueryOpenTitles(int limit)
    {
        using var db = Open();
        return db.Todos.AsNoTracking()
            .Where(t => t.DeletedAt == null && (t.Status == "open" || t.Status == "doing"))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(t => new TodoTitleRow(t.Id, t.Title, t.Notes))
            .ToList();
    }

    public StatsRaw GetStats(DateTime startOfTodayUtc, DateTime startOfTomorrowUtc, DateTime sevenDaysAgoUtc)
    {
        using var db = Open();
        var open = db.Todos.AsNoTracking().Where(t => t.DeletedAt == null
            && (t.Status == "open" || t.Status == "doing"));
        var openTotal = open.Count();
        var dueToday = open.Count(t => t.DueAt != null && t.DueAt >= startOfTodayUtc && t.DueAt < startOfTomorrowUtc);
        var overdue = open.Count(t => t.DueAt != null && t.DueAt < startOfTodayUtc);
        var doneToday = db.Todos.AsNoTracking()
            .Count(t => (t.Status == "done" || t.Status == "verified" || t.Status == "closed")
                        && t.CompletedAt != null
                        && t.CompletedAt >= startOfTodayUtc && t.CompletedAt < startOfTomorrowUtc);
        var doneLast7 = db.Todos.AsNoTracking()
            .Count(t => (t.Status == "done" || t.Status == "verified" || t.Status == "closed")
                        && t.CompletedAt != null && t.CompletedAt >= sevenDaysAgoUtc);
        var createdLast7 = db.Todos.AsNoTracking().Count(t => t.CreatedAt >= sevenDaysAgoUtc);

        var listRows = db.Lists.AsNoTracking().ToList();
        var lists = listRows.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var byList = open
            .GroupBy(t => t.ListId)
            .Select(g => new { ListId = g.Key, Count = g.Count() })
            .ToList()
            .Select(g => (
                g.ListId,
                g.ListId is null ? null : lists.GetValueOrDefault(g.ListId),
                g.Count))
            .OrderByDescending(x => x.Item3)
            .ToList();

        return new StatsRaw(openTotal, dueToday, overdue, doneToday, doneLast7, createdLast7, byList);
    }

    // ── 全文检索（FTS5 + 2-gram）──────────────────────────────────────────

    /// <summary>
    /// 返回命中 id 与 bm25 分数（越小越相关）。调用方负责 AND/OR 放宽策略。
    /// </summary>
    public IReadOnlyList<SearchIdRow> SearchTodos(string matchExpression, int limit)
    {
        using var db = Open();
        var connection = db.Database.GetDbConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.todo_id, bm25(todo_fts) AS score
            FROM todo_fts f
            JOIN todo t ON t.id = f.todo_id
            WHERE todo_fts MATCH $q AND t.deleted_at IS NULL
            ORDER BY score
            LIMIT $n
            """;
        command.Parameters.Add(CreateParameter(command, "$q", matchExpression));
        command.Parameters.Add(CreateParameter(command, "$n", Math.Clamp(limit, 1, PlannerConstants.MaxListLimit)));

        var hits = new List<SearchIdRow>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new SearchIdRow(reader.GetString(0), reader.GetDouble(1)));
            }
        }
        catch (SqliteException)
        {
            // FTS 语法错误（例如空查询被拼成 ""）不应把整个搜索打成 500；
            // 交给上层按「无命中」处理。
            return [];
        }

        return hits;
    }

    /// <summary>单字查询的退化路径：FTS 的 2-gram 切不出单字，用 LIKE。</summary>
    public IReadOnlyList<SearchIdRow> SearchTodosLike(string keyword, int limit)
    {
        using var db = Open();
        var pattern = $"%{keyword}%";
        return db.Todos.AsNoTracking()
            .Where(t => t.DeletedAt == null && (EF.Functions.Like(t.Title, pattern)
                                                || (t.Notes != null && EF.Functions.Like(t.Notes, pattern))))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(Math.Clamp(limit, 1, PlannerConstants.MaxListLimit))
            .Select(t => new SearchIdRow(t.Id, 0))
            .ToList();
    }

    public TodoRow? GetTodoIncludingDeleted(string id)
    {
        using var db = Open();
        return db.Todos.AsNoTracking().FirstOrDefault(t => t.Id == id);
    }

    // ── 写 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 批量写待办：新建 / 更新 / 软删 / 恢复 / 物理清理都在一个事务里。
    /// expectedRevision 由调用方给出，事务内再校验一次，避免「先读后写」窗口里的覆盖。
    /// </summary>
    public void WriteTodos(
        IReadOnlyList<TodoWrite> writes,
        string? idempotencyKey = null,
        string? idempotencyJson = null)
    {
        if (writes.Count == 0 && idempotencyKey is null)
        {
            return;
        }

        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var connection = db.Database.GetDbConnection();
            connection.Open();

            ApplyTodoWrites(db, connection, writes);

            if (idempotencyKey is not null && idempotencyJson is not null)
            {
                var cutoff = DateTime.UtcNow - PlannerConstants.IdempotencyWindow;
                db.Idempotency.RemoveRange(db.Idempotency.Where(r => r.CreatedAt < cutoff));
                db.Idempotency.Add(new IdempotencyRow
                {
                    ClientRequestId = idempotencyKey,
                    ResponseJson = idempotencyJson,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    public void WriteEvents(IReadOnlyList<EventWrite> writes, string? idempotencyKey = null, string? idempotencyJson = null)
    {
        if (writes.Count == 0 && idempotencyKey is null)
        {
            return;
        }

        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var connection = db.Database.GetDbConnection();
            connection.Open();

            foreach (var write in writes)
            {
                ApplyEventWrite(db, write);
            }

            if (idempotencyKey is not null && idempotencyJson is not null)
            {
                db.Idempotency.Add(new IdempotencyRow
                {
                    ClientRequestId = idempotencyKey,
                    ResponseJson = idempotencyJson,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    public void InsertList(ListRow row)
    {
        using var db = Open();
        db.Lists.Add(row);
        db.SaveChanges();
    }

    public void InsertDraft(DraftRow draft, IReadOnlyList<DraftItemRow> items)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        db.Drafts.Add(draft);
        foreach (var item in items)
        {
            db.DraftItems.Add(item);
        }

        db.SaveChanges();
        transaction.Commit();
    }

    /// <summary>
    /// 采纳草稿：逐条决策 + 写正式表 + 标记草稿状态，**一个事务**。
    /// 写入的 todo 带 source=ai/meeting 与 sourceSessionId/sourceExcerpt（PL-11）。
    /// </summary>
    public void AcceptDraft(
        DraftRow draft,
        IReadOnlyList<DraftItemRow> items,
        IReadOnlyList<TodoWrite> todoWrites,
        string? idempotencyKey = null,
        string? idempotencyJson = null)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var connection = db.Database.GetDbConnection();
            connection.Open();

            foreach (var item in items)
            {
                db.DraftItems.Update(item);
            }

            db.Drafts.Update(draft);
            ApplyTodoWrites(db, connection, todoWrites);

            if (idempotencyKey is not null && idempotencyJson is not null)
            {
                db.Idempotency.Add(new IdempotencyRow
                {
                    ClientRequestId = idempotencyKey,
                    ResponseJson = idempotencyJson,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    public void RejectDraft(DraftRow draft, IReadOnlyList<DraftItemRow> items)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        foreach (var item in items)
        {
            db.DraftItems.Update(item);
        }

        db.Drafts.Update(draft);
        db.SaveChanges();
        transaction.Commit();
    }

    public DraftRow? GetDraft(string id)
    {
        using var db = Open();
        return db.Drafts.AsNoTracking().FirstOrDefault(d => d.Id == id);
    }

    public IReadOnlyList<DraftItemRow> GetDraftItems(string draftId)
    {
        using var db = Open();
        return db.DraftItems.AsNoTracking()
            .Where(i => i.DraftId == draftId)
            .OrderBy(i => i.Sort)
            .ToList();
    }

    public IReadOnlyList<DraftRow> QueryDrafts(bool pendingOnly, int limit)
    {
        using var db = Open();
        var query = db.Drafts.AsNoTracking();
        if (pendingOnly)
        {
            query = query.Where(d => d.Status == "pending");
        }

        return query.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(limit, 1, 50)).ToList();
    }

    /// <summary>幂等窗口内返回首次结果 JSON；过窗口视为未命中。</summary>
    public string? TryGetIdempotency(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        using var db = Open();
        var row = db.Idempotency.AsNoTracking().FirstOrDefault(r => r.ClientRequestId == key);
        if (row is null)
        {
            return null;
        }

        return DateTime.UtcNow - NormalizeUtc(row.CreatedAt) <= PlannerConstants.IdempotencyWindow
            ? row.ResponseJson
            : null;
    }

    /// <summary>
    /// 记录幂等响应。与业务写不在同一事务（幂等是「防重复」而非「一致性」要求），
    /// 但写成功后立刻落，窗口内重复调用直接回放。
    /// </summary>
    public void SaveIdempotency(string key, string responseJson)
    {
        using var db = Open();
        var cutoff = DateTime.UtcNow - PlannerConstants.IdempotencyWindow;
        db.Idempotency.RemoveRange(db.Idempotency.Where(r => r.CreatedAt < cutoff));
        var existing = db.Idempotency.FirstOrDefault(r => r.ClientRequestId == key);
        if (existing is not null)
        {
            existing.ResponseJson = responseJson;
            existing.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            db.Idempotency.Add(new IdempotencyRow
            {
                ClientRequestId = key,
                ResponseJson = responseJson,
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.SaveChanges();
    }

    public void RecordMcpAudit(string tool, string paramsSummary, int itemCount, string resultCode, long durationMs, string? requestId)
    {        using var db = Open();
        db.McpAudit.Add(new McpAuditRow
        {
            Tool = tool,
            ParamsSummary = paramsSummary.Length > 512 ? paramsSummary[..512] : paramsSummary,
            ItemCount = itemCount,
            ResultCode = resultCode,
            DurationMs = durationMs,
            RequestId = requestId,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>回收站到期清理：仅当 restorable_until 已过，才允许物理删除（7 天后）。</summary>
    public int PurgeExpired(DateTime nowUtc)
    {
        using var db = Open();
        var todoRows = db.Todos.AsNoTracking()
            .Where(t => t.DeletedAt != null && t.RestorableUntil != null && t.RestorableUntil < nowUtc)
            .ToList();
        var eventRows = db.Events.AsNoTracking()
            .Where(e => e.DeletedAt != null && e.RestorableUntil != null && e.RestorableUntil < nowUtc)
            .ToList();

        var writes = todoRows.Select(row => new TodoWrite
        {
            Row = row,
            Op = "purge",
            Actor = "system",
            Reason = "回收站到期",
        }).ToList();

        if (writes.Count > 0)
        {
            WriteTodos(writes);
        }

        var eventWrites = eventRows.Select(row => new EventWrite
        {
            Row = row,
            Op = "purge",
            Actor = "system",
            Reason = "回收站到期",
        }).ToList();

        if (eventWrites.Count > 0)
        {
            WriteEvents(eventWrites);
        }

        return writes.Count + eventWrites.Count;
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────

    private static void ApplyTodoWrites(
        PlannerDbContext db,
        DbConnection connection,
        IReadOnlyList<TodoWrite> writes)
    {
        foreach (var write in writes)
        {
            var row = write.Row;

            if (write.Op == "purge")
            {
                var existing = db.Todos.FirstOrDefault(t => t.Id == row.Id);
                if (existing is null)
                {
                    continue;
                }

                RemoveFtsRow(connection, existing.FtsRowId);
                db.TodoTags.RemoveRange(db.TodoTags.Where(x => x.TodoId == row.Id));
                db.Links.RemoveRange(db.Links.Where(l => l.OwnerType == "todo" && l.OwnerId == row.Id));
                db.Todos.Remove(existing);
                AddOutbox(db, "todo", row.Id, "purge", existing.Revision, row);
                AddOpLog(db, "todo", row.Id, "purge", Json(existing), null, write.Actor, write.RequestId, write.Reason);
                continue;
            }

            var current = db.Todos.FirstOrDefault(t => t.Id == row.Id);
            string? beforeJson = null;
            if (current is null)
            {
                if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
                {
                    throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
                }

                row.FtsRowId = null;
                db.Todos.Add(row);
                AddOutbox(db, "todo", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "todo", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                if (write.ExpectedRevision is { } expected && current.Revision != expected)
                {
                    throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
                }

                beforeJson = Json(current);

                // 保留主键与索引 rowid；其余字段整体覆盖（Core 负责填好最终值）。
                row.FtsRowId = current.FtsRowId;
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "todo", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "todo", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            ReplaceTags(db, row.Id, write.Tags);
            ReplaceLinks(db, "todo", row.Id, write.Links);
        }

        // 行落库后再刷新 FTS：新行需要拿到 rowid，删行时 rowid 已在写前读入内存。
        db.SaveChanges();

        foreach (var write in writes.Where(w => w.Op != "purge"))
        {
            // 用被跟踪的那个实例改 FtsRowId；另建一个同主键的实例再 Entry() 会触发「已跟踪」错误。
            var tracked = db.Todos.Local.First(t => t.Id == write.Row.Id);
            tracked.FtsRowId = RefreshFts(connection, tracked.FtsRowId, tracked.Id, tracked.Title, tracked.Notes);
        }

        db.SaveChanges();
    }

    private static void ApplyEventWrite(PlannerDbContext db, EventWrite write)
    {
        var row = write.Row;
        if (write.Op == "purge")
        {
            var existing = db.Events.FirstOrDefault(e => e.Id == row.Id);
            if (existing is null)
            {
                return;
            }

            db.Links.RemoveRange(db.Links.Where(l => l.OwnerType == "event" && l.OwnerId == row.Id));
            db.Events.Remove(existing);
            AddOutbox(db, "event", row.Id, "purge", existing.Revision, row);
            AddOpLog(db, "event", row.Id, "purge", Json(existing), null, write.Actor, write.RequestId, write.Reason);
            return;
        }

        var current = db.Events.FirstOrDefault(e => e.Id == row.Id);
        string? beforeJson = null;
        if (current is null)
        {
            if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
            {
                throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
            }

            db.Events.Add(row);
            AddOutbox(db, "event", row.Id, write.Op, row.Revision, row);
            AddOpLog(db, "event", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
        }
        else
        {
            if (write.ExpectedRevision is { } expected && current.Revision != expected)
            {
                throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
            }

            beforeJson = Json(current);
            db.Entry(current).CurrentValues.SetValues(row);
            AddOutbox(db, "event", row.Id, write.Op, row.Revision, row);
            AddOpLog(db, "event", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
        }

        ReplaceLinks(db, "event", row.Id, write.Links);
    }

    private static void ReplaceTags(PlannerDbContext db, string todoId, IReadOnlyList<string> tags)
    {
        var existing = db.TodoTags.Where(x => x.TodoId == todoId).ToList();
        db.TodoTags.RemoveRange(existing);

        foreach (var raw in tags.Select(NormalizeTag).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var tagId = "tg_" + raw;
            if (!db.Tags.Any(t => t.Id == tagId))
            {
                if (!db.Tags.Local.Any(t => t.Id == tagId))
                {
                    db.Tags.Add(new TagRow
                    {
                        Id = tagId,
                        Name = raw,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
            }

            db.TodoTags.Add(new TodoTagRow { TodoId = todoId, TagId = tagId });
        }
    }

    private static void ReplaceLinks(PlannerDbContext db, string ownerType, string ownerId, IReadOnlyList<string> links)
    {
        var existing = db.Links.Where(l => l.OwnerType == ownerType && l.OwnerId == ownerId).ToList();
        db.Links.RemoveRange(existing);

        foreach (var uri in links.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal))
        {
            db.Links.Add(new LinkRow
            {
                Id = "lk_" + Guid.NewGuid().ToString("N"),
                OwnerType = ownerType,
                OwnerId = ownerId,
                Uri = uri,
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    private static long? RefreshFts(DbConnection connection, long? oldRowId, string todoId, string title, string? notes)
    {
        RemoveFtsRow(connection, oldRowId);

        var terms = TodoFts.BuildTerms(title, notes);
        if (terms.Length == 0)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO todo_fts(todo_id, terms) VALUES ($id, $terms)";
        command.Parameters.Add(CreateParameter(command, "$id", todoId));
        command.Parameters.Add(CreateParameter(command, "$terms", terms));
        command.ExecuteNonQuery();

        using var lastId = connection.CreateCommand();
        lastId.CommandText = "SELECT last_insert_rowid()";
        return (long)(lastId.ExecuteScalar() ?? 0L);
    }

    private static void RemoveFtsRow(DbConnection connection, long? ftsRowId)
    {
        if (ftsRowId is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM todo_fts WHERE rowid = $rowid";
        command.Parameters.Add(CreateParameter(command, "$rowid", ftsRowId.Value));
        command.ExecuteNonQuery();
    }

    private static void AddOutbox(PlannerDbContext db, string entityType, string entityId, string op, int revision, object payload)
    {
        db.Outbox.Add(new OutboxRow
        {
            EntityType = entityType,
            EntityId = entityId,
            Op = op,
            Revision = revision,
            PayloadJson = Json(payload),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static void AddOpLog(
        PlannerDbContext db,
        string entityType,
        string entityId,
        string op,
        string? beforeJson,
        string? afterJson,
        string actor,
        string? requestId,
        string? reason)
    {
        db.OpLog.Add(new OpLogRow
        {
            EntityType = entityType,
            EntityId = entityId,
            Op = op,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Actor = actor,
            RequestId = requestId,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, PlannerJson.Wire);

    private static string NormalizeTag(string? tag) =>
        (tag ?? string.Empty).Trim().ToLowerInvariant();

    private static DateTime NormalizeUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DbParameter CreateParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    private static bool IsLocked(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
            {
                return true;
            }
        }

        return false;
    }
}
