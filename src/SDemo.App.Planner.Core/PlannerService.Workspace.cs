using System.Globalization;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// v2 工作区门面：项目 / 大迭代 / 小迭代 / 日常 / 首页快照 / 提醒判定。
/// 仍是零模型凭证，所有写入走 PlannerStore 的事务 + outbox/op_log。
/// </summary>
public sealed partial class PlannerService
{
    private const string DefaultProjectColor = "#4C8DFF";
    private const string DefaultRoutineEmoji = "✅";

    /// <summary>固定时间提醒的补发窗口：应用晚开半小时内仍提醒，再晚就跳过，避免晚上弹出早上的吃药提醒。</summary>
    private static readonly TimeSpan ReminderGrace = TimeSpan.FromMinutes(30);

    // ── 项目 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ProjectDto> ListProjects(bool includeArchived = false)
    {
        var projects = _store.QueryProjects();
        if (!includeArchived)
        {
            projects = projects.Where(p => p.Status != "archived").ToList();
        }

        var allIterations = _store.QueryIterations();
        var allTodos = _store.QueryProjectTodos();
        var groups = LoadProjectGroups();
        return projects
            .Select(p => BuildProject(p, allIterations, allTodos, groups.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public ProjectDto? GetProject(string id)
    {
        var row = _store.GetProject(id);
        if (row is null)
        {
            return null;
        }

        return BuildProject(row, _store.QueryIterations(id), _store.QueryProjectTodos(), GroupsOf(id));
    }

    public ProjectDto CreateProject(ProjectInput input, WriteContext context)
    {
        var now = NowUtc();
        var groupIds = NormalizeGroupIds(input.GroupIds);
        var row = new ProjectRow
        {
            Id = NewId("pj"),
            Name = RequireTitle(input.Name),
            Description = NormalizeNotes(input.Description),
            Color = NormalizeColor(input.Color),
            Status = "active",
            StartAt = PlannerMapper.ToUtc(input.StartAt),
            TargetAt = PlannerMapper.ToUtc(input.TargetAt),
            Sort = _store.QueryProjects().Count,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ValidateProjectDates(row.StartAt, row.TargetAt);
        _store.WriteProject(new ProjectWrite
        {
            Row = row,
            Op = "create",
            ExpectedRevision = 0,
            GroupIds = groupIds,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return PlannerMapper.ToProject(row, [], GroupsByIds(groupIds));
    }

    public ProjectDto UpdateProject(string id, int expectedRevision, ProjectPatch patch, WriteContext context)
    {
        var row = _store.GetProject(id) ?? throw PlannerException.NotFound($"没有找到项目「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var updated = CloneProject(row);
        if (patch.Name is not null)
        {
            updated.Name = RequireTitle(patch.Name);
        }

        if (patch.ClearDescription)
        {
            updated.Description = null;
        }
        else if (patch.Description is not null)
        {
            updated.Description = NormalizeNotes(patch.Description);
        }

        if (patch.Color is not null)
        {
            updated.Color = NormalizeColor(patch.Color);
        }

        if (patch.Status is { } status)
        {
            updated.Status = PlannerMapper.ToStorage(status);
        }

        if (patch.ClearStartAt)
        {
            updated.StartAt = null;
        }
        else if (patch.StartAt is { } startAt)
        {
            updated.StartAt = startAt.ToUniversalTime().UtcDateTime;
        }

        if (patch.ClearTargetAt)
        {
            updated.TargetAt = null;
        }
        else if (patch.TargetAt is { } targetAt)
        {
            updated.TargetAt = targetAt.ToUniversalTime().UtcDateTime;
        }

        if (patch.Sort is { } sort)
        {
            updated.Sort = sort;
        }

        ValidateProjectDates(updated.StartAt, updated.TargetAt);
        updated.Revision = row.Revision + 1;
        updated.UpdatedAt = NowUtc();
        var groupIds = patch.GroupIds is null ? null : NormalizeGroupIds(patch.GroupIds);

        _store.WriteProject(new ProjectWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            GroupIds = groupIds,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return BuildProject(
            updated,
            _store.QueryIterations(id),
            _store.QueryProjectTodos(),
            groupIds is null ? GroupsOf(id) : GroupsByIds(groupIds));
    }

    public void DeleteProject(string id, int expectedRevision, bool deleteTasks = false)
    {
        var row = _store.GetProject(id) ?? throw PlannerException.NotFound($"没有找到项目「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (deleteTasks)
        {
            foreach (var todo in _store.QueryProjectTodos().Where(t => t.ProjectId == id))
            {
                DeleteTodo(todo.Id, todo.Revision, $"随项目「{row.Name}」删除", WriteContext.Manual());
            }
        }

        _store.DeleteProject(id, NowUtc());
        RaiseChanged();
    }

    // ── 项目分组 ──────────────────────────────────────────────────────────

    public IReadOnlyList<ProjectGroupDto> ListProjectGroups()
    {
        var rows = _store.QueryProjectGroups();
        var members = _store.QueryProjectGroupMembers();
        var projects = _store.QueryProjects().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return rows
            .Select(g => PlannerMapper.ToProjectGroup(
                g,
                members.Count(m => m.ProjectGroupId == g.Id && projects.Contains(m.ProjectId))))
            .ToList();
    }

    public ProjectGroupDto CreateProjectGroup(ProjectGroupInput input, WriteContext context)
    {
        var now = NowUtc();
        var row = new ProjectGroupRow
        {
            Id = NewId("pg"),
            Name = RequireTitle(input.Name),
            Color = NormalizeGroupColor(input.Color),
            Sort = _store.QueryProjectGroups().Count,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _store.WriteProjectGroup(new ProjectGroupWrite
        {
            Row = row,
            Op = "create",
            ExpectedRevision = 0,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return PlannerMapper.ToProjectGroup(row);
    }

    public ProjectGroupDto UpdateProjectGroup(string id, int expectedRevision, ProjectGroupPatch patch, WriteContext context)
    {
        var row = _store.GetProjectGroup(id)
            ?? throw PlannerException.NotFound($"没有找到分组「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var updated = new ProjectGroupRow
        {
            Id = row.Id,
            Name = row.Name,
            Color = row.Color,
            Sort = row.Sort,
            Revision = row.Revision + 1,
            CreatedAt = row.CreatedAt,
            UpdatedAt = NowUtc(),
            DeletedAt = row.DeletedAt,
        };

        if (patch.Name is not null)
        {
            updated.Name = RequireTitle(patch.Name);
        }

        if (patch.Color is not null)
        {
            updated.Color = NormalizeGroupColor(patch.Color);
        }

        if (patch.Sort is { } sort)
        {
            updated.Sort = sort;
        }

        _store.WriteProjectGroup(new ProjectGroupWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        var projectCount = _store.QueryProjectGroupMembers().Count(m => m.ProjectGroupId == id);
        return PlannerMapper.ToProjectGroup(updated, projectCount);
    }

    /// <summary>删除分组只断开成员关系，项目本身保留。</summary>
    public void DeleteProjectGroup(string id, int expectedRevision)
    {
        var row = _store.GetProjectGroup(id)
            ?? throw PlannerException.NotFound($"没有找到分组「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        _store.DeleteProjectGroup(id, NowUtc());
        RaiseChanged();
    }

    // ── 迭代 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<IterationDto> ListIterations(string? projectId = null)
    {
        var projects = _store.QueryProjects(includeDeleted: true).ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        var rows = _store.QueryIterations(projectId);
        var byId = rows.ToDictionary(i => i.Id, i => i, StringComparer.Ordinal);
        var todos = _store.QueryProjectTodos();
        return rows
            .Select(i => BuildIteration(
                i,
                projects.GetValueOrDefault(i.ProjectId),
                i.ParentId is null ? null : byId.GetValueOrDefault(i.ParentId),
                rows,
                todos))
            .ToList();
    }

    public IterationDto CreateIteration(IterationInput input, WriteContext context)
    {
        var project = _store.GetProject(input.ProjectId)
            ?? throw PlannerException.Validation($"没有找到项目「{input.ProjectId}」");

        IterationRow? parent = null;
        if (input.ParentId is { Length: > 0 } parentId)
        {
            parent = _store.GetIteration(parentId)
                ?? throw PlannerException.Validation($"没有找到大迭代「{parentId}」");
            if (parent.ProjectId != project.Id)
            {
                throw PlannerException.Validation("小迭代必须和大迭代在同一个项目里");
            }

            if (parent.ParentId is not null)
            {
                throw PlannerException.Validation("迭代最多两层：大迭代 → 小迭代");
            }

            if (input.Level != IterationLevel.Small)
            {
                throw PlannerException.Validation("挂在大迭代下的只能是「小迭代」");
            }
        }
        else if (input.Level == IterationLevel.Small)
        {
            throw PlannerException.Validation("小迭代必须指定所属大迭代");
        }

        var now = NowUtc();
        var row = new IterationRow
        {
            Id = NewId("it"),
            ProjectId = project.Id,
            ParentId = parent?.Id,
            Level = PlannerMapper.ToStorage(input.Level),
            Name = RequireTitle(input.Name),
            Goal = NormalizeNotes(input.Goal),
            Status = "planned",
            StartAt = PlannerMapper.ToUtc(input.StartAt),
            EndAt = PlannerMapper.ToUtc(input.EndAt),
            Sort = _store.QueryIterations(project.Id).Count,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ValidateIterationDates(row.StartAt, row.EndAt);
        _store.WriteIteration(new IterationWrite
        {
            Row = row,
            Op = "create",
            ExpectedRevision = 0,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return PlannerMapper.ToIteration(row, project.Name, project.Color, parent?.Name);
    }

    public IterationDto UpdateIteration(string id, int expectedRevision, IterationPatch patch, WriteContext context)
    {
        var row = _store.GetIteration(id) ?? throw PlannerException.NotFound($"没有找到迭代「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var updated = CloneIteration(row);
        if (patch.Name is not null)
        {
            updated.Name = RequireTitle(patch.Name);
        }

        if (patch.ClearGoal)
        {
            updated.Goal = null;
        }
        else if (patch.Goal is not null)
        {
            updated.Goal = NormalizeNotes(patch.Goal);
        }

        if (patch.Status is { } status)
        {
            updated.Status = PlannerMapper.ToStorage(status);
            updated.CompletedAt = updated.Status == "done"
                ? updated.CompletedAt ?? NowUtc()
                : null;
        }

        if (patch.ClearCompletionNote)
        {
            updated.CompletionNote = null;
        }
        else if (patch.CompletionNote is not null)
        {
            updated.CompletionNote = NormalizeNotes(patch.CompletionNote);
        }

        if (patch.ClearStartAt)
        {
            updated.StartAt = null;
        }
        else if (patch.StartAt is { } startAt)
        {
            updated.StartAt = startAt.ToUniversalTime().UtcDateTime;
        }

        if (patch.ClearEndAt)
        {
            updated.EndAt = null;
        }
        else if (patch.EndAt is { } endAt)
        {
            updated.EndAt = endAt.ToUniversalTime().UtcDateTime;
        }

        if (patch.Sort is { } sort)
        {
            updated.Sort = sort;
        }

        ValidateIterationDates(updated.StartAt, updated.EndAt);
        updated.Revision = row.Revision + 1;
        updated.UpdatedAt = NowUtc();

        _store.WriteIteration(new IterationWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();

        var project = _store.GetProject(updated.ProjectId);
        var parent = updated.ParentId is null ? null : _store.GetIteration(updated.ParentId);
        return BuildIteration(updated, project, parent, _store.QueryIterations(updated.ProjectId), _store.QueryProjectTodos());
    }

    /// <summary>
    /// 完成迭代。大迭代会连同其小迭代一起收口；已经完成时只补说明，不重复涨版本。
    /// </summary>
    public IterationDto CompleteIteration(string id, int expectedRevision, string? completionNote, WriteContext context)
    {
        var row = _store.GetIteration(id) ?? throw PlannerException.NotFound($"没有找到迭代「{id}」");
        var note = NormalizeNotes(completionNote);
        var noteChanged = !string.Equals(row.CompletionNote, note, StringComparison.Ordinal);
        if (row.Status == "done" && !noteChanged)
        {
            return BuildIteration(row, _store.GetProject(row.ProjectId),
                row.ParentId is null ? null : _store.GetIteration(row.ParentId),
                _store.QueryIterations(row.ProjectId), _store.QueryProjectTodos());
        }

        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var targets = new List<IterationRow> { row };
        if (row.ParentId is null)
        {
            targets.AddRange(_store.QueryIterations(row.ProjectId).Where(i => i.ParentId == row.Id));
        }

        IterationRow? updatedRow = null;
        foreach (var target in targets)
        {
            var updated = CloneIteration(target);
            updated.Status = "done";
            updated.CompletedAt ??= now;
            if (target.Id == row.Id)
            {
                updated.CompletionNote = note;
                updatedRow = updated;
            }

            updated.Revision = target.Revision + 1;
            updated.UpdatedAt = now;
            _store.WriteIteration(new IterationWrite
            {
                Row = updated,
                Op = "complete",
                ExpectedRevision = target.Revision,
                Actor = ActorOf(context.Source),
                RequestId = context.RequestId,
                Reason = "完成迭代",
            });
        }

        RaiseChanged();
        var result = updatedRow ?? row;
        return BuildIteration(result, _store.GetProject(result.ProjectId),
            result.ParentId is null ? null : _store.GetIteration(result.ParentId),
            _store.QueryIterations(result.ProjectId), _store.QueryProjectTodos());
    }

    public IterationDto ReopenIteration(string id, int expectedRevision, WriteContext context)
    {
        var row = _store.GetIteration(id) ?? throw PlannerException.NotFound($"没有找到迭代「{id}」");
        if (row.Status != "done")
        {
            return BuildIteration(row, _store.GetProject(row.ProjectId),
                row.ParentId is null ? null : _store.GetIteration(row.ParentId),
                _store.QueryIterations(row.ProjectId), _store.QueryProjectTodos());
        }

        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        var updated = CloneIteration(row);
        updated.Status = "active";
        updated.CompletedAt = null;
        updated.Revision = row.Revision + 1;
        updated.UpdatedAt = now;
        _store.WriteIteration(new IterationWrite
        {
            Row = updated,
            Op = "reopen",
            ExpectedRevision = expectedRevision,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
            Reason = "重开迭代",
        });
        RaiseChanged();
        return BuildIteration(updated, _store.GetProject(updated.ProjectId),
            updated.ParentId is null ? null : _store.GetIteration(updated.ParentId),
            _store.QueryIterations(updated.ProjectId), _store.QueryProjectTodos());
    }

    public void DeleteIteration(string id, int expectedRevision, bool deleteTasks = false)
    {
        var row = _store.GetIteration(id) ?? throw PlannerException.NotFound($"没有找到迭代「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (deleteTasks)
        {
            var iterationIds = DescendantIterationIds(row.ProjectId, id);
            foreach (var todo in _store.QueryProjectTodos()
                         .Where(t => t.IterationId is not null && iterationIds.Contains(t.IterationId!)))
            {
                DeleteTodo(todo.Id, todo.Revision, $"随迭代「{row.Name}」删除", WriteContext.Manual());
            }
        }

        _store.DeleteIteration(id, NowUtc());
        RaiseChanged();
    }

    /// <summary>某迭代自身 + 所有层级的子迭代 id（删除迭代时任务跟着删的判定范围）。</summary>
    private HashSet<string> DescendantIterationIds(string? projectId, string rootId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { rootId };
        if (projectId is null)
        {
            return ids;
        }

        var iterations = _store.QueryIterations(projectId);
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var iteration in iterations)
            {
                if (iteration.ParentId is { } parent && ids.Contains(parent) && ids.Add(iteration.Id))
                {
                    grew = true;
                }
            }
        }

        return ids;
    }

    // ── 日常 ──────────────────────────────────────────────────────────────

    public IReadOnlyList<RoutineDto> ListRoutines(DateOnly? date = null)
    {
        var day = date ?? TodayLocal();
        var counts = _store.GetRoutineChecks(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return _store.QueryRoutines()
            .Where(r => r.Enabled)
            .Where(r => r.OnceAt is { } onceAt
                ? LocalDateOf(onceAt) == day
                : RoutineDays.Includes(r.DaysMask, day.DayOfWeek))
            .Select(r => PlannerMapper.ToRoutine(r, counts.GetValueOrDefault(r.Id)?.Count ?? 0))
            .ToList();
    }

    public IReadOnlyList<RoutineDto> ListAllRoutines()
    {
        var today = TodayLocal().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var counts = _store.GetRoutineChecks(today);
        return _store.QueryRoutines()
            .Select(r => PlannerMapper.ToRoutine(r, counts.GetValueOrDefault(r.Id)?.Count ?? 0))
            .ToList();
    }

    public RoutineDto CreateRoutine(RoutineInput input, WriteContext context)
    {
        var now = NowUtc();
        var normalized = NormalizeRoutine(
            input.Title,
            input.Emoji,
            input.Notes,
            input.DaysMask,
            input.TimeOfDay,
            input.IntervalMinutes,
            input.ActiveEnd,
            input.TargetCount,
            input.OnceAt);

        if (input.Remind && normalized.OnceAt is { } onceAt && onceAt <= now)
        {
            throw PlannerException.Validation("仅一次提醒要晚于现在。");
        }

        var row = new RoutineRow
        {
            Id = NewId("rt"),
            Title = normalized.Title,
            Emoji = normalized.Emoji,
            Notes = normalized.Notes,
            DaysMask = normalized.DaysMask,
            TimeOfDay = normalized.TimeOfDay,
            IntervalMinutes = normalized.IntervalMinutes,
            ActiveEnd = normalized.ActiveEnd,
            OnceAt = normalized.OnceAt,
            TargetCount = normalized.TargetCount,
            Remind = input.Remind,
            Enabled = true,
            Sort = _store.QueryRoutines().Count,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _store.WriteRoutine(new RoutineWrite
        {
            Row = row,
            Op = "create",
            ExpectedRevision = 0,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return PlannerMapper.ToRoutine(row);
    }

    public RoutineDto UpdateRoutine(string id, int expectedRevision, RoutinePatch patch, WriteContext context)
    {
        var row = _store.GetRoutine(id) ?? throw PlannerException.NotFound($"没有找到日常「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        if (patch.IsNoOp)
        {
            throw PlannerException.Validation("patch 为空，没有任何要修改的字段");
        }

        var title = patch.Title ?? row.Title;
        var emoji = patch.Emoji ?? row.Emoji;
        var notes = patch.ClearNotes ? null : patch.Notes ?? row.Notes;
        var daysMask = patch.DaysMask ?? row.DaysMask;
        var timeOfDay = patch.ClearTimeOfDay ? null : patch.TimeOfDay ?? row.TimeOfDay;
        var interval = patch.ClearInterval ? null : patch.IntervalMinutes ?? row.IntervalMinutes;
        var activeEnd = patch.ClearActiveEnd ? null : patch.ActiveEnd ?? row.ActiveEnd;
        var onceAt = patch.ClearOnceAt ? null : patch.OnceAt ?? row.OnceAt;
        var target = patch.TargetCount ?? row.TargetCount;

        var normalized = NormalizeRoutine(title, emoji, notes, daysMask, timeOfDay, interval, activeEnd, target, onceAt);
        var enabled = patch.Enabled ?? row.Enabled;
        var remind = patch.Remind ?? row.Remind;
        var now = NowUtc();
        if (enabled && remind && normalized.OnceAt is { } onceUtc && onceUtc <= now)
        {
            throw PlannerException.Validation("仅一次提醒要晚于现在。");
        }

        var updated = new RoutineRow
        {
            Id = row.Id,
            Title = normalized.Title,
            Emoji = normalized.Emoji,
            Notes = normalized.Notes,
            DaysMask = normalized.DaysMask,
            TimeOfDay = normalized.TimeOfDay,
            IntervalMinutes = normalized.IntervalMinutes,
            ActiveEnd = normalized.ActiveEnd,
            OnceAt = normalized.OnceAt,
            TargetCount = normalized.TargetCount,
            Remind = remind,
            Enabled = enabled,
            Sort = patch.Sort ?? row.Sort,
            Revision = row.Revision + 1,
            LastFiredAt = row.LastFiredAt,
            CreatedAt = row.CreatedAt,
            UpdatedAt = now,
        };

        _store.WriteRoutine(new RoutineWrite
        {
            Row = updated,
            Op = "update",
            ExpectedRevision = expectedRevision,
            Actor = ActorOf(context.Source),
            RequestId = context.RequestId,
        });
        RaiseChanged();
        return PlannerMapper.ToRoutine(updated);
    }

    public void DeleteRoutine(string id, int expectedRevision)
    {
        var row = _store.GetRoutine(id) ?? throw PlannerException.NotFound($"没有找到日常「{id}」");
        if (row.Revision != expectedRevision)
        {
            throw PlannerException.Conflict(row.Revision);
        }

        var now = NowUtc();
        row.DeletedAt = now;
        row.UpdatedAt = now;
        row.Revision++;
        _store.WriteRoutine(new RoutineWrite
        {
            Row = row,
            Op = "delete",
            ExpectedRevision = expectedRevision,
            Reason = "日常删除",
        });
        RaiseChanged();
    }

    /// <summary>打卡 +1 / -1，落后端上限 targetCount。</summary>
    public RoutineDto CheckRoutine(string id, int delta = 1)
    {
        var row = _store.GetRoutine(id) ?? throw PlannerException.NotFound($"没有找到日常「{id}」");
        var today = TodayLocal();
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var count = _store.AdjustRoutineCheck(id, key, Math.Clamp(delta, -20, 20), NowUtc());
        RaiseChanged();
        return PlannerMapper.ToRoutine(row, count);
    }

    public IReadOnlyList<RoutineDayDto> RoutineWeek(DateOnly? endDate = null, int days = 7)
    {
        days = Math.Clamp(days, 1, 31);
        var end = endDate ?? TodayLocal();
        var start = end.AddDays(-(days - 1));
        var routines = _store.QueryRoutines().Where(r => r.Enabled && r.OnceAt == null).ToList();
        var checks = _store.QueryRoutineChecks(
                start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .GroupBy(c => (c.RoutineId, c.LocalDate))
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<RoutineDayDto>(days);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var scheduled = routines.Where(r => RoutineDays.Includes(r.DaysMask, date.DayOfWeek)).ToList();
            var done = 0;
            var count = 0;
            foreach (var routine in scheduled)
            {
                var row = checks.GetValueOrDefault((routine.Id, key));
                count += row?.Count ?? 0;
                if ((row?.Count ?? 0) >= routine.TargetCount)
                {
                    done++;
                }
            }

            result.Add(new RoutineDayDto
            {
                Date = key,
                Total = scheduled.Count,
                Done = done,
                Count = count,
            });
        }

        return result;
    }

    // ── 首页 ──────────────────────────────────────────────────────────────

    public HomeSnapshotDto Home()
    {
        var localNow = _time.GetLocalNow();
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var startUtc = ToUtc(today, TimeOnly.MinValue);
        var endUtc = ToUtc(today.AddDays(1), TimeOnly.MinValue);
        var nowUtc = NowUtc();

        var routines = ListRoutines(today);
        var dueToday = MapTodos(_store.QueryTodos(new TodoFilter
        {
            Status = "open",
            DueAfterUtc = startUtc,
            DueBeforeUtc = endUtc,
            Limit = 100,
        }));
        var overdue = MapTodos(_store.QueryTodos(new TodoFilter
        {
            Status = "open",
            DueBeforeUtc = startUtc,
            Limit = 100,
        }));
        var upcoming = MapTodos(_store.QueryTodos(new TodoFilter
        {
            Status = "open",
            DueAfterUtc = endUtc,
            DueBeforeUtc = ToUtc(today.AddDays(8), TimeOnly.MinValue),
            Limit = 100,
        }));
        var events = MapEvents(_store.QueryEvents(startUtc, endUtc));

        var projects = ListProjects();
        var iterations = ListIterations()
            .Where(i => i.Status is IterationStatus.Active
                        || (i.Status == IterationStatus.Planned && (i.EndAt is null || i.EndAt >= ToOffset(nowUtc))))
            .OrderBy(i => i.Level)
            .ThenBy(i => i.EndAt ?? DateTimeOffset.MaxValue)
            .ToList();

        return new HomeSnapshotDto
        {
            Today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Routines = routines,
            DueToday = dueToday,
            Overdue = overdue,
            Upcoming = upcoming,
            Events = events,
            ActiveIterations = iterations,
            Projects = projects,
            Stats = Stats(),
            RoutineTotal = routines.Count,
            RoutineDone = routines.Count(r => r.DoneToday),
        };
    }

    // ── 提醒 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 计算此刻应触发的提醒并（默认）标记已触发，避免下一个 tick 重复。
    /// 固定时间：当天到点后 30 分钟内且当天未触发；间隔：在时间窗内按 lastFiredAt + interval 轮询。
    /// </summary>
    public IReadOnlyList<RoutineReminderDto> CollectDueReminders(DateTimeOffset? now = null, bool markFired = true)
    {
        var local = now ?? _time.GetLocalNow();
        var today = DateOnly.FromDateTime(local.DateTime);
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var checks = _store.GetRoutineChecks(key);
        var result = new List<RoutineReminderDto>();

        foreach (var row in _store.QueryRoutines().Where(r => r.Enabled && r.Remind))
        {
            var count = checks.GetValueOrDefault(row.Id)?.Count ?? 0;
            if (count >= Math.Max(1, row.TargetCount))
            {
                continue;
            }

            // 仅一次：只在指定本地日期/时刻的宽限窗口内触发，触发后自动停用，避免每天重复。
            if (row.OnceAt is { } onceUtc)
            {
                var onceLocal = TimeZoneInfo.ConvertTime(
                    new DateTimeOffset(DateTime.SpecifyKind(onceUtc, DateTimeKind.Utc)),
                    _time.LocalTimeZone);
                if (DateOnly.FromDateTime(onceLocal.DateTime) != today)
                {
                    continue;
                }

                if (row.LastFiredAt is { } firedOnce && firedOnce >= onceUtc)
                {
                    continue;
                }

                if (local < onceLocal || local - onceLocal > ReminderGrace)
                {
                    continue;
                }

                result.Add(new RoutineReminderDto
                {
                    RoutineId = row.Id,
                    Title = row.Title,
                    Emoji = row.Emoji,
                    Message = "仅一次提醒",
                    ScheduledFor = onceLocal,
                    IsInterval = false,
                });

                if (markFired)
                {
                    _store.MarkRoutineFired(row.Id, local.UtcDateTime, disable: true);
                }

                continue;
            }

            if (!RoutineDays.Includes(row.DaysMask, today.DayOfWeek))
            {
                continue;
            }

            if (!TryParseClock(row.TimeOfDay, out var start))
            {
                continue;
            }

            var nowTime = TimeOnly.FromDateTime(local.DateTime);
            DateTimeOffset scheduled;
            var isInterval = row.IntervalMinutes is > 0;
            if (isInterval)
            {
                var end = TryParseClock(row.ActiveEnd, out var parsedEnd) ? parsedEnd : new TimeOnly(23, 59);
                if (nowTime < start || nowTime > end)
                {
                    continue;
                }

                var lastLocal = row.LastFiredAt is { } last
                    ? TimeZoneInfo.ConvertTimeFromUtc(last, _time.LocalTimeZone)
                    : (DateTime?)null;
                var from = lastLocal is { } fired && DateOnly.FromDateTime(fired) == today
                    ? TimeOnly.FromDateTime(fired)
                    : start;
                var next = from.AddMinutes(row.IntervalMinutes!.Value);
                if (lastLocal is null || DateOnly.FromDateTime(lastLocal.Value) != today)
                {
                    next = start;
                }

                if (nowTime < next)
                {
                    continue;
                }

                scheduled = new DateTimeOffset(today.ToDateTime(next), local.Offset);
            }
            else
            {
                if (nowTime < start)
                {
                    continue;
                }

                if (nowTime.ToTimeSpan() - start.ToTimeSpan() > ReminderGrace)
                {
                    continue;
                }

                var lastLocal = row.LastFiredAt is { } last
                    ? TimeZoneInfo.ConvertTimeFromUtc(last, _time.LocalTimeZone)
                    : (DateTime?)null;
                if (lastLocal is { } fired && DateOnly.FromDateTime(fired) == today)
                {
                    continue;
                }

                scheduled = new DateTimeOffset(today.ToDateTime(start), local.Offset);
            }

            var ordinal = count + 1;
            result.Add(new RoutineReminderDto
            {
                RoutineId = row.Id,
                Title = row.Title,
                Emoji = row.Emoji,
                Message = isInterval
                    ? (row.TargetCount > 1 ? $"间隔提醒 · 第 {ordinal}/{row.TargetCount} 次" : "间隔提醒")
                    : (row.TargetCount > 1 ? $"第 {ordinal}/{row.TargetCount} 次" : "到点提醒"),
                ScheduledFor = scheduled,
                IsInterval = isInterval,
            });

            if (markFired)
            {
                _store.MarkRoutineFired(row.Id, local.UtcDateTime);
            }
        }

        return result;
    }

    // ── 内部：项目 / 迭代计数 ─────────────────────────────────────────────

    internal static bool IsCompletedStatus(string status) =>
        status is "done" or "verified" or "closed";

    internal static bool IsTerminalStatus(string status) =>
        IsCompletedStatus(status) || status == "canceled";

    internal static bool IsActiveStatus(string status) =>
        status is "open" or "doing";

    private ProjectDto BuildProject(
        ProjectRow project,
        IReadOnlyList<IterationRow> allIterations,
        IReadOnlyList<TodoRow> allTodos,
        IReadOnlyList<ProjectGroupDto>? groups = null)
    {
        var rows = allIterations.Where(i => i.ProjectId == project.Id).ToList();
        var todos = allTodos.Where(t => t.ProjectId == project.Id && t.Status != "canceled").ToList();
        var iterations = rows
            .Select(i => BuildIteration(i, project, i.ParentId is null ? null : rows.FirstOrDefault(p => p.Id == i.ParentId), rows, todos))
            .ToList();

        var open = todos.Where(t => t.Status == "open" || t.Status == "doing").ToList();
        var done = todos.Count(t => IsCompletedStatus(t.Status));
        var nextDue = open.Where(t => t.DueAt is not null).Select(t => t.DueAt).Min();
        return PlannerMapper.ToProject(project, iterations, groups) with
        {
            TotalTasks = todos.Count,
            DoneTasks = done,
            OpenTasks = open.Count,
            OverdueTasks = open.Count(t => t.DueAt is not null && t.DueAt < NowUtc()),
            Progress = todos.Count == 0 ? 0 : Math.Round(done * 100.0 / todos.Count, 0),
            NextDueAt = nextDue is null ? null : PlannerMapper.ToDto(nextDue),
        };
    }

    /// <summary>一次取全部分组与成员关系，避免每个项目查一次库。</summary>
    private Dictionary<string, List<ProjectGroupDto>> LoadProjectGroups()
    {
        var rows = _store.QueryProjectGroups().ToDictionary(g => g.Id, g => g, StringComparer.Ordinal);
        var result = new Dictionary<string, List<ProjectGroupDto>>(StringComparer.Ordinal);
        foreach (var member in _store.QueryProjectGroupMembers())
        {
            if (!rows.TryGetValue(member.ProjectGroupId, out var group))
            {
                continue;
            }

            if (!result.TryGetValue(member.ProjectId, out var list))
            {
                list = [];
                result[member.ProjectId] = list;
            }

            list.Add(PlannerMapper.ToProjectGroup(group));
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => a.Sort != b.Sort ? a.Sort.CompareTo(b.Sort) : a.Name.CompareTo(b.Name));
        }

        return result;
    }

    private IReadOnlyList<ProjectGroupDto> GroupsOf(string projectId) =>
        LoadProjectGroups().GetValueOrDefault(projectId) ?? [];

    private IReadOnlyList<ProjectGroupDto> GroupsByIds(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return _store.QueryProjectGroups()
            .Where(g => ids.Contains(g.Id))
            .Select(g => PlannerMapper.ToProjectGroup(g))
            .ToList();
    }

    /// <summary>去重并校验分组存在；null/空都视为「不分组」。</summary>
    private IReadOnlyList<string> NormalizeGroupIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in ids)
        {
            var id = (raw ?? string.Empty).Trim();
            if (id.Length == 0 || result.Contains(id))
            {
                continue;
            }

            if (_store.GetProjectGroup(id) is null)
            {
                throw PlannerException.Validation($"没有找到分组「{id}」");
            }

            result.Add(id);
        }

        return result;
    }

    private static string NormalizeGroupColor(string? color)
    {
        var value = (color ?? string.Empty).Trim();
        if (value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        return "#7C8CF8";
    }


    private IterationDto BuildIteration(
        IterationRow iteration,
        ProjectRow? project,
        IterationRow? parent,
        IReadOnlyList<IterationRow> siblings,
        IReadOnlyList<TodoRow> allTodos)
    {
        var childIds = siblings.Where(i => i.ParentId == iteration.Id).Select(i => i.Id).ToList();
        childIds.Add(iteration.Id);
        var todos = allTodos.Where(t => t.IterationId is not null && childIds.Contains(t.IterationId)
                                        && t.Status != "canceled").ToList();
        var done = todos.Count(t => IsCompletedStatus(t.Status));
        return PlannerMapper.ToIteration(iteration, project?.Name, project?.Color, parent?.Name) with
        {
            TotalTasks = todos.Count,
            DoneTasks = done,
            OpenTasks = todos.Count - done,
            Progress = todos.Count == 0 ? 0 : Math.Round(done * 100.0 / todos.Count, 0),
        };
    }

    private (string? ProjectId, string? IterationId) ResolvePlacement(string? projectId, string? iterationId)
    {
        if (iterationId is { Length: > 0 })
        {
            var iteration = _store.GetIteration(iterationId)
                ?? throw PlannerException.Validation($"没有找到迭代「{iterationId}」");
            if (projectId is { Length: > 0 } && projectId != iteration.ProjectId)
            {
                throw PlannerException.Validation("迭代不属于指定项目");
            }

            return (iteration.ProjectId, iteration.Id);
        }

        if (projectId is { Length: > 0 })
        {
            var project = _store.GetProject(projectId)
                ?? throw PlannerException.Validation($"没有找到项目「{projectId}」");
            return (project.Id, null);
        }

        return (null, null);
    }

    private string? EnsureParentTodo(string? parentTodoId)
    {
        if (string.IsNullOrWhiteSpace(parentTodoId))
        {
            return null;
        }

        var parent = _store.GetTodo(parentTodoId)
            ?? throw PlannerException.Validation($"没有找到父任务「{parentTodoId}」");
        if (parent.ParentTodoId is not null)
        {
            throw PlannerException.Validation("子任务最多一层");
        }

        return parent.Id;
    }

    private static void ValidateProjectDates(DateTime? start, DateTime? target)
    {
        if (start is not null && target is not null && target < start)
        {
            throw PlannerException.Validation("项目目标日期不能早于开始日期");
        }
    }

    private static void ValidateIterationDates(DateTime? start, DateTime? end)
    {
        if (start is not null && end is not null && end < start)
        {
            throw PlannerException.Validation("迭代结束日期不能早于开始日期");
        }
    }

    private static string NormalizeColor(string? color)
    {
        var value = (color ?? string.Empty).Trim();
        if (value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        return DefaultProjectColor;
    }

    private static string NormalizeEmoji(string? emoji)
    {
        var value = (emoji ?? string.Empty).Trim();
        return value.Length is 0 or > 16 ? DefaultRoutineEmoji : value;
    }

    private RoutineNormalized NormalizeRoutine(
        string? title,
        string? emoji,
        string? notes,
        int daysMask,
        string? timeOfDay,
        int? intervalMinutes,
        string? activeEnd,
        int targetCount,
        DateTimeOffset? onceAt = null)
    {
        var cleanTitle = RequireTitle(title);

        if (onceAt is { } once)
        {
            var localOnce = TimeZoneInfo.ConvertTime(once, _time.LocalTimeZone);
            var localDate = DateOnly.FromDateTime(localOnce.DateTime);
            var localTime = new TimeOnly(localOnce.Hour, localOnce.Minute);
            return new RoutineNormalized(
                cleanTitle,
                NormalizeEmoji(emoji),
                NormalizeNotes(notes),
                1 << RoutineDays.BitFor(localDate.DayOfWeek),
                localTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                null,
                null,
                1,
                ToUtc(localDate, localTime));
        }

        if (daysMask is < 1 or > 127)
        {
            throw PlannerException.Validation("重复日必须至少选一天（掩码 1–127）");
        }

        var interval = intervalMinutes is > 0 ? Math.Clamp(intervalMinutes.Value, 1, 1440) : (int?)null;
        var start = NormalizeClock(timeOfDay) ?? (interval is null ? null : "08:00");
        if (interval is null)
        {
            return new RoutineNormalized(cleanTitle, NormalizeEmoji(emoji), NormalizeNotes(notes), daysMask, start, null, null, Math.Clamp(targetCount, 1, 99), null);
        }

        var end = NormalizeClock(activeEnd) ?? "22:00";
        if (start is null)
        {
            start = "08:00";
        }

        if (TimeOnly.Parse(start) >= TimeOnly.Parse(end))
        {
            throw PlannerException.Validation("间隔提醒的结束时间必须晚于开始时间");
        }

        return new RoutineNormalized(
            cleanTitle,
            NormalizeEmoji(emoji),
            NormalizeNotes(notes),
            daysMask,
            start,
            interval,
            end,
            Math.Clamp(targetCount, 1, 99),
            null);
    }

    private static string? NormalizeClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : throw PlannerException.Validation("时间格式必须是 HH:mm");
    }

    private static bool TryParseClock(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value ?? string.Empty, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private DateOnly TodayLocal() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>UTC 时刻 → TimeProvider 时区下的本地日期。</summary>
    private DateOnly LocalDateOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _time.LocalTimeZone));

    /// <summary>本地日期 + 本地时刻 → UTC；用 TimeProvider 的时区，测试可控。</summary>
    private DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, _time.LocalTimeZone);
    }

    private static ProjectRow CloneProject(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Color = row.Color,
        Status = row.Status,
        StartAt = row.StartAt,
        TargetAt = row.TargetAt,
        Sort = row.Sort,
        Revision = row.Revision,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private static IterationRow CloneIteration(IterationRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ParentId = row.ParentId,
        Level = row.Level,
        Name = row.Name,
        Goal = row.Goal,
        Status = row.Status,
        StartAt = row.StartAt,
        EndAt = row.EndAt,
        CompletedAt = row.CompletedAt,
        CompletionNote = row.CompletionNote,
        Sort = row.Sort,
        Revision = row.Revision,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private sealed record RoutineNormalized(
        string Title,
        string Emoji,
        string? Notes,
        int DaysMask,
        string? TimeOfDay,
        int? IntervalMinutes,
        string? ActiveEnd,
        int TargetCount,
        DateTime? OnceAt);
}
