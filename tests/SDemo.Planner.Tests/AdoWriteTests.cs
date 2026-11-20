using System.Net;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>0.9：上传新工作项、关联已有工作项、只同步状态。</summary>
public sealed class AdoWriteTests
{
    private const string Collection = "http://172.30.184.25:8088/Utilities";

    [Fact]
    public async Task CreateWorkItem_PostsAddOpsAndParsesNewId()
    {
        var handler = new SequenceHandler(
            ("""{"id":321,"rev":1,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.CreateWorkItemAsync(
            Collection, "pat", "CloudProj", ["任务"], "新任务", "第一行\n第二行", "open", 2);

        Assert.True(result.Ok);
        Assert.Equal("321", result.ExternalId);
        Assert.Equal(1, result.ExternalRev);
        Assert.EndsWith("/_workitems/edit/321", result.ExternalUrl);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Contains("/_apis/wit/workitems/$", handler.Uris[0], StringComparison.Ordinal);
        Assert.Contains("api-version=5.1", handler.Uris[0], StringComparison.Ordinal);

        using var document = JsonDocument.Parse(handler.Bodies[0]);
        var ops = document.RootElement.EnumerateArray().ToList();
        Assert.DoesNotContain(ops, op => op.GetProperty("path").GetString() == "/rev");

        var fields = ops.ToDictionary(
            op => op.GetProperty("path").GetString()!,
            op => op.GetProperty("value"),
            StringComparer.Ordinal);
        Assert.Equal("新任务", fields["/fields/System.Title"].GetString());
        Assert.Equal("第一行<br />第二行", fields["/fields/System.Description"].GetString());
        Assert.Equal(3, fields["/fields/Microsoft.VSTS.Common.Priority"].GetInt32());
        Assert.Equal("新建", fields["/fields/System.State"].GetString());
    }

    [Fact]
    public async Task CreateWorkItem_FallsBackThroughStatesAndTypes()
    {
        var handler = new SequenceHandler(
            ("""{"message":"type"}""", HttpStatusCode.NotFound),
            ("""{"message":"type"}""", HttpStatusCode.NotFound),
            ("""{"message":"state"}""", HttpStatusCode.BadRequest),
            ("""{"id":322,"rev":1,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.CreateWorkItemAsync(
            Collection, "pat", "CloudProj", ["任务", "Task"], "降级任务", null, "doing", 2);

        Assert.True(result.Ok);
        Assert.Equal("322", result.ExternalId);
        Assert.Equal(4, handler.Methods.Count);

        static string? StateOf(string body)
        {
            using var document = JsonDocument.Parse(body);
            foreach (var op in document.RootElement.EnumerateArray())
            {
                if (op.GetProperty("path").GetString() == "/fields/System.State")
                {
                    return op.GetProperty("value").GetString();
                }
            }

            return null;
        }

        // 任务类型：活动 → Active → 不带状态（模板不认这两个状态名）；
        // 换 Task 类型后「活动」被接受。
        Assert.Equal("活动", StateOf(handler.Bodies[0]));
        Assert.Equal("Active", StateOf(handler.Bodies[1]));
        Assert.Null(StateOf(handler.Bodies[2]));
        Assert.Equal("活动", StateOf(handler.Bodies[3]));
    }

    [Fact]
    public async Task UpdateStatus_SendsOnlyRevisionGuardAndState()
    {
        var handler = new SequenceHandler(
            ("""{"id":66,"rev":10,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.UpdateWorkItemStatusAsync(Collection, "pat", 66, "doing", 9);

        Assert.True(result.Ok);
        Assert.Equal(10, result.ExternalRev);
        Assert.Equal(["状态"], result.ChangedFields);
        Assert.Equal("PATCH", handler.Methods[0]);

        using var document = JsonDocument.Parse(handler.Bodies[0]);
        var ops = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, ops.Count);
        Assert.Equal("test", ops[0].GetProperty("op").GetString());
        Assert.Equal("/rev", ops[0].GetProperty("path").GetString());
        Assert.Equal(9, ops[0].GetProperty("value").GetInt32());
        Assert.Equal("replace", ops[1].GetProperty("op").GetString());
        Assert.Equal("/fields/System.State", ops[1].GetProperty("path").GetString());
        Assert.Equal("活动", ops[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task UploadTodo_LinksExternalFieldsWithoutTouchingLocalTitle()
    {
        using var harness = new Harness(
            ("""{"id":321,"rev":1,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var todo = harness.CreateTodo("本地任务");

        var result = await harness.Ado.UploadTodoAsync(todo.Id);

        Assert.True(result.Ok);
        var refreshed = harness.Host.Service.GetTodo(todo.Id)!;
        Assert.Equal(AdoMapper.SourceName, refreshed.ExternalSource);
        Assert.Equal("321", refreshed.ExternalId);
        Assert.Equal(1, refreshed.ExternalRev);
        Assert.EndsWith("/_workitems/edit/321", refreshed.ExternalUrl);
        Assert.Equal("本地任务", refreshed.Title);
        Assert.Equal(1, refreshed.Revision);
    }

    [Fact]
    public async Task UploadTodo_RejectsAlreadyLinkedTask()
    {
        using var harness = new Harness(
            ("""{"id":321,"rev":1,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var todo = harness.CreateTodo("本地任务");
        await harness.Ado.UploadTodoAsync(todo.Id);

        var second = await harness.Ado.UploadTodoAsync(todo.Id);

        Assert.False(second.Ok);
        Assert.Contains("已经关联", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkTodo_OnlyWritesExternalFields()
    {
        using var harness = new Harness((
            """
            {"id":66,"rev":7,"fields":{
              "System.WorkItemType":"Bug",
              "System.State":"活动",
              "System.Title":"云端 Bug",
              "System.TeamProject":"CloudProj"}}
            """,
            HttpStatusCode.OK));
        var todo = harness.CreateTodo("本地任务");

        var result = await harness.Ado.LinkTodoAsync(todo.Id, " #66 ");

        Assert.True(result.Ok);
        Assert.Equal("GET", harness.Handler.Methods[0]);
        var refreshed = harness.Host.Service.GetTodo(todo.Id)!;
        Assert.Equal("66", refreshed.ExternalId);
        Assert.Equal(7, refreshed.ExternalRev);
        Assert.Equal("本地任务", refreshed.Title);
        Assert.Equal(WorkItemKind.Task, refreshed.Kind);
        Assert.Equal(harness.Project.Id, refreshed.ProjectId);
    }

    [Fact]
    public async Task LinkTodo_RejectsWorkItemFromAnotherCloudProject()
    {
        using var harness = new Harness((
            """
            {"id":66,"rev":7,"fields":{
              "System.WorkItemType":"Bug",
              "System.State":"活动",
              "System.Title":"别人的 Bug",
              "System.TeamProject":"OtherProj"}}
            """,
            HttpStatusCode.OK));
        var todo = harness.CreateTodo("本地任务");

        var result = await harness.Ado.LinkTodoAsync(todo.Id, "66");

        Assert.False(result.Ok);
        Assert.Contains("OtherProj", result.Message, StringComparison.Ordinal);
        Assert.Contains("CloudProj", result.Message, StringComparison.Ordinal);
        Assert.Null(harness.Host.Service.GetTodo(todo.Id)!.ExternalId);
    }

    [Fact]
    public async Task LinkTodo_RejectsDuplicateLocalOwner()
    {
        using var harness = new Harness((
            """
            {"id":66,"rev":7,"fields":{
              "System.WorkItemType":"Bug",
              "System.State":"活动",
              "System.Title":"云端 Bug",
              "System.TeamProject":"CloudProj"}}
            """,
            HttpStatusCode.OK));
        var first = harness.CreateTodo("第一个任务");
        var second = harness.CreateTodo("第二个任务");
        Assert.True((await harness.Ado.LinkTodoAsync(first.Id, "66")).Ok);

        var result = await harness.Ado.LinkTodoAsync(second.Id, "66");

        Assert.False(result.Ok);
        Assert.Contains("已经关联到本地任务", result.Message, StringComparison.Ordinal);
        Assert.Null(harness.Host.Service.GetTodo(second.Id)!.ExternalId);
    }

    [Fact]
    public async Task PushStatus_ReadsLatestRevisionThenPatchesState()
    {
        using var harness = new Harness(
            (
                """
                {"id":66,"rev":9,"fields":{
                  "System.WorkItemType":"Bug",
                  "System.State":"新建",
                  "System.Title":"云端被改过的标题",
                  "System.TeamProject":"CloudProj"}}
                """,
                HttpStatusCode.OK),
            ("""{"id":66,"rev":10,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));
        var todo = harness.CreateTodo("本地任务");
        harness.Host.Service.SetTodoStatus(todo.Id, todo.Revision, TodoStatus.Doing, null, [], WriteContext.Manual());
        var linked = await harness.Ado.LinkTodoAsync(todo.Id, "66");

        // LinkTodo 已经消费了一次 GET，这里重新排队一次读 + 一次写。
        Assert.True(linked.Ok);
        harness.Handler.Reset(
            (
                """
                {"id":66,"rev":9,"fields":{
                  "System.WorkItemType":"Bug",
                  "System.State":"新建",
                  "System.Title":"云端被改过的标题",
                  "System.TeamProject":"CloudProj"}}
                """,
                HttpStatusCode.OK),
            ("""{"id":66,"rev":10,"fields":{"System.TeamProject":"CloudProj"}}""", HttpStatusCode.OK));

        var result = await harness.Ado.PushStatusAsync(todo.Id);

        Assert.True(result.Ok);
        Assert.Equal(10, result.ExternalRev);
        Assert.Equal(["GET", "PATCH"], harness.Handler.Methods);
        var refreshed = harness.Host.Service.GetTodo(todo.Id)!;
        Assert.Equal(10, refreshed.ExternalRev);
        Assert.Equal("本地任务", refreshed.Title);
        Assert.Equal(TodoStatus.Doing, refreshed.Status);

        using var document = JsonDocument.Parse(harness.Handler.Bodies[1]);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.DoesNotContain(
            "/fields/System.Title",
            harness.Handler.Bodies[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToAdoWorkItemTypes_MapsLocalKinds()
    {
        Assert.Equal(["Bug", "缺陷"], AdoMapper.ToAdoWorkItemTypes("bug"));
        Assert.Equal(["任务", "Task"], AdoMapper.ToAdoWorkItemTypes("task"));
        Assert.Equal(["任务", "Task"], AdoMapper.ToAdoWorkItemTypes(null));
    }

    private sealed class Harness : IDisposable
    {
        public Harness(params (string Json, HttpStatusCode Status)[] responses)
        {
            Handler = new SequenceHandler(responses);
            Host = new TestHost();
            Host.Service.SaveAdoConnection(new AdoConnectionInput
            {
                Enabled = true,
                CollectionUrl = Collection,
                Pat = "pat",
            });
            Project = Host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());
            Host.Service.SaveAdoBinding(Project.Id, new AdoBindingInput { AdoProject = "CloudProj" });
            Ado = new AdoImportService(Host.Service, new AdoClient(new HttpClient(Handler)));
        }

        public TestHost Host { get; }

        public SequenceHandler Handler { get; }

        public ProjectDto Project { get; }

        public AdoImportService Ado { get; }

        public TodoDto CreateTodo(string title) =>
            Host.Service.CreateTodos(
                [new TodoCreateInput { Title = title, ProjectId = Project.Id }],
                WriteContext.Manual()).Items[0];

        public void Dispose() => Host.Dispose();
    }

    private sealed class SequenceHandler(params (string Json, HttpStatusCode Status)[] responses)
        : HttpMessageHandler
    {
        private List<(string Json, HttpStatusCode Status)> _responses = [.. responses];
        private int _index;

        public List<string> Methods { get; private set; } = [];

        public List<string> Uris { get; private set; } = [];

        public List<string> Bodies { get; private set; } = [];

        public void Reset(params (string Json, HttpStatusCode Status)[] next)
        {
            _responses = [.. next];
            _index = 0;
            Methods = [];
            Uris = [];
            Bodies = [];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            var (json, status) = _responses[Math.Min(_index, _responses.Count - 1)];
            _index++;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
