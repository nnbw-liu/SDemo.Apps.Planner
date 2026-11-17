using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// 任务阶段性进展的读写。进展是任务的从属记录：跟着任务的删除 / 回收站走，
/// 每次写都落 op_log（entity_type = progress），并做乐观并发校验。
/// </summary>
public sealed partial class PlannerStore
{
    public IReadOnlyList<TodoProgressRow> QueryTodoProgress(string todoId, bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.TodoProgress.AsNoTracking().Where(p => p.TodoId == todoId);
        if (!includeDeleted)
        {
            query = query.Where(p => p.DeletedAt == null);
        }

        return query.OrderBy(p => p.OccurredAt).ThenBy(p => p.CreatedAt).ToList();
    }

    /// <summary>一次取多条任务的进展，给列表页算「最近进度」用。</summary>
    public IReadOnlyList<TodoProgressRow> QueryProgressForTodos(IReadOnlyList<string> todoIds)
    {
        if (todoIds.Count == 0)
        {
            return [];
        }

        using var db = Open();
        return db.TodoProgress.AsNoTracking()
            .Where(p => todoIds.Contains(p.TodoId) && p.DeletedAt == null)
            .OrderBy(p => p.OccurredAt)
            .ThenBy(p => p.CreatedAt)
            .ToList();
    }

    public TodoProgressRow? GetTodoProgress(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.TodoProgress.AsNoTracking().Where(p => p.Id == id);
        if (!includeDeleted)
        {
            query = query.Where(p => p.DeletedAt == null);
        }

        return query.FirstOrDefault();
    }

    public int CountTodoProgress(string todoId)
    {
        using var db = Open();
        return db.TodoProgress.Count(p => p.TodoId == todoId && p.DeletedAt == null);
    }

    public void InsertTodoProgress(TodoProgressRow row, string actor, string? requestId)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            db.TodoProgress.Add(row);
            AddOpLog(db, "progress", row.Id, "create", null, Json(row), actor, requestId, null);
            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>整行覆盖：调用方已完成字段合并；这里只做 revision 守卫与审计。</summary>
    public void UpdateTodoProgress(TodoProgressRow row, int expectedRevision, string actor, string? requestId)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var current = db.TodoProgress.FirstOrDefault(p => p.Id == row.Id && p.DeletedAt == null)
                ?? throw new PlannerStoreConflictException(row.Id, -1, expectedRevision);

            if (current.Revision != expectedRevision)
            {
                throw new PlannerStoreConflictException(row.Id, current.Revision, expectedRevision);
            }

            var beforeJson = Json(current);
            current.Title = row.Title;
            current.Notes = row.Notes;
            current.Percent = row.Percent;
            current.ArtifactsJson = row.ArtifactsJson;
            current.OccurredAt = row.OccurredAt;
            current.Revision = expectedRevision + 1;
            current.UpdatedAt = row.UpdatedAt;
            AddOpLog(db, "progress", row.Id, "update", beforeJson, Json(current), actor, requestId, null);
            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>软删除：详情里立即不可见，行与附件留到回收站到期再物理清理。</summary>
    public bool DeleteTodoProgress(
        string id,
        int? expectedRevision,
        DateTime nowUtc,
        string actor,
        string? requestId,
        string? reason)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var current = db.TodoProgress.FirstOrDefault(p => p.Id == id && p.DeletedAt == null);
            if (current is null)
            {
                return false;
            }

            if (expectedRevision is not null && current.Revision != expectedRevision)
            {
                throw new PlannerStoreConflictException(id, current.Revision, expectedRevision.Value);
            }

            var beforeJson = Json(current);
            current.DeletedAt = nowUtc;
            current.UpdatedAt = nowUtc;
            current.Revision += 1;
            AddOpLog(db, "progress", id, "delete", beforeJson, Json(current), actor, requestId, reason);
            db.SaveChanges();
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>回收站到期：物理删除已软删的进展行。</summary>
    public int PurgeExpiredProgress(DateTime nowUtc)
    {
        var cutoff = nowUtc - Contracts.PlannerConstants.RecycleBinRetention;
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var rows = db.TodoProgress.Where(p => p.DeletedAt != null && p.DeletedAt < cutoff).ToList();
            foreach (var row in rows)
            {
                db.TodoProgress.Remove(row);
                AddOpLog(db, "progress", row.Id, "purge", Json(row), null, "system", null, "回收站到期");
            }

            if (rows.Count == 0)
            {
                return 0;
            }

            db.SaveChanges();
            transaction.Commit();
            return rows.Count;
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>任务被物理清理时，连带删掉它的全部进展（含已软删的）。</summary>
    public IReadOnlyList<TodoProgressRow> PurgeProgressForTodo(string todoId)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var rows = db.TodoProgress.Where(p => p.TodoId == todoId).ToList();
            if (rows.Count == 0)
            {
                return [];
            }

            foreach (var row in rows)
            {
                db.TodoProgress.Remove(row);
                AddOpLog(db, "progress", row.Id, "purge", Json(row), null, "system", null, "所属任务已清理");
            }

            db.SaveChanges();
            transaction.Commit();
            return rows;
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }
}
