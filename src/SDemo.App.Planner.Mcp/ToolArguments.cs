using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Mcp;

/// <summary>
/// 工具入参模型。字段名与 schema 一致（camelCase），时间用 DateTimeOffset：
/// 不带偏移的时间串会反序列化失败，从根上堵住「明天下午三点」直接落库。
/// </summary>
public sealed class TodoListArgs
{
    public string Status { get; init; } = "open";

    public string? ListId { get; init; }

    public DateTimeOffset? DueBefore { get; init; }

    public DateTimeOffset? DueAfter { get; init; }

    public string? Tag { get; init; }

    public string? Q { get; init; }

    public int Limit { get; init; } = PlannerConstants.DefaultListLimit;
}

public sealed class TodoGetArgs
{
    public string Id { get; init; } = string.Empty;
}

public sealed class TodoCreateItemArgs
{
    public string Title { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public int Priority { get; init; } = 2;

    public string? ListId { get; init; }

    public List<string>? Tags { get; init; }

    public List<string>? Links { get; init; }
}

public sealed class TodoCreateArgs
{
    public List<TodoCreateItemArgs> Items { get; init; } = [];

    public string? ClientRequestId { get; init; }
}

public sealed class TodoUpdateArgs
{
    public string Id { get; init; } = string.Empty;

    public int? ExpectedRevision { get; init; }

    public TodoPatch Patch { get; init; } = new();
}

public sealed class TodoCompleteArgs
{
    public string Id { get; init; } = string.Empty;

    public int? ExpectedRevision { get; init; }
}

public sealed class TodoDeleteArgs
{
    public string Id { get; init; } = string.Empty;

    public int? ExpectedRevision { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class TodoBulkUpdateArgs
{
    public List<string> Ids { get; init; } = [];

    public TodoPatch Patch { get; init; } = new();
}

public sealed class TodoClearListArgs
{
    public string ListId { get; init; } = string.Empty;
}

public sealed class TodoPlanFromTextArgs
{
    public string Text { get; init; } = string.Empty;

    public string? Hint { get; init; }

    public string? ListId { get; init; }
}

public sealed class DraftDecisionArgs
{
    public string DraftItemId { get; init; } = string.Empty;

    public bool Reject { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public int? Priority { get; init; }
}

public sealed class TodoAcceptDraftArgs
{
    public string DraftId { get; init; } = string.Empty;

    public List<DraftDecisionArgs>? Items { get; init; }

    public string? ListId { get; init; }
}

public sealed class TodoSearchArgs
{
    public string Q { get; init; } = string.Empty;

    public int Limit { get; init; } = PlannerConstants.DefaultListLimit;
}
