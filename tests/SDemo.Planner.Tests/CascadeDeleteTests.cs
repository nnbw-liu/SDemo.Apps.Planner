using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>删除项目/迭代时，任务保留（默认）还是随父项一并移入回收站。</summary>
public sealed class CascadeDeleteTests
{
    [Fact]
    public void DeleteProject_Default_KeepsTasksButDetachesThem()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "保留任务" }, WriteContext.Manual());
        var iteration = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "迭代" },
            WriteContext.Manual());
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "任务", ProjectId = project.Id, IterationId = iteration.Id }],
            WriteContext.Manual()).Items.Single();

        host.Service.DeleteProject(project.Id, project.Revision);

        var row = host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == todo.Id);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.ProjectId);
        Assert.Null(row.IterationId);
        Assert.Equal(todo.Revision + 1, row.Revision);
    }

    [Fact]
    public void DeleteProject_WithDeleteTasks_SoftDeletesEveryTask()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "连任务一起删" }, WriteContext.Manual());
        var keep = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "别的项目", ProjectId = null }],
            WriteContext.Manual()).Items.Single();
        var a = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "甲", ProjectId = project.Id }],
            WriteContext.Manual()).Items.Single();
        var b = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "乙", ProjectId = project.Id }],
            WriteContext.Manual()).Items.Single();

        host.Service.DeleteProject(project.Id, project.Revision, deleteTasks: true);

        Assert.NotNull(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == a.Id).DeletedAt);
        Assert.NotNull(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == b.Id).DeletedAt);
        Assert.Null(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == keep.Id).DeletedAt);
        Assert.DoesNotContain(
            host.Service.ListTodos(new TodoQuery { Status = "all", ProjectId = project.Id, IncludeDeleted = true }),
            t => t.Id == a.Id);
    }

    [Fact]
    public void DeleteIteration_WithDeleteTasks_CoversChildIterations()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "迭代级联" }, WriteContext.Manual());
        var big = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "大迭代" },
            WriteContext.Manual());
        var small = host.Service.CreateIteration(
            new IterationInput
            {
                ProjectId = project.Id,
                ParentId = big.Id,
                Level = IterationLevel.Small,
                Name = "小迭代",
            },
            WriteContext.Manual());

        var inBig = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "大迭代任务", ProjectId = project.Id, IterationId = big.Id }],
            WriteContext.Manual()).Items.Single();
        var inSmall = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "小迭代任务", ProjectId = project.Id, IterationId = small.Id }],
            WriteContext.Manual()).Items.Single();
        var backlog = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "项目净任务", ProjectId = project.Id }],
            WriteContext.Manual()).Items.Single();

        host.Service.DeleteIteration(big.Id, big.Revision, deleteTasks: true);

        Assert.NotNull(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == inBig.Id).DeletedAt);
        Assert.NotNull(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == inSmall.Id).DeletedAt);
        Assert.Null(host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == backlog.Id).DeletedAt);
    }

    [Fact]
    public void DeleteIteration_Default_KeepsTasksInProject()
    {
        using var host = new TestHost();
        var project = host.Service.CreateProject(new ProjectInput { Name = "迭代保留" }, WriteContext.Manual());
        var iteration = host.Service.CreateIteration(
            new IterationInput { ProjectId = project.Id, Level = IterationLevel.Big, Name = "迭代" },
            WriteContext.Manual());
        var todo = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "任务", ProjectId = project.Id, IterationId = iteration.Id }],
            WriteContext.Manual()).Items.Single();

        host.Service.DeleteIteration(iteration.Id, iteration.Revision);

        var row = host.Store.QueryProjectTodos(includeDeleted: true).Single(t => t.Id == todo.Id);
        Assert.Null(row.DeletedAt);
        Assert.Equal(project.Id, row.ProjectId);
        Assert.Null(row.IterationId);
    }
}
