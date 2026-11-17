using System.IO;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>0.8.0：任务阶段性进展——百分比、产物、附件、软删与随任务清理。</summary>
public sealed class ProgressTests
{
    [Fact]
    public void AddProgress_UpdatesTodoSummary()
    {
        using var host = new TestHost();
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "阶段产物任务" }],
            WriteContext.Manual()).Items.Single();

        var progress = host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput
            {
                Title = "方案评审",
                Notes = "评审通过，按意见补了接口时序。",
                Percent = 35,
                Artifacts = ["https://example.com/plan", @"C:\builds\plan-v1.zip"],
            },
            WriteContext.Manual());

        Assert.Equal("方案评审", progress.Title);
        Assert.Equal(35, progress.Percent);
        Assert.Equal(2, progress.Artifacts.Count);
        Assert.Equal(1, progress.Revision);
        Assert.Single(host.Service.ListTodoProgress(todo.Id));

        var detail = host.Service.GetTodo(todo.Id)!;
        Assert.Equal(1, detail.ProgressCount);
        Assert.Equal(35, detail.ProgressPercent);
        Assert.Equal("方案评审", detail.LatestProgressTitle);
    }

    [Fact]
    public void UpdateProgress_MergesFieldsAndBumpsRevision()
    {
        using var host = new TestHost();
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "合并字段任务" }],
            WriteContext.Manual()).Items.Single();
        var progress = host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "方案评审", Notes = "先按 A 方案。", Percent = 20 },
            WriteContext.Manual());

        var updated = host.Service.UpdateTodoProgress(
            progress.Id,
            progress.Revision,
            new TodoProgressPatch { Title = "接口联调", Percent = 70, Artifacts = ["PR #12"] },
            WriteContext.Manual());

        Assert.Equal("接口联调", updated.Title);
        Assert.Equal("先按 A 方案。", updated.Notes);
        Assert.Equal(70, updated.Percent);
        Assert.Equal(2, updated.Revision);
        Assert.Single(updated.Artifacts);

        var cleared = host.Service.UpdateTodoProgress(
            updated.Id,
            updated.Revision,
            new TodoProgressPatch { ClearPercent = true, ClearArtifacts = true },
            WriteContext.Manual());

        Assert.Null(cleared.Percent);
        Assert.Empty(cleared.Artifacts);
        Assert.Equal("先按 A 方案。", cleared.Notes);

        var detail = host.Service.GetTodo(todo.Id)!;
        Assert.Equal(1, detail.ProgressCount);
        Assert.Null(detail.ProgressPercent);
    }

    [Fact]
    public void UpdateProgress_RejectsStaleRevision()
    {
        using var host = new TestHost();
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "并发进展任务" }],
            WriteContext.Manual()).Items.Single();
        var progress = host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "开发中" },
            WriteContext.Manual());

        var error = Assert.Throws<PlannerException>(() => host.Service.UpdateTodoProgress(
            progress.Id,
            progress.Revision + 5,
            new TodoProgressPatch { Title = "并发覆盖" },
            WriteContext.Manual()));

        Assert.Equal(ErrorCodes.Conflict, error.Code);
        Assert.Equal(progress.Revision, error.Details!["currentRevision"]);
    }

    [Fact]
    public void DeleteProgress_HidesFromListAndPurgesAfterRetention()
    {
        using var host = new TestHost(new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-8)));
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "删进展任务" }],
            WriteContext.Manual()).Items.Single();
        var progress = host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "待清理" },
            WriteContext.Manual());

        host.Service.DeleteTodoProgress(progress.Id, progress.Revision, null, WriteContext.Manual());
        Assert.Empty(host.Service.ListTodoProgress(todo.Id));

        var live = new PlannerService(host.Store, new FixedTimeProvider(DateTimeOffset.UtcNow));
        Assert.Equal(1, live.PurgeExpired());
        Assert.Null(host.Store.GetTodoProgress(progress.Id, includeDeleted: true));
    }

    [Fact]
    public void DeleteTodo_CascadesProgressAndOrphanAttachments()
    {
        using var host = new TestHost(new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-8)));
        UseTempRoot(host);
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "级联清理任务" }],
            WriteContext.Manual()).Items.Single();
        var progress = host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "验收", Percent = 100 },
            WriteContext.Manual());
        var attachment = host.Service.AddAttachment(
            AttachmentOwnerTypes.Progress, progress.Id, TempFile("验收单.txt", "passed"));

        host.Service.DeleteTodo(todo.Id, todo.Revision, "清理", WriteContext.Manual());

        var live = new PlannerService(host.Store, new FixedTimeProvider(DateTimeOffset.UtcNow));
        Assert.True(live.PurgeExpired() >= 2);
        Assert.Null(host.Store.GetTodo(todo.Id, includeDeleted: true));
        Assert.Null(host.Store.GetTodoProgress(progress.Id, includeDeleted: true));
        Assert.Empty(live.ListAttachments(AttachmentOwnerTypes.Progress, progress.Id));
        Assert.Null(live.ResolveAttachmentPath(attachment.Id));
    }

    [Fact]
    public void AddProgress_ValidatesPercentArtifactsAndTitle()
    {
        using var host = new TestHost();
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "校验进展任务" }],
            WriteContext.Manual()).Items.Single();

        var percentError = Assert.Throws<PlannerException>(() => host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "越界", Percent = 101 },
            WriteContext.Manual()));
        Assert.Contains("0 到 100", percentError.Message, StringComparison.Ordinal);

        var artifacts = Enumerable.Range(0, PlannerConstants.MaxProgressArtifacts + 1)
            .Select(i => $"https://example.com/{i}")
            .ToList();
        Assert.Throws<PlannerException>(() => host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = "产物太多", Artifacts = artifacts },
            WriteContext.Manual()));

        var titleError = Assert.Throws<PlannerException>(() => host.Service.AddTodoProgress(
            todo.Id,
            new TodoProgressInput { Title = new string('长', PlannerConstants.MaxProgressTitleChars + 1) },
            WriteContext.Manual()));
        Assert.Contains("阶段名超过", titleError.Message, StringComparison.Ordinal);
    }

    private static string UseTempRoot(TestHost host)
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-progress-attach", Guid.NewGuid().ToString("N"));
        host.Service.SaveAttachmentSettings(new AttachmentSettingsInput { RootDirectory = root });
        return root;
    }

    private static string TempFile(string name, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdemo-progress-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
