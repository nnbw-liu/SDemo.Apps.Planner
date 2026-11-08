using System.Text.Json.Nodes;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Mcp;

namespace SDemo.Planner.Tests;

/// <summary>0.5.0：项目多分组、状态排序、MCP 总开关与逐工具开关。</summary>
public sealed class Version05Tests
{
    [Fact]
    public void ProjectGroups_AreManyToMany_AndSurviveUnrelatedEdits()
    {
        using var host = new TestHost();
        var work = host.Service.CreateProjectGroup(
            new ProjectGroupInput { Name = "工作", Color = "#3366ff" },
            WriteContext.Manual());
        var side = host.Service.CreateProjectGroup(
            new ProjectGroupInput { Name = "副业" },
            WriteContext.Manual());

        Assert.Equal("#7C8CF8", side.Color);

        var project = host.Service.CreateProject(
            new ProjectInput { Name = "事序", GroupIds = [work.Id, side.Id] },
            WriteContext.Manual());

        Assert.Equal(2, project.Groups.Count);
        Assert.Contains(project.Groups, g => g.Name == "工作");
        Assert.Equal(1, host.Service.ListProjectGroups().Single(g => g.Id == work.Id).ProjectCount);

        var updated = host.Service.UpdateProject(
            project.Id,
            project.Revision,
            new ProjectPatch { GroupIds = [side.Id] },
            WriteContext.Manual());
        Assert.Single(updated.Groups);
        Assert.Equal(side.Id, updated.Groups[0].Id);

        // GroupIds = null 表示只改其它字段，不动分组。
        var renamed = host.Service.UpdateProject(
            project.Id,
            updated.Revision,
            new ProjectPatch { Name = "事序 2" },
            WriteContext.Manual());
        Assert.Single(renamed.Groups);

        host.Service.DeleteProject(project.Id, renamed.Revision);
        Assert.DoesNotContain(
            host.Store.QueryProjectGroupMembers(),
            m => m.ProjectId == project.Id);
    }

    [Fact]
    public void DeletingGroup_RemovesMembershipWithoutDeletingProjects()
    {
        using var host = new TestHost();
        var group = host.Service.CreateProjectGroup(
            new ProjectGroupInput { Name = "临时" },
            WriteContext.Manual());
        var project = host.Service.CreateProject(
            new ProjectInput { Name = "保留的项目", GroupIds = [group.Id] },
            WriteContext.Manual());

        host.Service.DeleteProjectGroup(group.Id, group.Revision);

        Assert.Empty(host.Service.ListProjectGroups());
        var reloaded = host.Service.GetProject(project.Id)!;
        Assert.Empty(reloaded.Groups);
    }

    [Fact]
    public void StatusSort_PutsDoingFirst_ThenOpenDoneVerifiedClosedCanceled()
    {
        using var host = new TestHost();
        var names = new[] { "新建项", "活动项", "已完成项", "已验收项", "已关闭项", "已取消项" };
        var items = names
            .Select(name => host.Service.CreateTodos(
                [new TodoCreateInput { Title = name }],
                WriteContext.Manual()).Items[0])
            .ToList();

        TodoDto Item(string title) => items.Single(t => t.Title == title);

        void Move(string title, TodoStatus status)
        {
            var current = Item(title);
            host.Service.SetTodoStatus(current.Id, current.Revision, status, null, null, WriteContext.Manual());
        }

        Move("活动项", TodoStatus.Doing);
        Move("已完成项", TodoStatus.Done);
        Move("已验收项", TodoStatus.Verified);
        Move("已关闭项", TodoStatus.Closed);
        Move("已取消项", TodoStatus.Canceled);

        var ordered = host.Service.ListTodos(new TodoQuery { Status = "all", Sort = TodoSort.Status })
            .Select(t => t.Title)
            .ToList();

        Assert.Equal(
            new[] { "活动项", "新建项", "已完成项", "已验收项", "已关闭项", "已取消项" },
            ordered);
    }

    [Fact]
    public void TaskCanSpawnMultipleBugs_AndChildrenAreQueryableByParent()
    {
        using var host = new TestHost();
        var task = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "登录页联调" }],
            WriteContext.Manual()).Items[0];

        var bug1 = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "Safari 下按钮无响应", Kind = WorkItemKind.Bug, ParentTodoId = task.Id }],
            WriteContext.Manual()).Items[0];
        var bug2 = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "错误提示文案错位", Kind = WorkItemKind.Bug, ParentTodoId = task.Id }],
            WriteContext.Manual()).Items[0];

        Assert.Equal(task.Id, bug1.ParentTodoId);
        Assert.Equal(task.Id, bug2.ParentTodoId);

        var bugs = host.Service.ListTodos(new TodoQuery
        {
            Status = "all",
            ParentTodoId = task.Id,
            Sort = TodoSort.Status,
        });
        Assert.Equal(2, bugs.Count);
        Assert.All(bugs, b => Assert.Equal(WorkItemKind.Bug, b.Kind));

        var parent = host.Service.GetTodo(bugs[0].ParentTodoId!)!;
        Assert.Equal("登录页联调", parent.Title);
        Assert.Empty(host.Service.ListTodos(new TodoQuery { ParentTodoId = bug1.Id }));
    }

    [Fact]
    public void McpSettings_MasterSwitch_HidesAndRejectsTools()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        Assert.True(host.Service.GetMcpSettings().Enabled);
        Assert.Equal(13, Tools(server).Count);

        host.Service.SetMcpEnabled(false);

        Assert.Empty(Tools(server));
        var rejected = CallTool(server, "todo_list", new JsonObject { ["status"] = "open" });
        Assert.False(rejected["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.McpDisabled, rejected["code"]!.GetValue<string>());

        host.Service.SetMcpEnabled(true);
        host.Service.SetMcpToolEnabled("todo_search", false);
        Assert.Equal(12, Tools(server).Count);
        Assert.DoesNotContain(Tools(server), t => t!["name"]!.GetValue<string>() == "todo_search");

        var toolRejected = CallTool(server, "todo_search", new JsonObject { ["query"] = "事序" });
        Assert.False(toolRejected["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.McpToolDisabled, toolRejected["code"]!.GetValue<string>());
    }

    [Fact]
    public void McpDescriptors_MatchToolCatalog()
    {
        var catalog = McpToolCatalog.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var descriptors = McpToolDescriptors.All.ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.Equal(catalog.Keys.OrderBy(x => x), descriptors.Keys.OrderBy(x => x));
        foreach (var (name, tool) in catalog)
        {
            Assert.Equal(tool.Risk, descriptors[name].Risk);
            Assert.Equal(tool.ReadOnly, descriptors[name].ReadOnly);
        }
    }

    private static JsonArray Tools(McpServer server)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["method"] = "tools/list",
        };
        var response = JsonNode.Parse(server.HandleJson(request.ToJsonString()))!;
        return response["result"]!["tools"]!.AsArray();
    }

    private static JsonObject CallTool(McpServer server, string name, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments,
            },
        };
        var response = JsonNode.Parse(server.HandleJson(request.ToJsonString()))!;
        return response["result"]!["structuredContent"]!.AsObject();
    }
}
