using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 任务阶段性进展：一条记录一个阶段（方案、开发、联调、验收…），带正文、可选的整体
/// 进度百分比、这一步的产物链接，以及可以单独挂附件的能力。进展不做终态限制，
/// 任务完成后仍可追加复盘类进展。
/// </summary>
public sealed partial class PlannerService
{
    public IReadOnlyList<TodoProgressDto> ListTodoProgress(string todoId)
    {
        EnsureTodoExists(todoId);
        var rows = _store.QueryTodoProgress(todoId);
        var counts = CountAttachments(
            AttachmentOwnerTypes.Progress,
            rows.Select(r => r.Id).ToList());
        return rows.Select(row => MapTodoProgress(row, counts.GetValueOrDefault(row.Id))).ToList();
    }

    public TodoProgressDto AddTodoProgress(string todoId, TodoProgressInput input, WriteContext context)
    {
        EnsureTodoExists(todoId);

        var existing = _store.CountTodoProgress(todoId);
        if (existing >= PlannerConstants.MaxProgressPerTodo)
        {
            throw PlannerException.Limit($"每条任务最多 {PlannerConstants.MaxProgressPerTodo} 条进展，请先清理旧记录");
        }

        var now = NowUtc();
        var row = new TodoProgressRow
        {
            Id = NewId("prg"),
            TodoId = todoId,
            Title = NormalizeProgressTitle(input.Title),
            Notes = NormalizeProgressNotes(input.Notes),
            Percent = NormalizePercent(input.Percent),
            ArtifactsJson = PlannerMapper.SerializeArtifacts(NormalizeProgressArtifacts(input.Artifacts)),
            OccurredAt = input.OccurredAt?.ToUniversalTime().UtcDateTime ?? now,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _store.InsertTodoProgress(row, ActorOf(context.Source), context.RequestId);
        RaiseChanged();
        return MapTodoProgress(row, 0);
    }

    public TodoProgressDto UpdateTodoProgress(
        string id,
        int expectedRevision,
        TodoProgressPatch patch,
        WriteContext context)
    {
        var current = _store.GetTodoProgress(id)
            ?? throw PlannerException.NotFound("这条进展不存在或已删除");
        if (current.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(current.Revision);
        }
        var row = new TodoProgressRow
        {
            Id = current.Id,
            TodoId = current.TodoId,
            Title = patch.Title is null ? current.Title : NormalizeProgressTitle(patch.Title),
            Notes = patch.ClearNotes
                ? null
                : patch.Notes is null ? current.Notes : NormalizeProgressNotes(patch.Notes),
            Percent = patch.ClearPercent
                ? null
                : patch.Percent is null ? current.Percent : NormalizePercent(patch.Percent),
            ArtifactsJson = patch.ClearArtifacts
                ? null
                : patch.Artifacts is null
                    ? current.ArtifactsJson
                    : PlannerMapper.SerializeArtifacts(NormalizeProgressArtifacts(patch.Artifacts)),
            OccurredAt = patch.OccurredAt?.ToUniversalTime().UtcDateTime ?? current.OccurredAt,
            Revision = expectedRevision,
            CreatedAt = current.CreatedAt,
            UpdatedAt = NowUtc(),
        };

        _store.UpdateTodoProgress(row, expectedRevision, ActorOf(context.Source), context.RequestId);
        RaiseChanged();

        var saved = _store.GetTodoProgress(id)!;
        var counts = CountAttachments(AttachmentOwnerTypes.Progress, [id]);
        return MapTodoProgress(saved, counts.GetValueOrDefault(id));
    }

    public void DeleteTodoProgress(
        string id,
        int expectedRevision,
        string? reason,
        WriteContext context)
    {
        var current = _store.GetTodoProgress(id)
            ?? throw PlannerException.NotFound("这条进展不存在或已删除");
        if (current.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(current.Revision);
        }

        _store.DeleteTodoProgress(
            id,
            expectedRevision,
            NowUtc(),
            ActorOf(context.Source),
            context.RequestId,
            reason);

        RaiseChanged();
    }

    /// <summary>挂某条进展上的附件数量（面板刷新时一次批量取）。</summary>
    public IReadOnlyDictionary<string, int> CountAttachments(string ownerType, IReadOnlyList<string> ownerIds)
    {
        if (!AttachmentOwnerTypes.IsSupported(ownerType))
        {
            return new Dictionary<string, int>();
        }

        return _store.CountAttachments(
            ownerType,
            ownerIds.Where(IsSafeAttachmentSegment).ToList());
    }

    private void EnsureTodoExists(string todoId)
    {
        if (!IsSafeAttachmentSegment(todoId) || _store.GetTodo(todoId) is null)
        {
            throw PlannerException.NotFound("任务不存在或已删除");
        }
    }

    private static TodoProgressDto MapTodoProgress(TodoProgressRow row, int attachmentCount) => new()
    {
        Id = row.Id,
        TodoId = row.TodoId,
        Title = row.Title,
        Notes = row.Notes,
        Percent = row.Percent,
        Artifacts = PlannerMapper.ParseArtifacts(row.ArtifactsJson),
        OccurredAt = PlannerMapper.ToDto(row.OccurredAt)!.Value,
        Revision = row.Revision,
        AttachmentCount = attachmentCount,
        CreatedAt = PlannerMapper.ToDto(row.CreatedAt)!.Value,
        UpdatedAt = PlannerMapper.ToDto(row.UpdatedAt)!.Value,
    };

    private static string NormalizeProgressTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "进展";
        }

        if (trimmed.Length > PlannerConstants.MaxProgressTitleChars)
        {
            throw PlannerException.Validation($"阶段名超过 {PlannerConstants.MaxProgressTitleChars} 字符");
        }

        return trimmed;
    }

    private static string? NormalizeProgressNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var trimmed = notes.Trim();
        if (trimmed.Length > PlannerConstants.MaxProgressNotesChars)
        {
            throw PlannerException.Validation($"阶段说明超过 {PlannerConstants.MaxProgressNotesChars} 字符");
        }

        return trimmed;
    }

    private static int? NormalizePercent(int? percent)
    {
        if (percent is null)
        {
            return null;
        }

        if (percent is < 0 or > 100)
        {
            throw PlannerException.Validation("进度必须在 0 到 100 之间");
        }

        return percent;
    }

    private static IReadOnlyList<string> NormalizeProgressArtifacts(IReadOnlyList<string>? artifacts)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            return [];
        }

        if (artifacts.Count > PlannerConstants.MaxProgressArtifacts)
        {
            throw PlannerException.Limit($"单条进展最多 {PlannerConstants.MaxProgressArtifacts} 个产物");
        }

        return NormalizeArtifacts(artifacts);
    }

    /// <summary>列表用的汇总：条数、最近一条的阶段名、最近一个带百分比的整体进度。</summary>
    private IReadOnlyDictionary<string, TodoProgressSummaryDto> LoadProgressSummaries(IReadOnlyList<string> todoIds)
    {
        var rows = _store.QueryProgressForTodos(todoIds);
        if (rows.Count == 0)
        {
            return new Dictionary<string, TodoProgressSummaryDto>();
        }

        return rows
            .GroupBy(r => r.TodoId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group.OrderBy(r => r.OccurredAt).ThenBy(r => r.CreatedAt).ToList();
                    var latest = ordered[^1];
                    var percent = ordered.LastOrDefault(r => r.Percent is not null)?.Percent;
                    return new TodoProgressSummaryDto
                    {
                        TodoId = group.Key,
                        Count = ordered.Count,
                        Percent = percent,
                        LatestTitle = latest.Title,
                        LatestAt = PlannerMapper.ToDto(latest.OccurredAt),
                    };
                },
                StringComparer.Ordinal);
    }
}
