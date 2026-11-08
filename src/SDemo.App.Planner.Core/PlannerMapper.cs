using System.Globalization;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>Storage 行 → 线格式 DTO。只在这一处做时间/枚举/JSON 的转换。</summary>
public static class PlannerMapper
{
    public static TodoDto ToTodo(
        TodoRow row,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> links,
        string? listName = null,
        string? projectName = null,
        string? projectColor = null,
        string? iterationName = null) =>
        new()
        {
            Id = row.Id,
            Title = row.Title,
            Notes = row.Notes,
            Status = ParseStatus(row.Status),
            Priority = row.Priority,
            DueAt = ToDto(row.DueAt),
            CompletedAt = ToDto(row.CompletedAt),
            CompletionNote = row.CompletionNote,
            Artifacts = ParseArtifacts(row.ArtifactsJson),
            ListId = row.ListId,
            ListName = listName,
            ProjectId = row.ProjectId,
            ProjectName = projectName,
            ProjectColor = projectColor,
            IterationId = row.IterationId,
            IterationName = iterationName,
            Kind = ParseKind(row.Kind),
            ParentTodoId = row.ParentTodoId,
            Sort = row.Sort,
            Revision = row.Revision,
            Source = ParseSource(row.Source),
            SourceSessionId = row.SourceSessionId,
            SourceExcerpt = row.SourceExcerpt,
            Tags = tags,
            Links = links,
            CreatedAt = ToDto(row.CreatedAt)!.Value,
            UpdatedAt = ToDto(row.UpdatedAt)!.Value,
            DeletedAt = ToDto(row.DeletedAt),
            RestorableUntil = ToDto(row.RestorableUntil),
        };

    public static ProjectDto ToProject(
        ProjectRow row,
        IReadOnlyList<IterationDto> iterations,
        IReadOnlyList<ProjectGroupDto>? groups = null) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Color = row.Color,
        Status = ParseProjectStatus(row.Status),
        StartAt = ToDto(row.StartAt),
        TargetAt = ToDto(row.TargetAt),
        Sort = row.Sort,
        Revision = row.Revision,
        Groups = groups ?? [],
        Iterations = iterations,
        CreatedAt = ToDto(row.CreatedAt)!.Value,
        UpdatedAt = ToDto(row.UpdatedAt)!.Value,
    };

    public static ProjectGroupDto ToProjectGroup(ProjectGroupRow row, int projectCount = 0) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Color = row.Color,
        Sort = row.Sort,
        Revision = row.Revision,
        ProjectCount = projectCount,
        CreatedAt = ToDto(row.CreatedAt)!.Value,
    };

    public static IterationDto ToIteration(
        IterationRow row,
        string? projectName = null,
        string? projectColor = null,
        string? parentName = null) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ProjectName = projectName,
        ProjectColor = projectColor,
        ParentId = row.ParentId,
        ParentName = parentName,
        Level = row.Level == "small" ? IterationLevel.Small : IterationLevel.Big,
        Name = row.Name,
        Goal = row.Goal,
        Status = ParseIterationStatus(row.Status),
        StartAt = ToDto(row.StartAt),
        EndAt = ToDto(row.EndAt),
        CompletedAt = ToDto(row.CompletedAt),
        CompletionNote = row.CompletionNote,
        Sort = row.Sort,
        Revision = row.Revision,
        CreatedAt = ToDto(row.CreatedAt)!.Value,
        UpdatedAt = ToDto(row.UpdatedAt)!.Value,
    };

    public static RoutineDto ToRoutine(RoutineRow row, int todayCount = 0) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Emoji = row.Emoji,
        Notes = row.Notes,
        DaysMask = row.DaysMask,
        TimeOfDay = row.TimeOfDay,
        IntervalMinutes = row.IntervalMinutes,
        ActiveEnd = row.ActiveEnd,
        OnceAt = ToDto(row.OnceAt),
        TargetCount = row.TargetCount,
        Remind = row.Remind,
        Enabled = row.Enabled,
        Sort = row.Sort,
        Revision = row.Revision,
        TodayCount = todayCount,
        CreatedAt = ToDto(row.CreatedAt)!.Value,
        UpdatedAt = ToDto(row.UpdatedAt)!.Value,
    };

    public static HolidayDto ToHoliday(HolidayRow row) => new()
    {
        Date = DateOnly.ParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        Name = row.Name,
        IsWorkday = row.IsWorkday,
        Source = row.Source,
        UpdatedAt = ToDto(row.UpdatedAt)!.Value,
    };

    public static EventDto ToEvent(EventRow row, IReadOnlyList<string> links, IReadOnlyList<string>? participants = null) =>        new()
        {
            Id = row.Id,
            Title = row.Title,
            Notes = row.Notes,
            StartAt = ToDto(row.StartAt)!.Value,
            EndAt = ToDto(row.EndAt)!.Value,
            Location = row.Location,
            Participants = participants ?? ParseParticipants(row.ParticipantsJson),
            LinkedTodoId = row.LinkedTodoId,
            ListId = row.ListId,
            Revision = row.Revision,
            Source = ParseSource(row.Source),
            Links = links,
            CreatedAt = ToDto(row.CreatedAt)!.Value,
            UpdatedAt = ToDto(row.UpdatedAt)!.Value,
            DeletedAt = ToDto(row.DeletedAt),
        };

    public static ListDto ToList(ListRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Color = row.Color,
        IsDefault = row.IsDefault,
        Sort = row.Sort,
        CreatedAt = ToDto(row.CreatedAt)!.Value,
    };

    public static DraftItemDto ToDraftItem(DraftItemRow row) => new()
    {
        DraftItemId = row.Id,
        Title = row.Title,
        Notes = row.Notes,
        DueAt = ToDto(row.DueAt),
        Priority = row.Priority,
        Excerpt = row.Excerpt,
        SuspectDuplicateOf = row.SuspectDuplicateOf,
        Decision = row.Decision,
    };

    public static DraftDto ToDraft(DraftRow row, IReadOnlyList<DraftItemRow> items, IReadOnlyList<string> questions) => new()
    {
        DraftId = row.Id,
        Status = ParseDraftStatus(row.Status),
        CreatedAt = ToDto(row.CreatedAt)!.Value,
        AcceptedAt = ToDto(row.AcceptedAt),
        Source = ParseSource(row.Source),
        SourceSessionId = row.SourceSessionId,
        ListId = row.ListId,
        Items = items.Select(ToDraftItem).ToList(),
        Questions = questions,
    };

    /// <summary>
    /// 草稿文本拆分器产出的「问题」是给用户看的提示，不是给模型猜的。
    /// P2 不解析自然语言日期（契约禁止「明天」直接落库）。
    /// </summary>
    public static IReadOnlyList<string> ParseParticipants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json, PlannerJson.Wire) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static string SerializeParticipants(IReadOnlyList<string>? participants) =>
        System.Text.Json.JsonSerializer.Serialize(participants ?? [], PlannerJson.Wire);

    public static DateTime? ToUtc(DateTimeOffset? value) =>
        value?.ToUniversalTime().UtcDateTime;

    public static DateTimeOffset? ToDto(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    public static TodoStatus ParseStatus(string? value) => value switch
    {
        "doing" => TodoStatus.Doing,
        "done" => TodoStatus.Done,
        "verified" => TodoStatus.Verified,
        "closed" => TodoStatus.Closed,
        "canceled" => TodoStatus.Canceled,
        _ => TodoStatus.Open,
    };

    public static string ToStorage(TodoStatus status) => status switch
    {
        TodoStatus.Doing => "doing",
        TodoStatus.Done => "done",
        TodoStatus.Verified => "verified",
        TodoStatus.Closed => "closed",
        TodoStatus.Canceled => "canceled",
        _ => "open",
    };

    public static IReadOnlyList<string> ParseArtifacts(string? json) => ParseParticipants(json);

    public static string? SerializeArtifacts(IReadOnlyList<string>? artifacts)
    {
        var values = artifacts?.Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        return values.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(values, PlannerJson.Wire);
    }

    public static WorkItemKind ParseKind(string? value) => value switch
    {
        "bug" => WorkItemKind.Bug,
        "research" => WorkItemKind.Research,
        "chore" => WorkItemKind.Chore,
        _ => WorkItemKind.Task,
    };

    public static string ToStorage(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Bug => "bug",
        WorkItemKind.Research => "research",
        WorkItemKind.Chore => "chore",
        _ => "task",
    };

    public static ProjectStatus ParseProjectStatus(string? value) => value switch
    {
        "paused" => ProjectStatus.Paused,
        "done" => ProjectStatus.Done,
        "archived" => ProjectStatus.Archived,
        _ => ProjectStatus.Active,
    };

    public static string ToStorage(ProjectStatus status) => status switch
    {
        ProjectStatus.Paused => "paused",
        ProjectStatus.Done => "done",
        ProjectStatus.Archived => "archived",
        _ => "active",
    };

    public static IterationStatus ParseIterationStatus(string? value) => value switch
    {
        "active" => IterationStatus.Active,
        "done" => IterationStatus.Done,
        "archived" => IterationStatus.Archived,
        _ => IterationStatus.Planned,
    };

    public static string ToStorage(IterationStatus status) => status switch
    {
        IterationStatus.Active => "active",
        IterationStatus.Done => "done",
        IterationStatus.Archived => "archived",
        _ => "planned",
    };

    public static string ToStorage(IterationLevel level) => level == IterationLevel.Small ? "small" : "big";

    public static TodoSource ParseSource(string? value) => value switch
    {
        "ai" => TodoSource.Ai,
        "meeting" => TodoSource.Meeting,
        "import" => TodoSource.Import,
        "ics" => TodoSource.Ics,
        _ => TodoSource.Manual,
    };

    public static string ToStorage(TodoSource source) => source switch
    {
        TodoSource.Ai => "ai",
        TodoSource.Meeting => "meeting",
        TodoSource.Import => "import",
        TodoSource.Ics => "ics",
        _ => "manual",
    };

    public static DraftStatus ParseDraftStatus(string? value) => value switch
    {
        "accepted" => DraftStatus.Accepted,
        "rejected" => DraftStatus.Rejected,
        _ => DraftStatus.Pending,
    };
}
