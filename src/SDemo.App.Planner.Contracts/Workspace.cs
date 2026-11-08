using System.Text.Json.Serialization;

namespace SDemo.App.Planner.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// 项目管理（v2）：项目 → 大迭代 → 小迭代 → 任务/Bug/调研
// 任务仍是 todo（可完成）；迭代是可嵌套的容器，不是万能实体。
// ─────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter<ProjectStatus>))]
public enum ProjectStatus
{
    [JsonStringEnumMemberName("active")]
    Active = 0,

    [JsonStringEnumMemberName("paused")]
    Paused = 1,

    [JsonStringEnumMemberName("done")]
    Done = 2,

    [JsonStringEnumMemberName("archived")]
    Archived = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<IterationLevel>))]
public enum IterationLevel
{
    /// <summary>大迭代：通常对应一个版本 / 里程碑。</summary>
    [JsonStringEnumMemberName("big")]
    Big = 0,

    /// <summary>小迭代：通常对应一个 sprint / 两周计划。</summary>
    [JsonStringEnumMemberName("small")]
    Small = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<IterationStatus>))]
public enum IterationStatus
{
    [JsonStringEnumMemberName("planned")]
    Planned = 0,

    [JsonStringEnumMemberName("active")]
    Active = 1,

    [JsonStringEnumMemberName("done")]
    Done = 2,

    [JsonStringEnumMemberName("archived")]
    Archived = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkItemKind>))]
public enum WorkItemKind
{
    [JsonStringEnumMemberName("task")]
    Task = 0,

    [JsonStringEnumMemberName("bug")]
    Bug = 1,

    [JsonStringEnumMemberName("research")]
    Research = 2,

    [JsonStringEnumMemberName("chore")]
    Chore = 3,
}

public sealed record ProjectDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string Color { get; init; } = "#4C8DFF";

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? TargetAt { get; init; }

    public int Sort { get; init; }

    public int Revision { get; init; }

    /// <summary>大迭代 + 小迭代，扁平返回；用 ParentId 组树。</summary>
    public IReadOnlyList<IterationDto> Iterations { get; init; } = [];

    public int TotalTasks { get; init; }

    public int DoneTasks { get; init; }

    public int OpenTasks { get; init; }

    public int OverdueTasks { get; init; }

    /// <summary>0–100，按任务数计算；无任务时为 0。</summary>
    public double Progress { get; init; }

    public DateTimeOffset? NextDueAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record IterationDto
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public string? ProjectColor { get; init; }

    /// <summary>大迭代下的小迭代有 ParentId；大迭代本身为 null。</summary>
    public string? ParentId { get; init; }

    public string? ParentName { get; init; }

    public IterationLevel Level { get; init; }

    public required string Name { get; init; }

    public string? Goal { get; init; }

    public IterationStatus Status { get; init; } = IterationStatus.Planned;

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>迭代完成时的收口说明。</summary>
    public string? CompletionNote { get; init; }

    public int Sort { get; init; }

    public int Revision { get; init; }

    public int TotalTasks { get; init; }

    public int DoneTasks { get; init; }

    public int OpenTasks { get; init; }

    public double Progress { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>日常条目：每天要做的事，也是提醒的来源。timeOfDay/activeEnd 是本地 HH:mm。</summary>
public sealed record RoutineDto
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Emoji { get; init; } = "✅";

    public string? Notes { get; init; }

    /// <summary>bit0=周一 … bit6=周日；127 表示每天。</summary>
    public int DaysMask { get; init; } = 127;

    /// <summary>固定提醒时间（本地 HH:mm）；间隔提醒时作为开始时间。</summary>
    public string? TimeOfDay { get; init; }

    /// <summary>大于 0 时表示按间隔提醒；null 表示只在 timeOfDay 提醒一次。</summary>
    public int? IntervalMinutes { get; init; }

    /// <summary>间隔提醒的结束时间（本地 HH:mm）。</summary>
    public string? ActiveEnd { get; init; }

    /// <summary>仅一次提醒的时刻（UTC）；非空时忽略 daysMask/interval/activeEnd，触发后自动停用。</summary>
    public DateTimeOffset? OnceAt { get; init; }

    /// <summary>是否为「仅一次」提醒。</summary>
    public bool IsOneTime => OnceAt is not null;

    /// <summary>每天目标次数；吃药/喝水通常为 1/8。</summary>
    public int TargetCount { get; init; } = 1;

    public bool Remind { get; init; } = true;

    public bool Enabled { get; init; } = true;

    public int Sort { get; init; }

    public int Revision { get; init; }

    /// <summary>指定日期的完成次数（由服务按日期填充）。</summary>
    public int TodayCount { get; init; }

    public bool DoneToday => TodayCount >= Math.Max(1, TargetCount);

    public double Progress => Math.Clamp((double)TodayCount / Math.Max(1, TargetCount), 0, 1);

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RoutineDayDto
{
    public required string Date { get; init; }

    public int Total { get; init; }

    public int Done { get; init; }

    public int Count { get; init; }
}

/// <summary>首页一次取全，减少 Blazor 首屏的往返与闪烁。</summary>
public sealed record HomeSnapshotDto
{
    public required string Today { get; init; }

    public IReadOnlyList<RoutineDto> Routines { get; init; } = [];

    public IReadOnlyList<TodoDto> DueToday { get; init; } = [];

    public IReadOnlyList<TodoDto> Overdue { get; init; } = [];

    public IReadOnlyList<TodoDto> Upcoming { get; init; } = [];

    public IReadOnlyList<EventDto> Events { get; init; } = [];

    public IReadOnlyList<IterationDto> ActiveIterations { get; init; } = [];

    public IReadOnlyList<ProjectDto> Projects { get; init; } = [];

    public StatsDto Stats { get; init; } = new();

    public int RoutineTotal { get; init; }

    public int RoutineDone { get; init; }
}

/// <summary>一次提醒的载荷。调度器只负责判断「该提醒了」，UI 决定怎么显示。</summary>
public sealed record RoutineReminderDto
{
    public required string RoutineId { get; init; }

    public required string Title { get; init; }

    public string Emoji { get; init; } = "⏰";

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset ScheduledFor { get; init; }

    public bool IsInterval { get; init; }
}

public static class RoutineDays
{
    /// <summary>周一 = bit0。与 DateOnly.DayOfWeek 转换：Sunday(0) 对应 bit6。</summary>
    public static int BitFor(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    public static bool Includes(int mask, DayOfWeek day) => (mask & (1 << BitFor(day))) != 0;

    public static int With(int mask, DayOfWeek day, bool selected) =>
        selected ? mask | (1 << BitFor(day)) : mask & ~(1 << BitFor(day));

    public static string Label(int mask)
    {
        if (mask is 0 or 127)
        {
            return mask == 0 ? "未选择" : "每天";
        }

        if (mask == 0b0011111)
        {
            return "工作日";
        }

        if (mask == 0b1100000)
        {
            return "周末";
        }

        var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
        var days = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => names[i]);
        return "周" + string.Join("、", days);
    }
}

public sealed record ProjectInput
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Color { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? TargetAt { get; init; }
}

public sealed record ProjectPatch
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool ClearDescription { get; init; }

    public string? Color { get; init; }

    public ProjectStatus? Status { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public bool ClearStartAt { get; init; }

    public DateTimeOffset? TargetAt { get; init; }

    public bool ClearTargetAt { get; init; }

    public int? Sort { get; init; }

    public bool IsNoOp => Name is null && Description is null && !ClearDescription && Color is null
        && Status is null && StartAt is null && !ClearStartAt && TargetAt is null && !ClearTargetAt
        && Sort is null;
}

public sealed record IterationInput
{
    public required string ProjectId { get; init; }

    public string? ParentId { get; init; }

    public IterationLevel Level { get; init; } = IterationLevel.Big;

    public required string Name { get; init; }

    public string? Goal { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }
}

public sealed record IterationPatch
{
    public string? Name { get; init; }

    public string? Goal { get; init; }

    public bool ClearGoal { get; init; }

    public IterationStatus? Status { get; init; }

    /// <summary>迭代完成说明；完成/重开由专用方法维护 CompletedAt。</summary>
    public string? CompletionNote { get; init; }

    public bool ClearCompletionNote { get; init; }

    public DateTimeOffset? StartAt { get; init; }

    public bool ClearStartAt { get; init; }

    public DateTimeOffset? EndAt { get; init; }

    public bool ClearEndAt { get; init; }

    public int? Sort { get; init; }

    public bool IsNoOp => Name is null && Goal is null && !ClearGoal
        && Status is null && CompletionNote is null && !ClearCompletionNote
        && StartAt is null && !ClearStartAt && EndAt is null && !ClearEndAt && Sort is null;
}

/// <summary>
/// 中国法定节假日 / 调休补班。Date 是本地日历日，不是 UTC 时刻。
/// Source 为 manual 时，联网同步不会覆盖；remote 表示来自公开节假日接口。
/// </summary>
public sealed record HolidayDto
{
    public required DateOnly Date { get; init; }

    public required string Name { get; init; }

    /// <summary>true = 调休上班（班）；false = 放假（休）。</summary>
    public bool IsWorkday { get; init; }

    public string Source { get; init; } = "manual";

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record HolidayInput
{
    public required DateOnly Date { get; init; }

    public required string Name { get; init; }

    /// <summary>true = 调休上班（班）；false = 放假（休）。</summary>
    public bool IsWorkday { get; init; }
}

public sealed record RoutineInput
{
    public required string Title { get; init; }

    public string? Emoji { get; init; }

    public string? Notes { get; init; }

    public int DaysMask { get; init; } = 127;

    public string? TimeOfDay { get; init; }

    public int? IntervalMinutes { get; init; }

    public string? ActiveEnd { get; init; }

    /// <summary>仅一次提醒的时刻；有值时忽略重复日与间隔设置。</summary>
    public DateTimeOffset? OnceAt { get; init; }

    public int TargetCount { get; init; } = 1;

    public bool Remind { get; init; } = true;
}

public sealed record RoutinePatch
{
    public string? Title { get; init; }

    public string? Emoji { get; init; }

    public string? Notes { get; init; }

    public bool ClearNotes { get; init; }

    public int? DaysMask { get; init; }

    public string? TimeOfDay { get; init; }

    public bool ClearTimeOfDay { get; init; }

    public int? IntervalMinutes { get; init; }

    public bool ClearInterval { get; init; }

    public string? ActiveEnd { get; init; }

    public bool ClearActiveEnd { get; init; }

    /// <summary>设置为「仅一次」提醒的时刻；会自动清空 interval/activeEnd。</summary>
    public DateTimeOffset? OnceAt { get; init; }

    /// <summary>取消「仅一次」提醒，回到按重复日提醒。</summary>
    public bool ClearOnceAt { get; init; }

    public int? TargetCount { get; init; }

    public bool? Remind { get; init; }

    public bool? Enabled { get; init; }

    public int? Sort { get; init; }

    public bool IsNoOp => Title is null && Emoji is null && Notes is null && !ClearNotes
        && DaysMask is null && TimeOfDay is null && !ClearTimeOfDay && IntervalMinutes is null
        && !ClearInterval && ActiveEnd is null && !ClearActiveEnd && OnceAt is null && !ClearOnceAt
        && TargetCount is null && Remind is null && Enabled is null && Sort is null;
}
