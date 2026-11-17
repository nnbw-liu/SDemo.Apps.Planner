namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 任务的一条阶段性进展。任务工程往往不是一个动作就完成：每推进一步记一条，
/// 带上阶段名、正文、可选的整体进度百分比与这一步的产物（链接 / 路径 / 提交号）。
/// 与完成说明不同，进展是过程中的流水，不做终态限制。
/// </summary>
public sealed record TodoProgressDto
{
    public required string Id { get; init; }

    public required string TodoId { get; init; }

    /// <summary>阶段名，例如「方案评审」「联调」「验收」；未填时为「进展」。</summary>
    public required string Title { get; init; }

    /// <summary>这一步做了什么、结论、卡点。</summary>
    public string? Notes { get; init; }

    /// <summary>0–100 的整体进度；为空表示这条只记录信息，不改变进度。</summary>
    public int? Percent { get; init; }

    /// <summary>这一步的产物：链接、文件路径、提交号等，一行一个。</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    /// <summary>这条进展发生的时间（用户可改）；用于排序，不一定等于创建时间。</summary>
    public DateTimeOffset OccurredAt { get; init; }

    public int Revision { get; init; }

    /// <summary>挂在这条进展上的附件数量（文件实体在附件目录）。</summary>
    public int AttachmentCount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>新建一条进展。</summary>
public sealed record TodoProgressInput
{
    public string? Title { get; init; }

    public string? Notes { get; init; }

    public int? Percent { get; init; }

    public IReadOnlyList<string>? Artifacts { get; init; }

    /// <summary>发生时间；为空取当前时间。</summary>
    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>修改一条进展。未提供的字段保持原值；Clear* 用于显式清空可空字段。</summary>
public sealed record TodoProgressPatch
{
    public string? Title { get; init; }

    public string? Notes { get; init; }

    public bool ClearNotes { get; init; }

    public int? Percent { get; init; }

    public bool ClearPercent { get; init; }

    public IReadOnlyList<string>? Artifacts { get; init; }

    public bool ClearArtifacts { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>
/// 列表页只需「完成度」时用的汇总，不把每条正文都塞进响应。
/// </summary>
public sealed record TodoProgressSummaryDto
{
    public required string TodoId { get; init; }

    /// <summary>这条任务一共有几条进展。</summary>
    public int Count { get; init; }

    /// <summary>最近一条带百分比的进展给出的整体进度；没有则为空。</summary>
    public int? Percent { get; init; }

    /// <summary>最近一条进展的阶段名，用于列表上的「阶段」提示。</summary>
    public string? LatestTitle { get; init; }

    public DateTimeOffset? LatestAt { get; init; }
}
