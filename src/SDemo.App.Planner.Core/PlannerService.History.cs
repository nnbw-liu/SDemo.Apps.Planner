using System.Globalization;
using System.Text.Json;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 任务操作记录：直接读 op_log 的 before/after 快照，转换成面向用户的中文差异。
/// 快照由 PlannerStore 在每次写入的同一事务里落库，因此创建、改标题、调优先级、
/// 改状态、延期、删除、恢复都会留下记录；这里只负责读取和展示。
/// </summary>
public sealed partial class PlannerService
{
    public IReadOnlyList<TodoHistoryEntryDto> ListTodoHistory(string todoId, int limit = 100)
    {
        var rows = _store.QueryOpLogs("todo", todoId, limit);
        if (rows.Count == 0)
        {
            return [];
        }

        var projects = _store.QueryProjects(includeDeleted: true)
            .ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var iterations = _store.QueryIterations(null, includeDeleted: true)
            .ToDictionary(i => i.Id, i => i.Name, StringComparer.Ordinal);

        var history = new List<TodoHistoryEntryDto>(rows.Count);
        foreach (var row in rows)
        {
            var before = DeserializeSnapshot(row.BeforeJson);
            var after = DeserializeSnapshot(row.AfterJson);
            var changes = BuildChanges(row.Op, before, after, projects, iterations);

            history.Add(new TodoHistoryEntryDto
            {
                Id = row.Id,
                Op = row.Op,
                Action = ActionLabel(row.Op, after),
                Actor = row.Actor,
                Reason = row.Reason,
                CreatedAt = ToOffset(row.CreatedAt),
                Changes = changes,
            });
        }

        return history;
    }

    private static TodoRow? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TodoRow>(json, PlannerJson.Wire);
        }
        catch (JsonException)
        {
            // 审计快照损坏不应影响详情页其它内容；保留记录本身即可。
            return null;
        }
    }

    private static string ActionLabel(string op, TodoRow? after) => op switch
    {
        "create" => "创建任务",
        "update" => "修改任务",
        "status" => $"状态改为 {StatusLabel(after?.Status)}",
        "postpone" => "调整截止时间",
        "reopen" => "重新打开任务",
        "delete" => "删除任务",
        "restore" => "从回收站恢复",
        "purge" => "彻底删除",
        "bulk_update" => "批量修改",
        "accept_draft" => "从草稿创建",
        _ => op,
    };

    private IReadOnlyList<TodoChangeDto> BuildChanges(
        string op,
        TodoRow? before,
        TodoRow? after,
        IReadOnlyDictionary<string, string> projects,
        IReadOnlyDictionary<string, string> iterations)
    {
        // 删除/恢复只记动作；字段快照被软删状态整体覆盖，逐字段展示没有意义。
        if (op is "delete" or "restore" or "purge" || (before is null && after is null))
        {
            return [];
        }

        var changes = new List<TodoChangeDto>();

        if (before is null && after is not null)
        {
            // 创建：展示初始的可见字段，之后每条修改都能接上。
            Add(changes, "标题", null, after.Title);
            Add(changes, "状态", null, StatusLabel(after.Status));
            Add(changes, "优先级", null, PriorityLabel(after.Priority));
            Add(changes, "截止时间", null, FormatDue(after.DueAt));
            Add(changes, "项目", null, ProjectLabel(after.ProjectId, projects));
            Add(changes, "迭代", null, IterationLabel(after.IterationId, iterations));
            Add(changes, "类型", null, KindLabel(after.Kind));
            Add(changes, "任务说明", null, after.Notes);
            return changes;
        }

        if (before is not null && after is not null)
        {
            Add(changes, "标题", before.Title, after.Title);
            Add(changes, "任务说明", before.Notes, after.Notes);
            Add(changes, "状态", StatusLabel(before.Status), StatusLabel(after.Status));
            Add(changes, "优先级", PriorityLabel(before.Priority), PriorityLabel(after.Priority));
            Add(changes, "截止时间", FormatDue(before.DueAt), FormatDue(after.DueAt));
            Add(changes, "项目", ProjectLabel(before.ProjectId, projects), ProjectLabel(after.ProjectId, projects));
            Add(changes, "迭代", IterationLabel(before.IterationId, iterations), IterationLabel(after.IterationId, iterations));
            Add(changes, "类型", KindLabel(before.Kind), KindLabel(after.Kind));
            Add(changes, "完成说明", before.CompletionNote, after.CompletionNote);
            Add(changes, "产物", FormatArtifacts(before.ArtifactsJson), FormatArtifacts(after.ArtifactsJson));
        }

        return changes;
    }

    private static void Add(List<TodoChangeDto> changes, string field, string? before, string? after)
    {
        before = string.IsNullOrWhiteSpace(before) ? null : before;
        after = string.IsNullOrWhiteSpace(after) ? null : after;
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new TodoChangeDto
        {
            Field = field,
            Before = before ?? "—",
            After = after ?? "—",
        });
    }

    private static string StatusLabel(string? status) => status switch
    {
        "open" => "待办",
        "doing" => "进行中",
        "done" => "完成",
        "verified" => "已验收",
        "closed" => "已关闭",
        "canceled" => "已取消",
        _ => status ?? "—",
    };

    private static string PriorityLabel(int priority) => priority switch
    {
        1 => "高",
        2 => "中",
        3 => "低",
        0 => "无",
        _ => $"P{priority}",
    };

    private static string KindLabel(string? kind) => kind switch
    {
        "task" => "任务",
        "bug" => "Bug",
        "research" => "调研",
        "chore" => "杂务",
        _ => kind ?? "—",
    };

    private static string ProjectLabel(string? id, IReadOnlyDictionary<string, string> projects)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "未归属";
        }

        return projects.TryGetValue(id, out var name) ? name : id;
    }

    private static string IterationLabel(string? id, IReadOnlyDictionary<string, string> iterations)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "无迭代";
        }

        return iterations.TryGetValue(id, out var name) ? name : id;
    }

    private string? FormatDue(DateTime? dueAtUtc)
    {
        if (dueAtUtc is null)
        {
            return null;
        }

        var utc = DateTime.SpecifyKind(dueAtUtc.Value, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _time.LocalTimeZone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string? FormatArtifacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var items = JsonSerializer.Deserialize<string[]>(json, PlannerJson.Wire);
            return items is { Length: > 0 } ? string.Join("、", items) : null;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
