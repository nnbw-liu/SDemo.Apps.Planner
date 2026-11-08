using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// v2 工作区（项目 / 迭代 / 日常）的持久化。与 todo 的写一样：
/// 单事务、revision+1、outbox + op_log 同事务。
/// </summary>
public sealed partial class PlannerStore
{
    // ── 项目 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ProjectRow> QueryProjects(bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.Projects.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(p => p.DeletedAt == null);
        }

        return query.OrderBy(p => p.Sort).ThenBy(p => p.CreatedAt).ToList();
    }

    public ProjectRow? GetProject(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.Projects.AsNoTracking().FirstOrDefault(p => p.Id == id);
        return row is null || (!includeDeleted && row.DeletedAt is not null) ? null : row;
    }

    // ── 项目分组 ──────────────────────────────────────────────────────────

    public IReadOnlyList<ProjectGroupRow> QueryProjectGroups(bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.ProjectGroups.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(g => g.DeletedAt == null);
        }

        return query.OrderBy(g => g.Sort).ThenBy(g => g.CreatedAt).ToList();
    }

    public ProjectGroupRow? GetProjectGroup(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.ProjectGroups.AsNoTracking().FirstOrDefault(g => g.Id == id);
        return row is null || (!includeDeleted && row.DeletedAt is not null) ? null : row;
    }

    /// <summary>全量成员关系；个人库规模有限，由 Core 在内存里分组。</summary>
    public IReadOnlyList<ProjectGroupMemberRow> QueryProjectGroupMembers()
    {
        using var db = Open();
        return db.ProjectGroupMembers.AsNoTracking().ToList();
    }

    public IReadOnlyList<string> QueryProjectGroupIds(string projectId)
    {
        using var db = Open();
        return db.ProjectGroupMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .Select(m => m.ProjectGroupId)
            .ToList();
    }

    public void WriteProjectGroup(ProjectGroupWrite write)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = write.Row;
            var current = db.ProjectGroups.FirstOrDefault(g => g.Id == row.Id);
            string? beforeJson = null;

            if (current is null)
            {
                if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
                {
                    throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
                }

                db.ProjectGroups.Add(row);
                AddOutbox(db, "project_group", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "project_group", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                if (write.ExpectedRevision is { } expected && current.Revision != expected)
                {
                    throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
                }

                beforeJson = Json(current);
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "project_group", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "project_group", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>软删分组：成员关系立即移除，项目本身保留。</summary>
    public void DeleteProjectGroup(string id, DateTime nowUtc)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var group = db.ProjectGroups.FirstOrDefault(g => g.Id == id);
        if (group is null)
        {
            return;
        }

        foreach (var member in db.ProjectGroupMembers.Where(m => m.ProjectGroupId == id).ToList())
        {
            db.ProjectGroupMembers.Remove(member);
        }

        group.DeletedAt = nowUtc;
        group.UpdatedAt = nowUtc;
        group.Revision++;
        AddOpLog(db, "project_group", id, "delete", null, Json(group), "manual", null, "分组删除，项目保留");
        db.SaveChanges();
        transaction.Commit();
    }

    /// <summary>整体替换项目所属分组；调用方已在事务外做 Id 校验。</summary>
    private static void ReplaceProjectGroups(PlannerDbContext db, string projectId, IReadOnlyList<string> groupIds)
    {
        foreach (var member in db.ProjectGroupMembers.Where(m => m.ProjectId == projectId).ToList())
        {
            db.ProjectGroupMembers.Remove(member);
        }

        var validIds = db.ProjectGroups
            .Where(g => g.DeletedAt == null && groupIds.Contains(g.Id))
            .Select(g => g.Id)
            .Distinct()
            .ToList();

        foreach (var groupId in validIds)
        {
            db.ProjectGroupMembers.Add(new ProjectGroupMemberRow { ProjectGroupId = groupId, ProjectId = projectId });
        }
    }

    public void WriteProject(ProjectWrite write)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = write.Row;
            var current = db.Projects.FirstOrDefault(p => p.Id == row.Id);
            string? beforeJson = null;

            if (current is null)
            {
                if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
                {
                    throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
                }

                db.Projects.Add(row);
                AddOutbox(db, "project", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "project", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                if (write.ExpectedRevision is { } expected && current.Revision != expected)
                {
                    throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
                }

                beforeJson = Json(current);
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "project", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "project", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            if (write.GroupIds is not null)
            {
                ReplaceProjectGroups(db, row.Id, write.GroupIds);
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>软删项目：迭代一并软删，任务脱离项目但保留（不删用户数据）。</summary>
    public void DeleteProject(string id, DateTime nowUtc)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        var project = db.Projects.FirstOrDefault(p => p.Id == id);
        if (project is null)
        {
            return;
        }

        foreach (var iteration in db.Iterations.Where(i => i.ProjectId == id && i.DeletedAt == null).ToList())
        {
            iteration.DeletedAt = nowUtc;
            iteration.UpdatedAt = nowUtc;
            iteration.Revision++;
        }

        foreach (var todo in db.Todos.Where(t => t.ProjectId == id && t.DeletedAt == null).ToList())
        {
            todo.ProjectId = null;
            todo.IterationId = null;
            todo.UpdatedAt = nowUtc;
            todo.Revision++;
        }

        foreach (var member in db.ProjectGroupMembers.Where(m => m.ProjectId == id).ToList())
        {
            db.ProjectGroupMembers.Remove(member);
        }

        project.DeletedAt = nowUtc;
        project.UpdatedAt = nowUtc;
        project.Revision++;
        AddOpLog(db, "project", id, "delete", null, Json(project), "manual", null, "项目删除，任务保留");
        db.SaveChanges();
        transaction.Commit();
    }

    // ── 迭代 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<IterationRow> QueryIterations(string? projectId = null, bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.Iterations.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(i => i.DeletedAt == null);
        }

        if (projectId is { Length: > 0 })
        {
            query = query.Where(i => i.ProjectId == projectId);
        }

        return query.OrderBy(i => i.Sort).ThenBy(i => i.StartAt).ThenBy(i => i.CreatedAt).ToList();
    }

    public IterationRow? GetIteration(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.Iterations.AsNoTracking().FirstOrDefault(i => i.Id == id);
        return row is null || (!includeDeleted && row.DeletedAt is not null) ? null : row;
    }

    public void WriteIteration(IterationWrite write)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = write.Row;
            var current = db.Iterations.FirstOrDefault(i => i.Id == row.Id);
            string? beforeJson = null;

            if (current is null)
            {
                if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
                {
                    throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
                }

                db.Iterations.Add(row);
                AddOutbox(db, "iteration", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "iteration", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                if (write.ExpectedRevision is { } expected && current.Revision != expected)
                {
                    throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
                }

                beforeJson = Json(current);
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "iteration", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "iteration", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>软删迭代：其子迭代一并软删，任务只清 iteration_id，仍留在项目里。</summary>
    public void DeleteIteration(string id, DateTime nowUtc)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        var iteration = db.Iterations.FirstOrDefault(i => i.Id == id);
        if (iteration is null)
        {
            return;
        }

        var childIds = db.Iterations
            .Where(i => i.ParentId == id && i.DeletedAt == null)
            .Select(i => i.Id)
            .ToList();

        foreach (var child in db.Iterations.Where(i => childIds.Contains(i.Id)).ToList())
        {
            child.DeletedAt = nowUtc;
            child.UpdatedAt = nowUtc;
            child.Revision++;
        }

        foreach (var todo in db.Todos.Where(t => t.IterationId == id || childIds.Contains(t.IterationId!)).ToList())
        {
            todo.IterationId = null;
            todo.UpdatedAt = nowUtc;
            todo.Revision++;
        }

        iteration.DeletedAt = nowUtc;
        iteration.UpdatedAt = nowUtc;
        iteration.Revision++;
        AddOpLog(db, "iteration", id, "delete", null, Json(iteration), "manual", null, "迭代删除，任务保留");
        db.SaveChanges();
        transaction.Commit();
    }

    // ── 日常 / 打卡 ───────────────────────────────────────────────────────

    public IReadOnlyList<RoutineRow> QueryRoutines(bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.Routines.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(r => r.DeletedAt == null);
        }

        return query.OrderBy(r => r.Sort).ThenBy(r => r.TimeOfDay).ThenBy(r => r.CreatedAt).ToList();
    }

    public RoutineRow? GetRoutine(string id, bool includeDeleted = false)
    {
        using var db = Open();
        var row = db.Routines.AsNoTracking().FirstOrDefault(r => r.Id == id);
        return row is null || (!includeDeleted && row.DeletedAt is not null) ? null : row;
    }

    public void WriteRoutine(RoutineWrite write)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = write.Row;
            var current = db.Routines.FirstOrDefault(r => r.Id == row.Id);
            string? beforeJson = null;

            if (current is null)
            {
                if (write.ExpectedRevision is { } expectedNew && expectedNew != 0)
                {
                    throw new PlannerStoreConflictException(row.Id, 0, expectedNew);
                }

                db.Routines.Add(row);
                AddOutbox(db, "routine", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "routine", row.Id, write.Op, null, Json(row), write.Actor, write.RequestId, write.Reason);
            }
            else
            {
                if (write.ExpectedRevision is { } expected && current.Revision != expected)
                {
                    throw new PlannerStoreConflictException(row.Id, current.Revision, expected);
                }

                beforeJson = Json(current);
                db.Entry(current).CurrentValues.SetValues(row);
                AddOutbox(db, "routine", row.Id, write.Op, row.Revision, row);
                AddOpLog(db, "routine", row.Id, write.Op, beforeJson, Json(row), write.Actor, write.RequestId, write.Reason);
            }

            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    public IReadOnlyDictionary<string, RoutineCheckRow> GetRoutineChecks(string localDate)
    {
        using var db = Open();
        return db.RoutineChecks.AsNoTracking()
            .Where(c => c.LocalDate == localDate)
            .ToDictionary(c => c.RoutineId, c => c, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, int> GetRoutineCheckCounts(string fromDate, string toDate)
    {
        using var db = Open();
        return db.RoutineChecks.AsNoTracking()
            .Where(c => string.Compare(c.LocalDate, fromDate) >= 0 && string.Compare(c.LocalDate, toDate) <= 0)
            .GroupBy(c => c.LocalDate)
            .Select(g => new { Date = g.Key, Count = g.Sum(x => x.Count) })
            .ToDictionary(x => x.Date, x => x.Count, StringComparer.Ordinal);
    }

    public IReadOnlyList<RoutineCheckRow> QueryRoutineChecks(string fromDate, string toDate)
    {
        using var db = Open();
        return db.RoutineChecks.AsNoTracking()
            .Where(c => string.Compare(c.LocalDate, fromDate) >= 0 && string.Compare(c.LocalDate, toDate) <= 0)
            .ToList();
    }

    /// <summary>按 delta 调整某天打卡次数，返回调整后的次数（不会小于 0）。</summary>
    public int AdjustRoutineCheck(string routineId, string localDate, int delta, DateTime nowUtc)
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction();
        var row = db.RoutineChecks.FirstOrDefault(c => c.RoutineId == routineId && c.LocalDate == localDate);
        if (row is null)
        {
            row = new RoutineCheckRow { RoutineId = routineId, LocalDate = localDate, Count = 0 };
            db.RoutineChecks.Add(row);
        }

        row.Count = Math.Max(0, row.Count + delta);
        if (delta > 0)
        {
            row.LastCompletedAt = nowUtc;
        }

        db.SaveChanges();
        transaction.Commit();
        return row.Count;
    }

    /// <summary>提醒去重时间：内部状态，不参与乐观并发 revision；仅一次提醒触发后同时停用。</summary>
    public void MarkRoutineFired(string routineId, DateTime firedAtUtc, bool disable = false)
    {
        using var db = Open();
        var row = db.Routines.FirstOrDefault(r => r.Id == routineId);
        if (row is null)
        {
            return;
        }

        row.LastFiredAt = firedAtUtc;
        if (disable)
        {
            row.Enabled = false;
            row.UpdatedAt = firedAtUtc;
            row.Revision++;
        }

        db.SaveChanges();
    }

    /// <summary>已有任务全部拉出来给 Core 算进度（个人库规模有限）。</summary>
    public IReadOnlyList<TodoRow> QueryProjectTodos(bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.Todos.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(t => t.DeletedAt == null);
        }

        return query.ToList();
    }
}
