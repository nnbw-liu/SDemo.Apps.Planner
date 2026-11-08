namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 写操作的来源上下文。**必须**由调用方（UI 或 MainAgent）注入，应用不接受模型自称 manual，
/// 否则 provenance 可以被伪造（planner-mcp-surface §9）。
/// </summary>
public sealed record WriteContext
{
    public TodoSource Source { get; init; } = TodoSource.Manual;

    public string? SessionId { get; init; }

    /// <summary>用户原话片段（取第一句即可），进 todo.sourceExcerpt。</summary>
    public string? Excerpt { get; init; }

    public string? RequestId { get; init; }

    /// <summary>幂等键：同一 clientRequestId 在 10 分钟内重复，返回首次结果。</summary>
    public string? ClientRequestId { get; init; }

    public string? Reason { get; init; }

    public static WriteContext Manual(string? requestId = null) => new()
    {
        Source = TodoSource.Manual,
        RequestId = requestId,
    };

    public static WriteContext Ai(string? sessionId, string? excerpt, string? requestId = null, string? clientRequestId = null) => new()
    {
        Source = TodoSource.Ai,
        SessionId = sessionId,
        Excerpt = excerpt,
        RequestId = requestId,
        ClientRequestId = clientRequestId,
    };
}

/// <summary>
/// <c>todo_list</c> / UI 筛选条件。默认只返回未完成项，按截止时间与优先级排序。
/// </summary>
public sealed record TodoQuery
{
    /// <summary>
    /// open/active = 未完成（新建+进行中）；new = 仅新建；doing / done / verified / closed / canceled；
    /// all / deleted。
    /// </summary>
    public string Status { get; init; } = "open";

    public string? ListId { get; init; }

    public string? ProjectId { get; init; }

    public string? IterationId { get; init; }

    /// <summary>只返回直接挂在该任务下的子项（例如任务衍生的 Bug）。</summary>
    public string? ParentTodoId { get; init; }

    /// <summary>task / bug / research / chore；null 表示不过滤。</summary>
    public WorkItemKind? Kind { get; init; }

    /// <summary>为 true 时只返回未归属项目的条目。</summary>
    public bool NoProject { get; init; }

    public DateTimeOffset? DueBefore { get; init; }

    public DateTimeOffset? DueAfter { get; init; }

    public string? Tag { get; init; }

    public string? Q { get; init; }

    /// <summary>排序方式；UI 默认按优先级，API 默认按截止时间。</summary>
    public TodoSort Sort { get; init; } = TodoSort.Due;

    public int Limit { get; init; } = PlannerConstants.DefaultListLimit;

    public bool IncludeDeleted { get; init; }
}

public sealed record TodoCreateInput
{
    public required string Title { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public int Priority { get; init; } = 2;

    public string? ListId { get; init; }

    public string? ProjectId { get; init; }

    public string? IterationId { get; init; }

    public WorkItemKind Kind { get; init; } = WorkItemKind.Task;

    public string? ParentTodoId { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public IReadOnlyList<string>? Links { get; init; }
}

/// <summary>
/// 部分更新。null 表示「不改」，清空用显式的 Clear* 开关，避免「传 null 到底是不改还是清空」的歧义。
/// </summary>
public sealed record TodoPatch
{
    public string? Title { get; init; }

    public string? Notes { get; init; }

    public bool ClearNotes { get; init; }

    public TodoStatus? Status { get; init; }

    public int? Priority { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public bool ClearDueAt { get; init; }

    public string? ListId { get; init; }

    public string? ProjectId { get; init; }

    public bool ClearProject { get; init; }

    public string? IterationId { get; init; }

    public bool ClearIteration { get; init; }

    public WorkItemKind? Kind { get; init; }

    public string? ParentTodoId { get; init; }

    public bool ClearParent { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public IReadOnlyList<string>? Links { get; init; }

    public bool IsNoOp =>
        Title is null && Notes is null && !ClearNotes && Status is null && Priority is null
        && DueAt is null && !ClearDueAt && ListId is null && Tags is null && Links is null
        && ProjectId is null && !ClearProject && IterationId is null && !ClearIteration
        && Kind is null && ParentTodoId is null && !ClearParent;
}

public sealed record EventCreateInput
{
    public required string Title { get; init; }

    public required DateTimeOffset StartAt { get; init; }

    public required DateTimeOffset EndAt { get; init; }

    public string? Notes { get; init; }

    public string? Location { get; init; }

    public IReadOnlyList<string>? Participants { get; init; }

    public string? LinkedTodoId { get; init; }

    public string? ListId { get; init; }

    public IReadOnlyList<string>? Links { get; init; }
}

public sealed record EventPatch
{
    public string? Title { get; init; }

    public string? Notes { get; init; }

    public bool ClearNotes { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }

    public string? Location { get; init; }

    public bool ClearLocation { get; init; }

    public IReadOnlyList<string>? Participants { get; init; }

    public string? LinkedTodoId { get; init; }

    public bool ClearLinkedTodo { get; init; }

    public string? ListId { get; init; }

    public IReadOnlyList<string>? Links { get; init; }
}

/// <summary>逐条采纳：Reject=true 表示拒绝该条，其余按 Title/Notes/DueAt 等覆盖草稿值。</summary>
public sealed record DraftAcceptItem
{
    public required string DraftItemId { get; init; }

    public bool Reject { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public int? Priority { get; init; }
}

public sealed record DraftAcceptRequest
{
    public required string DraftId { get; init; }

    public IReadOnlyList<DraftAcceptItem>? Items { get; init; }

    public string? ListId { get; init; }
}

/// <summary>待办在 ICS 里的表示：Calendar 用 VEVENT（Outlook/系统日历能显示），Task 用 VTODO。</summary>
public enum IcsTodoExportMode
{
    Calendar,
    Task,
}

public sealed record IcsExportOptions
{
    public bool IncludeTodos { get; init; } = true;

    public bool IncludeEvents { get; init; } = true;

    public string? CalendarName { get; init; }

    /// <summary>已完成的待办是否导出（导入后显示为已完成）。</summary>
    public bool IncludeCompleted { get; init; } = true;

    /// <summary>默认 VEVENT：Outlook 的“作为新日历/导入”会忽略 VTODO，导致导入后看不到内容。</summary>
    public IcsTodoExportMode TodoMode { get; init; } = IcsTodoExportMode.Calendar;
}
