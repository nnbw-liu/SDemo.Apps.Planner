using Microsoft.EntityFrameworkCore;

namespace SDemo.App.Planner.Storage;

/// <summary>外部系统（Azure DevOps）镜像的查询。写入仍走 WriteTodos / WriteIteration / WriteProject。</summary>
public sealed partial class PlannerStore
{
    public TodoRow? GetTodoByExternal(string source, string externalId)
    {
        using var db = Open();
        return db.Todos.AsNoTracking()
            .FirstOrDefault(t => t.ExternalSource == source
                && t.ExternalId == externalId
                && t.DeletedAt == null);
    }

    public IterationRow? GetIterationByExternal(string source, string externalId)
    {
        using var db = Open();
        return db.Iterations.AsNoTracking()
            .FirstOrDefault(i => i.ExternalSource == source
                && i.ExternalId == externalId
                && i.DeletedAt == null);
    }

    public ProjectRow? GetProjectByExternal(string source, string externalId)
    {
        using var db = Open();
        return db.Projects.AsNoTracking()
            .FirstOrDefault(p => p.ExternalSource == source
                && p.ExternalId == externalId
                && p.DeletedAt == null);
    }

    /// <summary>任务的外部字段单独更新（同步时不动别人的 revision 之外的东西）。</summary>
    public void UpdateTodoExternal(string todoId, string source, string externalId, int? rev, string? url, DateTime nowUtc)
    {
        using var db = Open();
        var row = db.Todos.FirstOrDefault(t => t.Id == todoId);
        if (row is null)
        {
            return;
        }

        row.ExternalSource = source;
        row.ExternalId = externalId;
        row.ExternalRev = rev;
        row.ExternalUrl = url;
        row.UpdatedAt = nowUtc;
        db.SaveChanges();
    }
}
