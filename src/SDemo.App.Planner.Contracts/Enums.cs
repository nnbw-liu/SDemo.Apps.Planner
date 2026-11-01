using System.Text.Json.Serialization;

namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 待办状态。日历上的 event 没有状态位——「能勾完成」是 todo 与 event 的根本区别之一。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TodoStatus>))]
public enum TodoStatus
{
    [JsonStringEnumMemberName("open")]
    Open = 0,

    [JsonStringEnumMemberName("doing")]
    Doing = 1,

    [JsonStringEnumMemberName("done")]
    Done = 2,
}

/// <summary>条目的来源。MCP 侧不接受自称 manual，避免 AI 条目伪装成用户手动录入。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TodoSource>))]
public enum TodoSource
{
    [JsonStringEnumMemberName("manual")]
    Manual = 0,

    [JsonStringEnumMemberName("ai")]
    Ai = 1,

    [JsonStringEnumMemberName("meeting")]
    Meeting = 2,

    [JsonStringEnumMemberName("import")]
    Import = 3,

    /// <summary>外部 ICS 只读镜像（P5）。v1 只在 schema 里保留取值。</summary>
    [JsonStringEnumMemberName("ics")]
    Ics = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<DraftStatus>))]
public enum DraftStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("accepted")]
    Accepted = 1,

    [JsonStringEnumMemberName("rejected")]
    Rejected = 2,
}

/// <summary>MCP 工具风险等级（见 planner-mcp-surface §6）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    [JsonStringEnumMemberName("low")]
    Low = 0,

    [JsonStringEnumMemberName("medium")]
    Medium = 1,

    [JsonStringEnumMemberName("high")]
    High = 2,
}

/// <summary>审批策略：never / always / threshold（按条数）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalMode>))]
public enum ApprovalMode
{
    [JsonStringEnumMemberName("never")]
    Never = 0,

    [JsonStringEnumMemberName("always")]
    Always = 1,

    [JsonStringEnumMemberName("threshold")]
    Threshold = 2,
}

/// <summary>写操作落审计时使用的实体类型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind
{
    [JsonStringEnumMemberName("todo")]
    Todo = 0,

    [JsonStringEnumMemberName("event")]
    Event = 1,

    [JsonStringEnumMemberName("draft")]
    Draft = 2,

    [JsonStringEnumMemberName("list")]
    List = 3,
}
