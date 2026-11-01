namespace SDemo.App.Planner.Storage;

/// <summary>乐观并发冲突：写库时 revision 与期望不一致。</summary>
public sealed class PlannerStoreConflictException(string entityId, int currentRevision, int expectedRevision)
    : Exception($"条目 {entityId} 已被修改（当前 revision={currentRevision}，期望 {expectedRevision}）")
{
    public string EntityId { get; } = entityId;

    public int CurrentRevision { get; } = currentRevision;

    public int ExpectedRevision { get; } = expectedRevision;
}

/// <summary>数据库被占用（同步写 / 外部进程锁）。</summary>
public sealed class PlannerStoreLockedException(Exception inner)
    : Exception("数据库被占用", inner);

/// <summary>写操作的结构化输入：行快照 + 操作名 + 审计上下文。</summary>
public sealed class TodoWrite
{
    public required TodoRow Row { get; init; }

    /// <summary>create / update / complete / reopen / delete / restore / purge。</summary>
    public required string Op { get; init; }

    /// <summary>null 表示新建；否则必须与库里的 revision 完全一致，否则判冲突。</summary>
    public int? ExpectedRevision { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Links { get; init; } = [];

    public string Actor { get; init; } = "manual";

    public string? RequestId { get; init; }

    public string? Reason { get; init; }
}

public sealed class EventWrite
{
    public required EventRow Row { get; init; }

    public required string Op { get; init; }

    public int? ExpectedRevision { get; init; }

    public IReadOnlyList<string> Links { get; init; } = [];

    public string Actor { get; init; } = "manual";

    public string? RequestId { get; init; }

    public string? Reason { get; init; }
}

public sealed record TodoFilter
{
    public string Status { get; init; } = "open";

    public string? ListId { get; init; }

    public DateTime? DueBeforeUtc { get; init; }

    public DateTime? DueAfterUtc { get; init; }

    public string? TagId { get; init; }

    public string? Keyword { get; init; }

    public int Limit { get; init; } = 20;

    public bool IncludeDeleted { get; init; }
}

public readonly record struct SearchIdRow(string Id, double Score);

public sealed record StatsRaw(
    int OpenTotal,
    int DueToday,
    int Overdue,
    int DoneToday,
    int DoneLast7Days,
    int CreatedLast7Days,
    IReadOnlyList<(string? ListId, string? ListName, int OpenCount)> ByList);

public sealed record TodoTitleRow(string Id, string Title, string? Notes);
