namespace SDemo.App.Planner.Storage;

/// <summary>
/// 待办行。状态/来源用字符串落库（便于外部工具直接读库排障），映射在 Core。
/// 所有 *At 列都是 UTC；列名见 PlannerDbContext.OnModelCreating。
/// </summary>
public sealed class TodoRow
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>open / doing / done。</summary>
    public string Status { get; set; } = "open";

    public int Priority { get; set; } = 2;

    public DateTime? DueAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ListId { get; set; }

    public int Revision { get; set; }

    /// <summary>manual / ai / meeting / import / ics。</summary>
    public string Source { get; set; } = "manual";

    public string? SourceSessionId { get; set; }

    public string? SourceExcerpt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? RestorableUntil { get; set; }

    /// <summary>todo_fts 的 rowid；索引时写入，避免按不可索引列删 FTS 行。</summary>
    public long? FtsRowId { get; set; }
}

public sealed class EventRow
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTime StartAt { get; set; }

    public DateTime EndAt { get; set; }

    public string? Location { get; set; }

    /// <summary>参与人 JSON 数组（人不多，拆表没有收益）。</summary>
    public string? ParticipantsJson { get; set; }

    public string? LinkedTodoId { get; set; }

    public string? ListId { get; set; }

    public int Revision { get; set; }

    public string Source { get; set; } = "manual";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? RestorableUntil { get; set; }
}

public sealed class ListRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; }

    public bool IsDefault { get; set; }

    public int Sort { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class TagRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public sealed class TodoTagRow
{
    public string TodoId { get; set; } = string.Empty;

    public string TagId { get; set; } = string.Empty;
}

/// <summary>跨应用引用：只存 URI 字符串字符串，解析责任在 DesktopAgent（R6）。</summary>
public sealed class LinkRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>todo / event。</summary>
    public string OwnerType { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string Uri { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public sealed class DraftRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>pending / accepted / rejected。</summary>
    public string Status { get; set; } = "pending";

    public string Source { get; set; } = "ai";

    public string? SourceSessionId { get; set; }

    public string? ListId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? AcceptedAt { get; set; }
}

public sealed class DraftItemRow
{
    public string Id { get; set; } = string.Empty;

    public string DraftId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public DateTime? DueAt { get; set; }

    public int Priority { get; set; } = 2;

    public string Excerpt { get; set; } = string.Empty;

    public string? SuspectDuplicateOf { get; set; }

    public int Sort { get; set; }

    /// <summary>pending / accepted / rejected。</summary>
    public string Decision { get; set; } = "pending";
}

/// <summary>P5 同步预留：每次写操作同时落一条 op-log（不要事后补）。</summary>
public sealed class OutboxRow
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Op { get; set; } = string.Empty;

    public int Revision { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }

    public DateTime? SyncedAt { get; set; }
}

/// <summary>写操作审计 / 撤销依据：before/after 各一份快照，可反向执行。</summary>
public sealed class OpLogRow
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Op { get; set; } = string.Empty;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public string Actor { get; set; } = "manual";

    public string? RequestId { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>MCP 调用审计：参数只存摘要不存全文（备注可能很长）。</summary>
public sealed class McpAuditRow
{
    public long Id { get; set; }

    public string Tool { get; set; } = string.Empty;

    public string ParamsSummary { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public string ResultCode { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string? RequestId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>幂等窗口：同一 clientRequestId 10 分钟内重复调用直接回放首次结果。</summary>
public sealed class IdempotencyRow
{
    public string ClientRequestId { get; set; } = string.Empty;

    public string ResponseJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
}
