using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>写操作结果。MCP 层把它们映射进信封，UI 层直接绑定到列表。</summary>
public sealed record TodoBatchResult(IReadOnlyList<TodoDto> Items, IReadOnlyList<string> Warnings);

public sealed record TodoWriteResult(TodoDto Item, IReadOnlyList<string> Warnings);

public sealed record DeleteTodoResult(
    string Id,
    DateTimeOffset DeletedAt,
    DateTimeOffset RestorableUntil,
    int Revision);

public sealed record BulkTodoResult(
    int Updated,
    IReadOnlyList<TodoDto> Items,
    IReadOnlyList<string> Warnings);

public sealed record ClearListResult(int Deleted, string ListId);

public sealed record EventConflictDto(
    string EventId,
    string Title,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt);

public sealed record EventWriteResult(
    EventDto Item,
    IReadOnlyList<EventConflictDto> Conflicts);

public sealed record EventDeleteResult(
    string Id,
    DateTimeOffset DeletedAt,
    DateTimeOffset RestorableUntil,
    int Revision);

public sealed record DraftAcceptResult(
    string DraftId,
    bool Accepted,
    IReadOnlyList<TodoDto> Items,
    int Rejected,
    IReadOnlyList<string> Warnings);
