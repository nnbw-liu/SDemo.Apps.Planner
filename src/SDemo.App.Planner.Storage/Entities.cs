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

    /// <summary>open / doing / done / verified / closed / canceled。</summary>
    public string Status { get; set; } = "open";

    public int Priority { get; set; } = 2;

    public DateTime? DueAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>完成/验收说明。</summary>
    public string? CompletionNote { get; set; }

    /// <summary>产物列表的 JSON 数组文本；空列表存 null。</summary>
    public string? ArtifactsJson { get; set; }

    public string? ListId { get; set; }

    /// <summary>所属项目；为空表示未归属项目（旧数据 / 收件箱）。</summary>
    public string? ProjectId { get; set; }

    /// <summary>所属迭代（大迭代或小迭代）；为空表示只挂在项目下。</summary>
    public string? IterationId { get; set; }

    /// <summary>task / bug / research / chore。</summary>
    public string Kind { get; set; } = "task";

    /// <summary>子任务的父 todo id。</summary>
    public string? ParentTodoId { get; set; }

    public int Sort { get; set; }

    public int Revision { get; set; }

    /// <summary>manual / ai / meeting / import / ics。</summary>
    public string Source { get; set; } = "manual";

    public string? SourceSessionId { get; set; }

    public string? SourceExcerpt { get; set; }

    /// <summary>外部来源（azure-devops）；为空表示本地创建。</summary>
    public string? ExternalSource { get; set; }

    public string? ExternalId { get; set; }

    /// <summary>外部系统里的版本号；用于「没变就跳过」。</summary>
    public int? ExternalRev { get; set; }

    public string? ExternalUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? RestorableUntil { get; set; }

    /// <summary>todo_fts 的 rowid；索引时写入，避免按不可索引列删 FTS 行。</summary>
    public long? FtsRowId { get; set; }
}

/// <summary>项目：任务的归属根。删除走软删，任务本身不级联删除。</summary>
public sealed class ProjectRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Color { get; set; } = "#4C8DFF";

    /// <summary>active / paused / done / archived。</summary>
    public string Status { get; set; } = "active";

    public DateTime? StartAt { get; set; }

    public DateTime? TargetAt { get; set; }

    public int Sort { get; set; }

    public int Revision { get; set; }

    /// <summary>外部来源（azure-devops）；为空表示本地项目。</summary>
    public string? ExternalSource { get; set; }

    public string? ExternalId { get; set; }

    public string? ExternalUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>项目分组：跨项目的集合（例如「产品」「外包」「个人」）。项目与分组多对多。</summary>
public sealed class ProjectGroupRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#7C8CF8";

    public int Sort { get; set; }

    public int Revision { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>项目与分组的成员关系；删除项目/分组时都清理这里。</summary>
public sealed class ProjectGroupMemberRow
{
    public string ProjectGroupId { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;
}

/// <summary>应用级键值设置（MCP 开关等），与业务实体分开，不参与 recycle bin。</summary>
public sealed class AppSettingRow
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>迭代：大迭代 parent 为空，小迭代挂大迭代；同一项目内两层。</summary>
public sealed class IterationRow
{
    public string Id { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string? ParentId { get; set; }

    /// <summary>big / small。</summary>
    public string Level { get; set; } = "big";

    public string Name { get; set; } = string.Empty;

    public string? Goal { get; set; }

    /// <summary>planned / active / done / archived。</summary>
    public string Status { get; set; } = "planned";

    public DateTime? StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? CompletionNote { get; set; }

    public int Sort { get; set; }

    public int Revision { get; set; }

    /// <summary>外部来源（azure-devops）；为空表示本地创建。</summary>
    public string? ExternalSource { get; set; }

    public string? ExternalId { get; set; }

    public string? ExternalUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>日常：每天要做的事，也是提醒来源。时间列存本地 HH:mm，不做时区换算。</summary>
public sealed class RoutineRow
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Emoji { get; set; } = "✅";

    public string? Notes { get; set; }

    /// <summary>bit0=周一 … bit6=周日。</summary>
    public int DaysMask { get; set; } = 127;

    /// <summary>本地 HH:mm。固定提醒时刻；间隔提醒时作为允许开始时间。</summary>
    public string? TimeOfDay { get; set; }

    public int? IntervalMinutes { get; set; }

    /// <summary>本地 HH:mm；间隔提醒的结束时间。</summary>
    public string? ActiveEnd { get; set; }

    /// <summary>仅一次提醒的 UTC 时刻；非空时忽略重复日与间隔设置，触发后自动停用。</summary>
    public DateTime? OnceAt { get; set; }

    public int TargetCount { get; set; } = 1;

    public bool Remind { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public int Sort { get; set; }

    public int Revision { get; set; }

    /// <summary>上次提醒时间（UTC），用于去重；固定与间隔提醒共用。</summary>
    public DateTime? LastFiredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>日常打卡：按「本地日期」计次，routine_id + local_date 唯一。</summary>
public sealed class RoutineCheckRow
{
    public string RoutineId { get; set; } = string.Empty;

    /// <summary>yyyy-MM-dd（本地日期，不是 UTC 日期）。</summary>
    public string LocalDate { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime? LastCompletedAt { get; set; }
}

/// <summary>中国法定节假日 / 调休补班；date 为本地日历日 yyyy-MM-dd。</summary>
public sealed class HolidayRow
{
    public string Date { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>true = 调休上班（班）；false = 放假（休）。</summary>
    public bool IsWorkday { get; set; }

    /// <summary>manual / remote；manual 优先，联网同步不覆盖。</summary>
    public string Source { get; set; } = "manual";

    public DateTime UpdatedAt { get; set; }
}

public sealed class EventRow
{    public string Id { get; set; } = string.Empty;

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

/// <summary>
/// 附件元数据。文件本体按 <see cref="RelativePath"/>（<c>ownerType/ownerId/id.ext</c>）
/// 存放在可配置附件目录，迁移目录时只动文件、不动这张表。
/// </summary>
public sealed class AttachmentRow
{
    public string Id { get; set; } = string.Empty;

    /// <summary>todo / project / iteration。</summary>
    public string OwnerType { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    /// <summary>原始文件名（展示 / 另存用）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>相对附件根目录的路径，始终用 <c>/</c> 分隔。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>幂等窗口：同一 clientRequestId 10 分钟内重复调用直接回放首次结果。</summary>
public sealed class IdempotencyRow
{
    public string ClientRequestId { get; set; } = string.Empty;

    public string ResponseJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
}
