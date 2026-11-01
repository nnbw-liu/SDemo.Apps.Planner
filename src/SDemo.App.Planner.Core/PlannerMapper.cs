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
        string? listName = null) =>
        new()
        {
            Id = row.Id,
            Title = row.Title,
            Notes = row.Notes,
            Status = ParseStatus(row.Status),
            Priority = row.Priority,
            DueAt = ToDto(row.DueAt),
            CompletedAt = ToDto(row.CompletedAt),
            ListId = row.ListId,
            ListName = listName,
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

    public static EventDto ToEvent(EventRow row, IReadOnlyList<string> links, IReadOnlyList<string>? participants = null) =>
        new()
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
        _ => TodoStatus.Open,
    };

    public static string ToStorage(TodoStatus status) => status switch
    {
        TodoStatus.Doing => "doing",
        TodoStatus.Done => "done",
        _ => "open",
    };

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
