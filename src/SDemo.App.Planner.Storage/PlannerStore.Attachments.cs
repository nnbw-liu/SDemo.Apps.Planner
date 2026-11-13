using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// 附件元数据读写。文件本体由 <c>PlannerService</c> 负责（复制 / 删除 / 迁移），
/// 这里只保证元数据与 op_log 在同一事务里。
/// </summary>
public sealed partial class PlannerStore
{
    public IReadOnlyList<AttachmentRow> QueryAttachments(string ownerType, string ownerId)
    {
        using var db = Open();
        return db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId && a.DeletedAt == null)
            .OrderBy(a => a.CreatedAt)
            .ToList();
    }

    public AttachmentRow? GetAttachment(string id)
    {
        using var db = Open();
        return db.Attachments.AsNoTracking().FirstOrDefault(a => a.Id == id && a.DeletedAt == null);
    }

    /// <summary>迁移 / 统计用：一次取全部附件行（默认不含回收站里的）。</summary>
    public IReadOnlyList<AttachmentRow> QueryAllAttachments(bool includeDeleted = false)
    {
        using var db = Open();
        var query = db.Attachments.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(a => a.DeletedAt == null);
        }

        return query.OrderBy(a => a.CreatedAt).ToList();
    }

    public void InsertAttachment(AttachmentRow row, string actor)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            db.Attachments.Add(row);
            AddOpLog(db, "attachment", row.Id, "create", null, Json(row), actor, null, null);
            db.SaveChanges();
            transaction.Commit();
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>软删除（回收站语义）：文件由调用方在同一流程里删除。</summary>
    public bool DeleteAttachment(string id, DateTime nowUtc, string actor)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = db.Attachments.FirstOrDefault(a => a.Id == id && a.DeletedAt == null);
            if (row is null)
            {
                return false;
            }

            row.DeletedAt = nowUtc;
            row.UpdatedAt = nowUtc;
            AddOpLog(db, "attachment", row.Id, "delete", Json(row), null, actor, null, null);
            db.SaveChanges();
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>回收站到期：物理删除元数据行，文件由调用方先删。</summary>
    public bool PurgeAttachment(string id)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var row = db.Attachments.FirstOrDefault(a => a.Id == id);
            if (row is null)
            {
                return false;
            }

            db.Attachments.Remove(row);
            AddOpLog(db, "attachment", row.Id, "purge", Json(row), null, "system", null, "回收站到期");
            db.SaveChanges();
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (IsLocked(ex))
        {
            throw new PlannerStoreLockedException(ex);
        }
    }

    /// <summary>删除所属对象（任务 / 项目 / 迭代）被物理清理时，连带清掉附件元数据。</summary>
    public IReadOnlyList<AttachmentRow> PurgeAttachmentsForOwner(string ownerType, string ownerId)
    {
        using var db = Open();
        try
        {
            using var transaction = db.Database.BeginTransaction();
            var rows = db.Attachments.Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId).ToList();
            if (rows.Count == 0)
            {
                return [];
            }

            foreach (var row in rows)
            {
                db.Attachments.Remove(row);
                AddOpLog(db, "attachment", row.Id, "purge", Json(row), null, "system", null, "所属对象已清理");
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
