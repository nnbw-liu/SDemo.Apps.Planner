namespace SDemo.App.Planner.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Azure DevOps（ADO Server）接入：只读镜像。
// 外部系统是权威源，本地手工条目不受影响；重复同步按 externalId 幂等。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// ADO 连接设置：只管「连不连得上」——集合地址 + PAT。
/// 具体同步哪个云端项目、拉哪些人/迭代，属于项目级绑定（<see cref="AdoBindingDto"/>）。
/// PAT 只进不出，对外只暴露 <see cref="HasPat"/>。
/// </summary>
public sealed record AdoConnectionDto
{
    public bool Enabled { get; init; }

    /// <summary>集合地址，例如 <c>http://host:8088/Utilities</c>。</summary>
    public string CollectionUrl { get; init; } = string.Empty;

    public bool HasPat { get; init; }
}

/// <summary>保存连接设置；<see cref="Pat"/> 为空表示保留原值，<see cref="ClearPat"/> 表示清空。</summary>
public sealed record AdoConnectionInput
{
    public bool Enabled { get; init; }

    public string CollectionUrl { get; init; } = string.Empty;

    public string? Pat { get; init; }

    public bool ClearPat { get; init; }
}

/// <summary>
/// 一个本地项目对应哪个 ADO 项目，以及从它拉取时的范围。
/// 绑定为空（<see cref="AdoProject"/> 为空）表示这个项目没有接 ADO。
/// </summary>
public sealed record AdoBindingDto
{
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>ADO 项目名；空表示未绑定。</summary>
    public string AdoProject { get; init; } = string.Empty;

    /// <summary>只同步指派给这个人的工作项；空表示都同步，<c>@Me</c> 表示 PAT 对应的账号。</summary>
    public string? AssignedTo { get; init; }

    /// <summary>只同步这些迭代（分类节点完整路径）；空表示全部迭代。</summary>
    public IReadOnlyList<string> IterationPaths { get; init; } = [];

    /// <summary>跳过已关闭/已解决的历史工作项。</summary>
    public bool SkipClosed { get; init; }

    public DateTimeOffset? LastSyncAt { get; init; }

    public string? LastSyncMessage { get; init; }

    public bool LastSyncOk { get; init; }

    public bool Bound => !string.IsNullOrWhiteSpace(AdoProject);
}

/// <summary>保存项目级的 ADO 绑定。</summary>
public sealed record AdoBindingInput
{
    public string? AdoProject { get; init; }

    public string? AssignedTo { get; init; }

    public IReadOnlyList<string> IterationPaths { get; init; } = [];

    public bool SkipClosed { get; init; }
}

/// <summary>一次拉取的范围过滤。</summary>
public sealed record AdoFetchOptions
{
    /// <summary>指派人过滤；空=全部，<c>@Me</c>=当前 PAT 用户。</summary>
    public string? AssignedTo { get; init; }

    /// <summary>只拉这些迭代（分类节点路径）；空=全部。</summary>
    public IReadOnlyList<string> IterationPaths { get; init; } = [];

    public bool SkipClosed { get; init; }
}

/// <summary>可选的一个迭代（供设置页勾选同步范围）。</summary>
public sealed record AdoIterationRefDto
{
    public required string ExternalId { get; init; }

    /// <summary>分类节点完整路径，例如 <c>dynabookAssistant-Agent\迭代\3.0.1</c>。</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>层级深度，顶层 = 1（用于缩进展示）。</summary>
    public int Depth { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }
}

/// <summary>“获取迭代列表”的结果。</summary>
public sealed record AdoIterationsResultDto
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<AdoIterationRefDto> Iterations { get; init; } = [];
}

/// <summary>可选的一个指派人。</summary>
public sealed record AdoAssigneeDto
{
    public required string DisplayName { get; init; }

    /// <summary>工作项里出现的指派人数（多的排前面）。</summary>
    public int WorkItemCount { get; init; }
}

/// <summary>“获取指派人列表”的结果。</summary>
public sealed record AdoAssigneesResultDto
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<AdoAssigneeDto> Assignees { get; init; } = [];
    }

/// <summary>测试连接的结果。</summary>
public sealed record AdoConnectionTestDto
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>服务端认证到的用户；为空表示没取到。</summary>
    public string? AuthenticatedUser { get; init; }

    /// <summary>集合里可见的项目数（只测连接时有值）。</summary>
    public int ProjectCount { get; init; }

    public int WorkItemCount { get; init; }

    public int IterationCount { get; init; }
}

/// <summary>集合里的一个项目（用于让用户从列表里挑）。</summary>
public sealed record AdoProjectRefDto
{
    public required string ExternalId { get; init; }

    public required string Name { get; init; }
}

/// <summary>“获取项目列表”的结果。</summary>
public sealed record AdoProjectsResultDto
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<AdoProjectRefDto> Projects { get; init; } = [];
}

/// <summary>ADO 迭代节点（扁平化后的分类节点）。</summary>
public sealed class AdoIterationDto
{
    public required string ExternalId { get; init; }

    /// <summary>完整路径，例如 <c>\dynabookAssistant-Agent\迭代\3.0.1</c>。</summary>
    public required string Path { get; init; }

    public string? ParentPath { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }
}

/// <summary>ADO 工作项。描述已从 HTML 转成纯文本。</summary>
public sealed class AdoWorkItemDto
{
    public required string ExternalId { get; init; }

    public int Rev { get; init; }

    public required string Title { get; init; }

    public string? Notes { get; init; }

    public required string Type { get; init; }

    public required string State { get; init; }

    public int? AdoPriority { get; init; }

    public string? IterationPath { get; init; }

    public string? AssignedTo { get; init; }

    public string? Tags { get; init; }

    public string? ParentExternalId { get; init; }

    public DateTimeOffset? ChangedAt { get; init; }

    public required string Url { get; init; }
}

/// <summary>ADO 项目快照，由客户端拉取、由服务落库。</summary>
public sealed class AdoProjectSnapshot
{
    public required string ExternalId { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<AdoIterationDto> Iterations { get; init; } = [];

    public IReadOnlyList<AdoWorkItemDto> WorkItems { get; init; } = [];
}

/// <summary>「拉取 ADO 项目」的输入：不要求本地已有项目，直接按范围建一个。</summary>
public sealed record AdoPullProjectInput
{
    /// <summary>云端项目名（<c>_apis/projects</c> 里的 name）。</summary>
    public string AdoProject { get; init; } = string.Empty;

    /// <summary>空 = 全部人，<c>@Me</c> = PAT 账号，或具体显示名。</summary>
    public string? AssignedTo { get; init; }

    /// <summary>勾选的迭代路径；空 = 全部迭代（自动带子节点与祖先）。</summary>
    public IReadOnlyList<string> IterationPaths { get; init; } = [];

    public bool SkipClosed { get; init; }
}

/// <summary>一次同步的统计。部分条目失败不会中断整体，失败原因进 warnings。</summary>
public sealed record AdoSyncResultDto
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ProjectId { get; set; }

    /// <summary>本地项目第一次补上 ADO 标识（外链/徽标随之出现）。</summary>
    public bool ProjectLinked { get; set; }

    /// <summary>本次拉取新建了本地项目（而不是绑定已有项目）。</summary>
    public bool ProjectCreated { get; set; }

    public int IterationsCreated { get; set; }

    public int IterationsUpdated { get; set; }

    public int TodosCreated { get; set; }

    public int TodosUpdated { get; set; }

    public int TodosSkipped { get; set; }

    public DateTimeOffset SyncedAt { get; set; }

    public IReadOnlyList<string> Warnings { get; set; } = [];
}

/// <summary>单条工作项的同步结果：推送（事序 → ADO）或拉取（ADO → 事序）。</summary>
public sealed record AdoWorkItemSyncResultDto
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? TodoId { get; init; }

    public string? ExternalId { get; init; }

    public int? ExternalRev { get; init; }

    public string? ExternalUrl { get; init; }

    /// <summary>这次真正写到 ADO 的字段（标题 / 说明 / 状态 / 优先级）。</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];
}
