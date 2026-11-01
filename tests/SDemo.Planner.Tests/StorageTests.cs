using Microsoft.EntityFrameworkCore;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Storage;

namespace SDemo.Planner.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Migrate_CreatesAllTablesAndFts()
    {
        using var host = new TestHost();
        using var db = host.Store.Open();

        Assert.Single(db.Lists.ToList());
        Assert.Equal(PlannerStore.DefaultListId, db.Lists.Single().Id);

        var ftsExists = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'todo_fts'")
            .Single();
        Assert.Equal(1, ftsExists);
    }

    [Fact]
    public void Search_ChineseTwoCharacterQuery_Hits()
    {
        using var host = new TestHost();
        var service = host.Service;

        var created = service.CreateTodos(
            [new TodoCreateInput { Title = "修复登录接口的鉴权问题", Notes = "登录超时后 token 失效" }],
            WriteContext.Manual());

        var result = service.Search("登录", 10);

        Assert.Single(result.Hits);
        Assert.Equal(created.Items[0].Id, result.Hits[0].Id);
        Assert.False(result.Widened);
    }

    [Fact]
    public void Search_SingleCharacterFallsBackToLike()
    {
        using var host = new TestHost();
        var service = host.Service;
        service.CreateTodos([new TodoCreateInput { Title = "写日报" }], WriteContext.Manual());

        var result = service.Search("报", 10);

        Assert.Single(result.Hits);
        Assert.True(result.Widened);
    }

    [Fact]
    public void Update_WithStaleRevision_ThrowsStoreConflict()
    {
        using var host = new TestHost();
        var service = host.Service;
        var created = service.CreateTodos([new TodoCreateInput { Title = "写方案" }], WriteContext.Manual());
        var item = created.Items[0];

        service.UpdateTodo(item.Id, item.Revision, new TodoPatch { Title = "写方案 v2" }, WriteContext.Manual());

        var stale = Assert.Throws<PlannerException>(() =>
            service.UpdateTodo(item.Id, item.Revision, new TodoPatch { Title = "写方案 v3" }, WriteContext.Manual()));

        Assert.Equal(ErrorCodes.Conflict, stale.Code);
        Assert.Equal(created.Items[0].Revision + 1, stale.Details!["currentRevision"]);
    }

    [Fact]
    public void EveryWrite_ProducesOutboxAndOpLog()
    {
        using var host = new TestHost();
        var service = host.Service;
        service.CreateTodos([new TodoCreateInput { Title = "买菜" }], WriteContext.Manual());

        using var db = host.Store.Open();
        Assert.Equal(1, db.Outbox.Count());
        Assert.Equal(1, db.OpLog.Count());
        Assert.Equal("create", db.Outbox.Single().Op);
        Assert.Equal("todo", db.Outbox.Single().EntityType);
        Assert.Contains("买菜", db.Outbox.Single().PayloadJson);
        Assert.NotNull(db.OpLog.Single().AfterJson);
    }

    [Fact]
    public void FtsTerms_AreTwoGrams_ForChinese()
    {
        var terms = TodoFts.BuildTerms("修复登录接口", null);
        Assert.Contains("登录", terms);
        Assert.Contains("修复", terms);
        Assert.Contains("复登", terms);
    }
}
