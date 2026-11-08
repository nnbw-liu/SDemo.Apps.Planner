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

    /// <summary>完成/验收说明；完成弹窗里填写，可后续补充。</summary>
    public string? CompletionNote { get; init; }

    /// <summary>完成后的产物：文件路径、链接、提交号等，一行一个。</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    public string? ListId { get; init; }

    public string? ListName { get; init; }

    /// <summary>所属项目；旧数据可能为空（未归属）。</summary>
    public string? ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public string? ProjectColor { get; init; }

    /// <summary>所属迭代（大/小均可）；为空表示只挂在项目下。</summary>
    public string? IterationId { get; init; }

    public string? IterationName { get; init; }

    public WorkItemKind Kind { get; init; } = WorkItemKind.Task;

    /// <summary>子任务父 id；为空表示顶层条目。</summary>
    public string? ParentTodoId { get; init; }

    public int Sort { get; init; }

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
/// 任务的一条操作记录。由 op_log 的 before/after 快照计算而来，字段值已转成中文。
/// </summary>
public sealed record TodoHistoryEntryDto
{
    /// <summary>op_log 自增 id，同毫秒内也能稳定排序。</summary>
    public long Id { get; init; }

    /// <summary>create / update / status / postpone / reopen / delete / restore 等原始操作。</summary>
    public required string Op { get; init; }

    /// <summary>面向用户的中文动作，例如「修改任务」「状态改为 完成」。</summary>
    public required string Action { get; init; }

    /// <summary>manual / ai / meeting / import / ics / system。</summary>
    public string Actor { get; init; } = "manual";

    public string? Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>实际变化的字段；记录本身不一定有字段差异（例如恢复）。</summary>
    public IReadOnlyList<TodoChangeDto> Changes { get; init; } = [];
}

/// <summary>一条字段变化；Before/After 已格式化，空值显示为「—」。</summary>
public sealed record TodoChangeDto
{
    public required string Field { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }
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
