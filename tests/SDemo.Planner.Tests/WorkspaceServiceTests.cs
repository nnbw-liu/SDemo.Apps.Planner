using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

public sealed class WorkspaceServiceTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "test-utc+8",
        TimeSpan.FromHours(8),
        "test-utc+8",
        "test-utc+8");

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Project_WithBigAndSmallIterations_AggregatesTaskProgress()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(
            new ProjectInput { Name = "SDemo 桌面", Color = "#ff8800" },
            WriteContext.Manual());
        Assert.Equal("#FF8800", project.Color);
        Assert.Equal(1, project.Revision);

        var big = host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = project.Id,
                Level = IterationLevel.Big,
                Name = "0.9 发布",
                StartAt = Local(2026, 11, 1, 9),
                EndAt = Local(2026, 12, 1, 9),
            },
            WriteContext.Manual());
        var small = host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = project.Id,
                ParentId = big.Id,
                Level = IterationLevel.Small,
                Name = "Sprint 12",
            },
            WriteContext.Manual());

        Assert.Equal(IterationLevel.Big, big.Level);
        Assert.Equal(big.Id, small.ParentId);
        Assert.Equal(big.Name, small.ParentName);

        var todo = host.Service.CreateTodos(
            [
                new TodoCreateInput
                {
                    Title = "修复日历拖拽",
                    ProjectId = project.Id,
                    IterationId = small.Id,
                    Kind = WorkItemKind.Bug,
                },
            ],
            WriteContext.Manual()).Items.Single();

        Assert.Equal(WorkItemKind.Bug, todo.Kind);
        Assert.Equal(small.Id, todo.IterationId);
        Assert.Equal(project.Id, todo.ProjectId);

        host.Service.CompleteTodo(todo.Id, todo.Revision, WriteContext.Manual());

        var loaded = host.Service.GetProject(project.Id)!;
        Assert.Equal(1, loaded.TotalTasks);
        Assert.Equal(1, loaded.DoneTasks);
        Assert.Equal(100, loaded.Progress);

        var loadedBig = loaded.Iterations.Single(i => i.Id == big.Id);
        Assert.Equal(1, loadedBig.TotalTasks);
        Assert.Equal(100, loadedBig.Progress);
    }

    [Fact]
    public void SmallIteration_RequiresBigParentInSameProject()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "A" }, WriteContext.Manual());
        var other = host.Service.CreateProject(new ProjectInput { Name = "B" }, WriteContext.Manual());
        var big = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "Big" },
            WriteContext.Manual());

        var ex = Assert.Throws<PlannerException>(() => host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = other.Id,
                ParentId = big.Id,
                Level = IterationLevel.Small,
                Name = "Small",
            },
            WriteContext.Manual()));
        Assert.Equal(ErrorCodes.Validation, ex.Code);

        Assert.Throws<PlannerException>(() => host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Small, Name = "Orphan" },
            WriteContext.Manual()));
    }

    [Fact]
    public void Routines_FilterByWeekday_AndCheckIsCappedAtZero()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 8), Zone)); // 周五
        var weekdayOnly = host.Service.CreateRoutine(
            new RoutineInput { Title = "喝水", DaysMask = 0b0011111, TargetCount = 8 },
            WriteContext.Manual());
        host.Service.CreateRoutine(
            new RoutineInput { Title = "周末买菜", DaysMask = 0b1100000 },
            WriteContext.Manual());

        var friday = host.Service.ListRoutines();
        Assert.Single(friday);
        Assert.Equal("喝水", friday[0].Title);

        var checkedOnce = host.Service.CheckRoutine(weekdayOnly.Id);
        Assert.Equal(1, checkedOnce.TodayCount);
        Assert.False(checkedOnce.DoneToday);

        var lowered = host.Service.CheckRoutine(weekdayOnly.Id, -5);
        Assert.Equal(0, lowered.TodayCount);
    }

    [Fact]
    public void FixedReminder_FiresOncePerDay_AndStopsWhenTargetReached()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9, 5), Zone));
        var routine = host.Service.CreateRoutine(
            new RoutineInput { Title = "吃药", TimeOfDay = "09:00", TargetCount = 1 },
            WriteContext.Manual());

        var due = host.Service.CollectDueReminders();
        Assert.Single(due);
        Assert.Equal(routine.Id, due[0].RoutineId);
        Assert.False(due[0].IsInterval);

        Assert.Empty(host.Service.CollectDueReminders());

        host.Service.CheckRoutine(routine.Id);
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 9, 6)));

        // 第二天是新的目标，固定提醒应再次触发。
        Assert.Single(host.Service.CollectDueReminders(Local(2026, 11, 7, 9, 5)));
    }

    [Fact]
    public void FixedReminder_DoesNotCatchUpHoursLate()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9, 5), Zone));
        host.Service.CreateRoutine(
            new RoutineInput { Title = "吃药", TimeOfDay = "09:00", TargetCount = 1 },
            WriteContext.Manual());

        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 9, 45)));
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 21, 0)));
        Assert.Single(host.Service.CollectDueReminders(Local(2026, 11, 7, 9, 10)));
    }

    [Fact]
    public void IntervalReminder_WaitsForNextSlot_AndUsesActiveWindow()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9, 1), Zone));
        var routine = host.Service.CreateRoutine(
            new RoutineInput
            {
                Title = "起来走动",
                TimeOfDay = "09:00",
                IntervalMinutes = 60,
                ActiveEnd = "18:00",
                TargetCount = 8,
            },
            WriteContext.Manual());

        var first = host.Service.CollectDueReminders();
        Assert.Single(first);
        Assert.True(first[0].IsInterval);

        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 9, 30)));

        var second = host.Service.CollectDueReminders(Local(2026, 11, 6, 10, 1));
        Assert.Single(second);
        Assert.Equal(routine.Id, second[0].RoutineId);

        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 8, 59)));
    }

    [Fact]
    public void OnceReminder_FiresOnlyOnSelectedDay_ThenDisables()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9), Zone)); // 周五
        var routine = host.Service.CreateRoutine(
            new RoutineInput { Title = "交燃气费", OnceAt = Local(2026, 11, 7, 10, 30) },
            WriteContext.Manual());

        Assert.NotNull(routine.OnceAt);
        Assert.Equal("10:30", routine.TimeOfDay);
        Assert.Equal(1, routine.TargetCount);
        Assert.Null(routine.IntervalMinutes);
        Assert.Equal(0b0100000, routine.DaysMask); // 周六，只用于展示

        var saturday = new DateOnly(2026, 11, 7);
        Assert.Empty(host.Service.ListRoutines(new DateOnly(2026, 11, 6)));
        Assert.Single(host.Service.ListRoutines(saturday));

        // 日期不对、时间没到都不触发。
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 10, 30)));
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 7, 10, 29)));

        var due = host.Service.CollectDueReminders(Local(2026, 11, 7, 10, 35));
        Assert.Single(due);
        Assert.Equal(routine.Id, due[0].RoutineId);
        Assert.False(due[0].IsInterval);
        Assert.Contains("仅一次", due[0].Message);

        // 触发后自动停用，不再进入今日列表，也不会第二天重复。
        Assert.False(host.Service.ListAllRoutines().Single().Enabled);
        Assert.Empty(host.Service.ListRoutines(saturday));
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 7, 10, 40)));
        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 8, 10, 30)));
    }

    [Fact]
    public void OnceReminder_DoesNotCatchUpHoursLate()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9), Zone));
        host.Service.CreateRoutine(
            new RoutineInput { Title = "取快递", OnceAt = Local(2026, 11, 6, 18, 0) },
            WriteContext.Manual());

        Assert.Empty(host.Service.CollectDueReminders(Local(2026, 11, 6, 18, 45)));
        Assert.Single(host.Service.CollectDueReminders(Local(2026, 11, 6, 18, 20)));
    }

    [Fact]
    public void TodoHistory_RecordsCreateUpdatePriorityStatusAndDelete()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 7, 9), Zone));
        var created = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "整理周报", Priority = 2 }],
            WriteContext.Manual()).Items.Single();

        var updated = host.Service.UpdateTodo(
            created.Id,
            created.Revision,
            new TodoPatch { Title = "整理本周周报", Priority = 1, DueAt = Local(2026, 11, 8, 18) },
            WriteContext.Manual()).Item;

        var doing = host.Service.SetTodoStatus(
            updated.Id,
            updated.Revision,
            TodoStatus.Doing,
            null,
            null,
            WriteContext.Manual()).Item;

        host.Service.DeleteTodo(doing.Id, doing.Revision, "合并到另一条", WriteContext.Manual());

        var history = host.Service.ListTodoHistory(created.Id);
        Assert.Equal(4, history.Count);
        Assert.Equal("delete", history[0].Op);
        Assert.Equal("删除任务", history[0].Action);
        Assert.Equal("合并到另一条", history[0].Reason);
        Assert.Equal("manual", history[0].Actor);

        Assert.Equal("status", history[1].Op);
        Assert.Contains("进行中", history[1].Action);
        Assert.Contains(history[1].Changes, c => c.Field == "状态" && c.After == "进行中");

        Assert.Equal("update", history[2].Op);
        Assert.Contains(history[2].Changes, c => c.Field == "标题" && c.Before == "整理周报" && c.After == "整理本周周报");
        Assert.Contains(history[2].Changes, c => c.Field == "优先级" && c.Before == "中" && c.After == "高");
        Assert.Contains(history[2].Changes, c => c.Field == "截止时间" && c.After == "2026-11-08 18:00");

        Assert.Equal("create", history[3].Op);
        Assert.Contains(history[3].Changes, c => c.Field == "标题" && c.After == "整理周报");

        // 删除后仍能按 id 回放历史，limit 生效。
        Assert.Equal(2, host.Service.ListTodoHistory(created.Id, limit: 2).Count);
    }

    [Fact]
    public void HomeSnapshot_ContainsRoutinesDueTodayAndActiveIterations()
    {
        using var host = new TestHost(new ZonedTimeProvider(Local(2026, 11, 6, 9), Zone));
        var project = host.Service.CreateProject(new ProjectInput { Name = "首页验证" }, WriteContext.Manual());
        host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = project.Id,
                Level = IterationLevel.Big,
                Name = "11 月大迭代",
                StartAt = Local(2026, 11, 1, 0),
                EndAt = Local(2026, 11, 30, 23),
            },
            WriteContext.Manual());
        host.Service.CreateRoutine(new RoutineInput { Title = "喝水", TargetCount = 8 }, WriteContext.Manual());
        host.Service.CreateTodos(
            [new TodoCreateInput { Title = "今天要做", DueAt = Local(2026, 11, 6, 18) }],
            WriteContext.Manual());

        var home = host.Service.Home();
        Assert.Equal("2026-11-06", home.Today);
        Assert.Single(home.Routines);
        Assert.Equal(1, home.RoutineTotal);
        Assert.Equal(0, home.RoutineDone);
        Assert.Single(home.DueToday);
        Assert.Single(home.ActiveIterations);
        Assert.Equal("首页验证", home.Projects.Single().Name);
    }

    [Fact]
    public void TodoStatus_StoresCompletionNoteArtifacts_AndReopenKeepsThem()
    {
        using var host = new TestHost(new FixedTimeProvider(Local(2026, 11, 6, 10)));
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "验证登录流程" }],
            WriteContext.Manual()).Items.Single();

        var verified = host.Service.SetTodoStatus(
            todo.Id,
            todo.Revision,
            TodoStatus.Verified,
            "已按验收单跑完",
            ["https://example.test/report", "D:\\artifacts\\login.zip"],
            WriteContext.Manual()).Item;

        Assert.Equal(TodoStatus.Verified, verified.Status);
        Assert.Equal("已按验收单跑完", verified.CompletionNote);
        Assert.Equal(2, verified.Artifacts.Count);
        Assert.NotNull(verified.CompletedAt);

        var reopened = host.Service.ReopenTodo(verified.Id, verified.Revision, WriteContext.Manual()).Item;
        Assert.Equal(TodoStatus.Open, reopened.Status);
        Assert.Null(reopened.CompletedAt);
        Assert.Equal("已按验收单跑完", reopened.CompletionNote);
        Assert.Equal(2, reopened.Artifacts.Count);
    }

    [Fact]
    public void PostponeTodo_MovesDueDateFromExistingDueOrNow()
    {
        using var host = new TestHost(new FixedTimeProvider(Local(2026, 11, 6, 9)));
        var task = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "延期测试", DueAt = Local(2026, 11, 8, 18) }],
            WriteContext.Manual()).Items.Single();

        var postponed = host.Service.PostponeTodo(task.Id, task.Revision, 2, WriteContext.Manual()).Item;
        Assert.Equal(Local(2026, 11, 10, 18), postponed.DueAt);

        var noDue = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "无截止" }],
            WriteContext.Manual()).Items.Single();
        var moved = host.Service.PostponeTodo(noDue.Id, noDue.Revision, 1, WriteContext.Manual()).Item;
        Assert.Equal(Local(2026, 11, 7, 9), moved.DueAt);
    }

    [Fact]
    public void TodoQuery_SearchesCompletionNote_AndSortsByPriority()
    {
        using var host = new TestHost();
        var low = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "低优先级", Priority = 3 }],
            WriteContext.Manual()).Items.Single();
        var high = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "高优先级", Priority = 1 }],
            WriteContext.Manual()).Items.Single();

        host.Service.SetTodoStatus(
            low.Id,
            low.Revision,
            TodoStatus.Done,
            "验收备注：登录已通过",
            [],
            WriteContext.Manual());

        var found = host.Service.ListTodos(new TodoQuery { Status = "all", Q = "验收备注" });
        Assert.Single(found);
        Assert.Equal(low.Id, found[0].Id);

        var sorted = host.Service.ListTodos(new TodoQuery { Status = "all", Sort = TodoSort.Priority });
        Assert.Equal(high.Id, sorted[0].Id);

        var activeOnly = host.Service.ListTodos(new TodoQuery { Status = "active" });
        Assert.Single(activeOnly);
        Assert.Equal(high.Id, activeOnly[0].Id);
    }

    [Fact]
    public void CompleteIteration_MarksChildrenDone_AndReopenRestoresParent()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "迭代完成" }, WriteContext.Manual());
        var big = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "0.9" },
            WriteContext.Manual());
        var small = host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = project.Id,
                ParentId = big.Id,
                Level = IterationLevel.Small,
                Name = "Sprint 1",
            },
            WriteContext.Manual());

        var completed = host.Service.CompleteIteration(
            big.Id,
            big.Revision,
            "本版已发布",
            WriteContext.Manual());

        Assert.Equal(IterationStatus.Done, completed.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("本版已发布", completed.CompletionNote);

        var loaded = host.Service.GetProject(project.Id)!;
        Assert.Equal(IterationStatus.Done, loaded.Iterations.Single(i => i.Id == big.Id).Status);
        Assert.Equal(IterationStatus.Done, loaded.Iterations.Single(i => i.Id == small.Id).Status);

        var reopened = host.Service.ReopenIteration(big.Id, completed.Revision, WriteContext.Manual());
        Assert.Equal(IterationStatus.Active, reopened.Status);
        Assert.Null(reopened.CompletedAt);
    }

    private sealed class ZonedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
