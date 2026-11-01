using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

public sealed class PlannerServiceTests
{
    private static DateTimeOffset At(int daysFromNow, int hour = 9) =>
        new DateTimeOffset(DateTime.UtcNow.Date.AddDays(daysFromNow).AddHours(hour), TimeSpan.Zero);

    [Fact]
    public void CreateTodos_Manual_RecordsProvenanceAndRevision()
    {
        using var host = new TestHost();
        var result = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "给客户回邮件", DueAt = At(1), Priority = 3, Tags = ["客户"] }],
            WriteContext.Manual());

        var item = result.Items.Single();
        Assert.Equal(TodoSource.Manual, item.Source);
        Assert.Equal(1, item.Revision);
        Assert.Equal("客户", item.Tags.Single());
        Assert.Equal(PlannerStoreDefaultList, item.ListId);
        Assert.NotEqual(default, item.DueAt);
    }

    private const string PlannerStoreDefaultList = "li_inbox";

    [Fact]
    public void CreateTodos_AiExactDuplicate_IsRejectedButNearDuplicateIsWarned()
    {
        using var host = new TestHost();
        host.Service.CreateTodos([new TodoCreateInput { Title = "整理季度汇报材料" }], WriteContext.Manual());

        var exact = Assert.Throws<PlannerException>(() => host.Service.CreateTodos(
            [new TodoCreateInput { Title = "整理季度汇报材料" }],
            WriteContext.Ai("s_1", "把汇报材料整理一下")));
        Assert.Equal(ErrorCodes.DuplicateSuspect, exact.Code);

        var near = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "整理季度汇报材料初稿" }],
            WriteContext.Ai("s_1", "把汇报材料整理出初稿"));
        Assert.NotEmpty(near.Warnings);
        Assert.Single(near.Items);
    }

    [Fact]
    public void Update_ChangesDueDateAndKeepsProvenance()
    {
        using var host = new TestHost();
        var created = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "预约牙医" }],
            WriteContext.Ai("s_1", "下周记得预约牙医"));

        var updated = host.Service.UpdateTodo(
            created.Items[0].Id,
            created.Items[0].Revision,
            new TodoPatch { DueAt = At(3), Notes = "下午三点" },
            WriteContext.Manual());

        Assert.Equal(2, updated.Item.Revision);
        Assert.Equal("下午三点", updated.Item.Notes);
        Assert.Equal(TodoSource.Ai, updated.Item.Source);
        Assert.Equal("s_1", updated.Item.SourceSessionId);
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        using var host = new TestHost();
        var created = host.Service.CreateTodos([new TodoCreateInput { Title = "交水电费" }], WriteContext.Manual());
        var item = created.Items[0];

        var done = host.Service.CompleteTodo(item.Id, item.Revision, WriteContext.Manual());
        Assert.Equal(TodoStatus.Done, done.Item.Status);
        Assert.Equal(2, done.Item.Revision);
        Assert.NotEqual(default, done.Item.CompletedAt);

        var again = host.Service.CompleteTodo(item.Id, done.Item.Revision, WriteContext.Manual());
        Assert.Equal(2, again.Item.Revision);
        Assert.Equal(TodoStatus.Done, again.Item.Status);
    }

    [Fact]
    public void SoftDelete_IsRestorable()
    {
        using var host = new TestHost();
        var created = host.Service.CreateTodos([new TodoCreateInput { Title = "临时条目" }], WriteContext.Manual());
        var item = created.Items[0];

        var deleted = host.Service.DeleteTodo(item.Id, item.Revision, "误建", WriteContext.Manual());
        Assert.Equal(2, deleted.Revision);
        Assert.True(deleted.RestorableUntil > DateTimeOffset.UtcNow.AddDays(6));
        Assert.Empty(host.Service.ListTodos(new TodoQuery()));
        Assert.Single(host.Service.ListDeletedTodos());

        var restored = host.Service.RestoreTodo(item.Id, WriteContext.Manual());
        Assert.Equal(3, restored.Item.Revision);
        Assert.Single(host.Service.ListTodos(new TodoQuery()));
        Assert.Empty(host.Service.ListDeletedTodos());
    }

    [Fact]
    public void BulkUpdate_UpdatesAllRequestedItems()
    {
        using var host = new TestHost();
        var created = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "A" }, new TodoCreateInput { Title = "B" }, new TodoCreateInput { Title = "C" }],
            WriteContext.Manual());

        var result = host.Service.BulkUpdate(
            created.Items.Select(i => i.Id).ToList(),
            new TodoPatch { Priority = 4, DueAt = At(2) },
            WriteContext.Ai("s_1", "这三件都提到高优先级"));

        Assert.Equal(3, result.Updated);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(4, item.Priority);
            Assert.Equal(TodoSource.Manual, item.Source);
            Assert.Equal(2, item.Revision);
        });
    }

    [Fact]
    public void ClearList_SoftDeletesEverythingInList()
    {
        using var host = new TestHost();
        host.Service.CreateTodos(
            [new TodoCreateInput { Title = "一" }, new TodoCreateInput { Title = "二" }],
            WriteContext.Manual());

        var cleared = host.Service.ClearList("li_inbox", WriteContext.Manual());
        Assert.Equal(2, cleared.Deleted);
        Assert.Empty(host.Service.ListTodos(new TodoQuery()));
    }

    [Fact]
    public void CalendarRange_ReturnsTodoPointsAndEventRanges()
    {
        using var host = new TestHost();
        host.Service.CreateTodos([new TodoCreateInput { Title = "会前准备", DueAt = At(1, 10) }], WriteContext.Manual());
        host.Service.CreateEvent(new EventCreateInput
        {
            Title = "和客户开会",
            StartAt = At(1, 14),
            EndAt = At(1, 15),
            Location = "3 号会议室",
        }, WriteContext.Manual());

        var range = host.Service.CalendarRange(At(1, 0), At(2, 0));
        Assert.Single(range.Todos);
        Assert.Single(range.Events);
        Assert.Equal("和客户开会", range.Events[0].Title);
    }

    [Fact]
    public void Event_Create_ReportsOverlap()
    {
        using var host = new TestHost();
        var first = host.Service.CreateEvent(new EventCreateInput
        {
            Title = "周会",
            StartAt = At(1, 9),
            EndAt = At(1, 10),
        }, WriteContext.Manual());
        Assert.Empty(first.Conflicts);

        var second = host.Service.CreateEvent(new EventCreateInput
        {
            Title = "评审",
            StartAt = At(1, 9),
            EndAt = At(1, 10),
        }, WriteContext.Manual());

        Assert.Single(second.Conflicts);
        Assert.Equal("周会", second.Conflicts[0].Title);
    }

    [Fact]
    public void Event_DoesNotPolluteTodoCompletionStats()
    {
        using var host = new TestHost();
        host.Service.CreateTodos([new TodoCreateInput { Title = "任务" }], WriteContext.Manual());
        host.Service.CreateEvent(new EventCreateInput
        {
            Title = "日程",
            StartAt = At(0, 10),
            EndAt = At(0, 11),
        }, WriteContext.Manual());

        var stats = host.Service.Stats();
        Assert.Equal(1, stats.OpenTotal);
        Assert.Equal(0, stats.DoneToday);
    }

    [Fact]
    public void Draft_DoesNotCreateTodosUntilAccepted()
    {
        using var host = new TestHost();
        var draft = host.Service.PlanFromText(
            "- 修复登录页的跳转\n- 补充登录失败的提示文案",
            "按顺序执行",
            null,
            WriteContext.Ai("s_1", "把登录问题拆成待办"));

        Assert.Equal(2, draft.Items.Count);
        Assert.Empty(host.Service.ListTodos(new TodoQuery()));
        Assert.Single(host.Service.ListDrafts());

        var accepted = host.Service.AcceptDraft(new DraftAcceptRequest { DraftId = draft.DraftId }, WriteContext.Ai("s_1", null));
        Assert.True(accepted.Accepted);
        Assert.Equal(2, accepted.Items.Count);
        Assert.All(accepted.Items, item =>
        {
            Assert.Equal(TodoSource.Ai, item.Source);
            Assert.Equal("s_1", item.SourceSessionId);
            Assert.NotNull(item.SourceExcerpt);
        });

        Assert.Empty(host.Service.ListDrafts());
        Assert.Equal(DraftStatus.Accepted, host.Service.GetDraft(draft.DraftId)!.Status);
    }

    [Fact]
    public void AcceptDraft_CanRejectItems()
    {
        using var host = new TestHost();
        var draft = host.Service.PlanFromText(
            "1. 写方案\n2. 约客户\n3. 订会议室",
            null,
            null,
            WriteContext.Ai("s_1", null));

        var rejectId = draft.Items[1].DraftItemId;
        var accepted = host.Service.AcceptDraft(new DraftAcceptRequest
        {
            DraftId = draft.DraftId,
            Items = draft.Items.Select(i => new DraftAcceptItem
            {
                DraftItemId = i.DraftItemId,
                Reject = i.DraftItemId == rejectId,
            }).ToList(),
        }, WriteContext.Ai("s_1", null));

        Assert.Equal(2, accepted.Items.Count);
        Assert.Equal(1, accepted.Rejected);
    }

    [Fact]
    public void IcsExport_ContainsTodoAndEvent()
    {
        using var host = new TestHost();
        host.Service.CreateTodos([new TodoCreateInput
        {
            Title = "提交周报",
            Notes = "含本周进展",
            DueAt = At(1, 18),
        }], WriteContext.Manual());
        host.Service.CreateEvent(new EventCreateInput
        {
            Title = "站会",
            StartAt = At(1, 9),
            EndAt = At(1, 9).AddMinutes(15),
            Location = "线上",
        }, WriteContext.Manual());

        var ics = host.Service.ExportIcs();

        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("BEGIN:VTODO", ics);
        Assert.Contains("SUMMARY:提交周报", ics);
        Assert.Contains("BEGIN:VEVENT", ics);
        Assert.Contains("SUMMARY:站会", ics);
        Assert.Contains("LOCATION:线上", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public void IcsExport_EscapesAndFoldsLongLines()
    {
        var longTitle = new string('甲', 60);
        var content = IcsExporter.Export([], new Dictionary<string, IReadOnlyList<string>>(), [],
            new IcsExportOptions { CalendarName = longTitle }, DateTimeOffset.UtcNow);

        Assert.Contains("\r\n ", content);
    }

    [Fact]
    public void PurgeExpired_RemovesOnlyExpiredDeletedRows()
    {
        using var host = new TestHost(new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-8)));

        // 用过期时钟建条目，再切回真实时钟：此时软删的条目已过了 7 天保留期。
        var created = host.Service.CreateTodos([new TodoCreateInput { Title = "旧条目" }], WriteContext.Manual());
        host.Service.DeleteTodo(created.Items[0].Id, created.Items[0].Revision, "过期", WriteContext.Manual());

        var now = DateTimeOffset.UtcNow.AddDays(1);
        var purgeHost = new PlannerService(host.Store, new FixedTimeProvider(now));
        Assert.Equal(1, purgeHost.PurgeExpired());
        Assert.Null(host.Service.GetTodo(created.Items[0].Id, includeDeleted: true));
    }
}
