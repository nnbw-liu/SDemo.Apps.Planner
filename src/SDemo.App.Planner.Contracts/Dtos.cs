namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 待办条目。todo 可完成、只有截止点；**没有** start/end —— 起止是 event 的事（PL-01）。
/// </summary>
public sealed record TodoDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Notes { get; init; }

    public TodoStatus Status { get; init; } = TodoStatus.Open;

    /// <summary>0 最低、4 最高；默认 2。</summary>
    public int Priority { get; init; } = 2;

    public DateTimeOffset? DueAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ListId { get; init; }

    public string? ListName { get; init; }

    /// <summary>乐观并发版本；每次写 +1。删除也占用一个版本。</summary>
    public int Revision { get; init; }

    public TodoSource Source { get; init; } = TodoSource.Manual;

    public string? SourceSessionId { get; init; }

    public string? SourceExcerpt { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>跨应用引用字符串，例如 <c>sdemo.kb/doc/doc_01H@v3#b2</c>。</summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public DateTimeOffset? RestorableUntil { get; init; }
}

/// <summary>
/// 日程。有起、有止、**不能勾完成**；与 todo 同库不同表，接口也分开（PL-01 / PL-03）。
/// </summary>
public sealed record EventDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Notes { get; init; }

    public required DateTimeOffset StartAt { get; init; }

    public required DateTimeOffset EndAt { get; init; }

    public string? Location { get; init; }

    public IReadOnlyList<string> Participants { get; init; } = [];

    /// <summary>关联的待办 id（会前准备等）；只存本应用实体 id，不跨应用调用。</summary>
    public string? LinkedTodoId { get; init; }

    public string? ListId { get; init; }

    public int Revision { get; init; }

    public TodoSource Source { get; init; } = TodoSource.Manual;

    public IReadOnlyList<string> Links { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>清单。默认清单不可删除，只能改名/换色。</summary>
public sealed record ListDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Color { get; init; }

    public bool IsDefault { get; init; }

    public int Sort { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>草稿里的一条候选待办。excerpt 必填——每条都能回答「从哪句话来的」（PL-11）。</summary>
public sealed record DraftItemDto
{
    public required string DraftItemId { get; init; }

    public required string Title { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public int Priority { get; init; } = 2;

    public required string Excerpt { get; init; }

    /// <summary>疑似重复的正式待办 id；由应用侧相似度判断填入，不让模型猜。</summary>
    public string? SuspectDuplicateOf { get; init; }

    /// <summary>pending / accepted / rejected（逐条决策，见 PL-09）。</summary>
    public string Decision { get; init; } = "pending";
}

/// <summary>草稿。草稿区与正式表物理分开，采纳才写 todo（PL-09）。</summary>
public sealed record DraftDto
{
    public required string DraftId { get; init; }

    public DraftStatus Status { get; init; } = DraftStatus.Pending;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; init; }

    public TodoSource Source { get; init; } = TodoSource.Ai;

    public string? SourceSessionId { get; init; }

    public string? ListId { get; init; }

    public IReadOnlyList<DraftItemDto> Items { get; init; } = [];

    public IReadOnlyList<string> Questions { get; init; } = [];
}

/// <summary>日历区间结果：todo 的 dueAt 是点，event 是区间。</summary>
public sealed record CalendarRangeDto
{
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public IReadOnlyList<TodoDto> Todos { get; init; } = [];

    public IReadOnlyList<EventDto> Events { get; init; } = [];
}

/// <summary>统计：完成率只统计 todo，不受 event 污染（PL-01 验收）。</summary>
public sealed record StatsDto
{
    public int DueToday { get; init; }

    public int Overdue { get; init; }

    public int OpenTotal { get; init; }

    public int DoneToday { get; init; }

    public int DoneLast7Days { get; init; }

    public int CreatedLast7Days { get; init; }

    public double CompletionRateLast7Days { get; init; }

    public IReadOnlyList<ListCountDto> ByList { get; init; } = [];
}

public sealed record ListCountDto
{
    public string? ListId { get; init; }

    public string? ListName { get; init; }

    public int OpenCount { get; init; }
}

/// <summary>全文检索命中。</summary>
public sealed record SearchHitDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>命中片段（带省略号），不是整条备注。</summary>
    public string? Snippet { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public TodoStatus Status { get; init; }

    public string? ListId { get; init; }

    /// <summary>bm25 分数，越小越相关（SQLite FTS5 口径）。</summary>
    public double Score { get; init; }
}

public sealed record SearchResultDto
{
    public required string Query { get; init; }

    public IReadOnlyList<SearchHitDto> Hits { get; init; } = [];

    /// <summary>AND 太严时放宽为 OR；让模型知道结果被放宽过。</summary>
    public bool Widened { get; init; }
}
