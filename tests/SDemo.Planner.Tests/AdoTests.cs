using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>0.6.0：Azure DevOps 只读镜像——映射规则与幂等导入。</summary>
public sealed class AdoTests
{
    [Theory]
    [InlineData("新建", TodoStatus.Open)]
    [InlineData("New", TodoStatus.Open)]
    [InlineData("活动", TodoStatus.Doing)]
    [InlineData("Active", TodoStatus.Doing)]
    [InlineData("已解决", TodoStatus.Done)]
    [InlineData("Resolved", TodoStatus.Done)]
    [InlineData("已关闭", TodoStatus.Closed)]
    [InlineData("Closed", TodoStatus.Closed)]
    [InlineData("已删除", TodoStatus.Canceled)]
    [InlineData("Removed", TodoStatus.Canceled)]
    [InlineData("未知状态", TodoStatus.Open)]
    [InlineData(null, TodoStatus.Open)]
    public void Status_MapsToPlannerStates(string? adoState, TodoStatus expected)
    {
        Assert.Equal(expected.ToString().ToLowerInvariant(), AdoMapper.ToStorageStatus(adoState));
    }

    [Theory]
    [InlineData("Bug", WorkItemKind.Bug)]
    [InlineData("bug", WorkItemKind.Bug)]
    [InlineData("任务", WorkItemKind.Task)]
    [InlineData("用户情景", WorkItemKind.Task)]
    [InlineData("Research", WorkItemKind.Research)]
    [InlineData("杂务", WorkItemKind.Chore)]
    [InlineData(null, WorkItemKind.Task)]
    public void Kind_MapsToWorkItemKind(string? adoType, WorkItemKind expected)
    {
        Assert.Equal(expected.ToString().ToLowerInvariant(), AdoMapper.ToStorageKind(adoType));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(null, 2)]
    [InlineData(9, 2)]
    public void Priority_MapsAdoToPlanner(int? adoPriority, int expected)
    {
        Assert.Equal(expected, AdoMapper.ToPriority(adoPriority));
    }

    [Fact]
    public void IterationStatus_FollowsDates()
    {
        var now = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("planned", AdoMapper.ToIterationStatus(now.AddDays(3), now.AddDays(10), now));
        Assert.Equal("active", AdoMapper.ToIterationStatus(now.AddDays(-3), now.AddDays(3), now));
        Assert.Equal("done", AdoMapper.ToIterationStatus(now.AddDays(-10), now.AddDays(-3), now));
        Assert.Equal("planned", AdoMapper.ToIterationStatus(null, null, now));
    }

    [Fact]
    public void HtmlToText_StripsMarkupAndDecodesEntities()
    {
        var text = AdoMapper.HtmlToText("<div>第一段</div><p>第二段 &amp; <b>加粗</b></p>");

        Assert.DoesNotContain("<", text);
        Assert.Contains("第一段", text);
        Assert.Contains("第二段 & 加粗", text);
    }

    [Fact]
    public void Connection_PatIsStoredButNeverReturned()
    {
        using var host = new TestHost();

        var saved = host.Service.SaveAdoConnection(new AdoConnectionInput
        {
            Enabled = true,
            CollectionUrl = "http://172.30.184.25:8088/Utilities/",
            Pat = "secret-pat",
        });

        Assert.Equal("http://172.30.184.25:8088/Utilities", saved.CollectionUrl);
        Assert.True(saved.HasPat);
        Assert.True(host.Service.GetAdoConnection().HasPat);
        Assert.Equal("secret-pat", host.Store.GetSetting("ado.pat"));

        // 再次保存不带 PAT：保留原值。
        host.Service.SaveAdoConnection(new AdoConnectionInput
        {
            Enabled = true,
            CollectionUrl = saved.CollectionUrl,
        });
        Assert.True(host.Service.GetAdoConnection().HasPat);

        // ClearPat 才清空。
        host.Service.SaveAdoConnection(new AdoConnectionInput
        {
            Enabled = true,
            CollectionUrl = saved.CollectionUrl,
            ClearPat = true,
        });
        Assert.False(host.Service.GetAdoConnection().HasPat);
        Assert.True(string.IsNullOrEmpty(host.Store.GetSetting("ado.pat")));
    }

    [Fact]
    public void ApplySnapshot_LinksLocalProjectAndCreatesIterationsAndTodos()
    {
        using var host = new TestHost();
        var local = host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());
        var snapshot = BuildSnapshot();

        var result = host.Service.ApplyAdoSnapshot(snapshot, local.Id);

        Assert.True(result.Ok);
        Assert.True(result.ProjectLinked);
        Assert.Equal(local.Id, result.ProjectId);
        Assert.Equal(2, result.IterationsCreated);
        Assert.Equal(2, result.TodosCreated);
        Assert.Empty(result.Warnings);

        var project = host.Service.GetProject(local.Id)!;
        Assert.Equal("本地项目", project.Name);
        Assert.Equal(AdoMapper.SourceName, project.ExternalSource);
        Assert.Equal("ado-project", project.ExternalId);
        Assert.EndsWith("dynabookAssistant-Agent", project.ExternalUrl);

        var iterations = host.Service.ListIterations(project.Id);
        var big = Assert.Single(iterations, i => i.Level == IterationLevel.Big);
        var small = Assert.Single(iterations, i => i.Level == IterationLevel.Small);
        Assert.Equal(big.Id, small.ParentId);
        Assert.Equal("3.0.1", small.Name);
        Assert.Equal(IterationStatus.Active, small.Status);

        var todos = host.Service.ListTodos(new TodoQuery { Status = "all", ProjectId = project.Id, Limit = 500 });
        var story = Assert.Single(todos, t => t.ExternalId == "59");
        Assert.Equal(TodoStatus.Open, story.Status);
        Assert.Equal(WorkItemKind.Task, story.Kind);
        Assert.Equal(4, story.Priority);
        Assert.Equal(small.Id, story.IterationId);
        Assert.Equal("azure-devops", story.ExternalSource);
        Assert.EndsWith("/_workitems/edit/59", story.ExternalUrl);

        var bug = Assert.Single(todos, t => t.ExternalId == "61");
        Assert.Equal(WorkItemKind.Bug, bug.Kind);
        Assert.Equal(TodoStatus.Closed, bug.Status);
        Assert.NotNull(bug.CompletedAt);
        Assert.Equal(story.Id, bug.ParentTodoId);
    }

    [Fact]
    public void ApplySnapshot_IsIdempotent_AndUpdatesOnlyChangedItems()
    {
        using var host = new TestHost();
        var local = host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());
        host.Service.ApplyAdoSnapshot(BuildSnapshot(), local.Id);

        var second = host.Service.ApplyAdoSnapshot(BuildSnapshot(), local.Id);
        Assert.False(second.ProjectLinked);
        Assert.Equal(0, second.IterationsCreated);
        Assert.Equal(0, second.IterationsUpdated);
        Assert.Equal(0, second.TodosCreated);
        Assert.Equal(0, second.TodosUpdated);
        Assert.Equal(2, second.TodosSkipped);

        var changed = BuildSnapshot(rev: 9, title: "改过的标题", state: "活动");
        var third = host.Service.ApplyAdoSnapshot(changed, local.Id);
        Assert.Equal(0, third.TodosCreated);
        Assert.Equal(1, third.TodosUpdated);

        var todos = host.Service.ListTodos(new TodoQuery { Status = "all", Limit = 500 });
        var bug = Assert.Single(todos, t => t.ExternalId == "61");
        Assert.Equal("改过的标题", bug.Title);
        Assert.Equal(TodoStatus.Doing, bug.Status);
        Assert.Null(bug.CompletedAt);

        // 手工任务不受同步影响。
        var manual = host.Service.CreateTodos(
            [new TodoCreateInput { Title = "本地任务" }],
            WriteContext.Manual()).Items.Single();
        Assert.Null(manual.ExternalSource);
        host.Service.ApplyAdoSnapshot(BuildSnapshot(), local.Id);
        Assert.Equal("本地任务", host.Service.GetTodo(manual.Id)!.Title);
    }

    [Fact]
    public void ApplySnapshot_WarnsWhenParentIsMissing()
    {
        using var host = new TestHost();
        var local = host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());
        var snapshot = BuildSnapshot(parentExternalId: "20516");

        var result = host.Service.ApplyAdoSnapshot(snapshot, local.Id);

        Assert.Contains(result.Warnings, w => w.Contains("20516"));
        var todos = host.Service.ListTodos(new TodoQuery { Status = "all", ProjectId = local.Id, Limit = 500 });
        Assert.All(todos, t => Assert.Null(t.ParentTodoId));
    }

    [Fact]
    public void Binding_RoundTripsScopeAndClearsWithEmptyProject()
    {
        using var host = new TestHost();
        var local = host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());

        var saved = host.Service.SaveAdoBinding(local.Id, new AdoBindingInput
        {
            AdoProject = "dynabookAssistant-Agent",
            AssignedTo = "Xie, Huangteng",
            IterationPaths = [@"dynabookAssistant-Agent\3.0.1\", @"dynabookAssistant-Agent\迭代\3.0.1"],
            SkipClosed = true,
        });

        Assert.True(saved.Bound);
        Assert.Equal("dynabookAssistant-Agent", saved.AdoProject);
        Assert.Equal("Xie, Huangteng", saved.AssignedTo);
        Assert.True(saved.SkipClosed);
        // 规范化（去掉首尾反斜杠）+ 去重。
        Assert.Equal(
            [@"dynabookAssistant-Agent\3.0.1", @"dynabookAssistant-Agent\迭代\3.0.1"],
            saved.IterationPaths);

        var cleared = host.Service.SaveAdoBinding(local.Id, new AdoBindingInput { AdoProject = string.Empty });
        Assert.False(cleared.Bound);
        Assert.Empty(cleared.IterationPaths);
        Assert.False(cleared.SkipClosed);
    }

    [Fact]
    public async Task SyncProject_ReportsUnboundProjectWithoutTouchingTheNetwork()
    {
        using var host = new TestHost();
        var local = host.Service.CreateProject(new ProjectInput { Name = "本地项目" }, WriteContext.Manual());
        host.Service.SaveAdoConnection(new AdoConnectionInput
        {
            Enabled = true,
            CollectionUrl = "http://172.30.184.25:8088/Utilities",
            Pat = "secret-pat",
        });

        var import = new AdoImportService(host.Service, new AdoClient(new HttpClient(new StubHandler("{}"))));
        var result = await import.SyncProjectAsync(local.Id);

        Assert.False(result.Ok);
        Assert.Contains("绑定", result.Message);
    }

    private static AdoProjectSnapshot BuildSnapshot(
        int rev = 1,
        string title = "Agent 功能实现",
        string state = "已关闭",
        string? parentExternalId = "59")
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);
        const string root = @"\dynabookAssistant-Agent\迭代";
        const string child = @"\dynabookAssistant-Agent\迭代\3.0.1";

        return new AdoProjectSnapshot
        {
            ExternalId = "ado-project",
            Name = "dynabookAssistant-Agent",
            Url = "http://172.30.184.25:8088/Utilities/dynabookAssistant-Agent",
            Description = "DA 助手项目",
            Iterations =
            [
                new AdoIterationDto { ExternalId = "iter-root", Path = root, Name = "迭代" },
                new AdoIterationDto
                {
                    ExternalId = "iter-301",
                    Path = child,
                    ParentPath = root,
                    Name = "3.0.1",
                    StartAt = start,
                    EndAt = end,
                },
            ],
            WorkItems =
            [
                new AdoWorkItemDto
                {
                    ExternalId = "59",
                    Rev = 1,
                    Title = "Agent 功能实现",
                    Type = "用户情景",
                    State = "新建",
                    AdoPriority = 1,
                    IterationPath = child,
                    Url = "http://172.30.184.25:8088/Utilities/dynabookAssistant-Agent/_workitems/edit/59",
                },
                new AdoWorkItemDto
                {
                    ExternalId = "61",
                    Rev = rev,
                    Title = title,
                    Type = "Bug",
                    State = state,
                    AdoPriority = 2,
                    IterationPath = child,
                    ParentExternalId = parentExternalId,
                    ChangedAt = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero),
                    Url = "http://172.30.184.25:8088/Utilities/dynabookAssistant-Agent/_workitems/edit/61",
                },
            ],
        };
    }

    [Fact]
    public async Task ListProjects_ParsesCollectionAndSortsByName()
    {
        var handler = new StubHandler(
            """
            {"count":3,"value":[
              {"id":"b","name":"Zeta","state":"wellFormed"},
              {"id":"a","name":"alpha","state":"wellFormed"},
              {"id":"c","name":"dynabookAssistant-Agent","state":"wellFormed"}
            ]}
            """);
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.ListProjectsAsync("http://172.30.184.25:8088/Utilities/", "pat");

        Assert.True(result.Ok);
        Assert.Equal(["alpha", "dynabookAssistant-Agent", "Zeta"], result.Projects.Select(p => p.Name));
        Assert.Equal("a", result.Projects[0].ExternalId);
        Assert.Contains("api-version=5.1", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.StartsWith("http://172.30.184.25:8088/Utilities/_apis/projects", handler.LastRequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListProjects_ReportsHttpErrorWithoutThrowing()
    {
        var handler = new StubHandler("""{"message":"TF400813"}""", System.Net.HttpStatusCode.Unauthorized);
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.ListProjectsAsync("http://172.30.184.25:8088/Utilities", "bad");

        Assert.False(result.Ok);
        Assert.Empty(result.Projects);
        Assert.Contains("HTTP 401", result.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void BuildWiql_DefaultsToWholeProjectMinusRemoved()
    {
        var query = AdoClient.BuildWiql("dynabookAssistant-Agent", null);

        Assert.Contains("[System.TeamProject] = 'dynabookAssistant-Agent'", query);
        Assert.Contains("[System.State] <> 'Removed'", query);
        Assert.Contains("[System.State] <> '已删除'", query);
        Assert.DoesNotContain("AssignedTo", query);
        Assert.DoesNotContain("UNDER", query);
    }

    [Fact]
    public void BuildWiql_AppliesAssigneeIterationScopeAndSkipClosed()
    {
        var query = AdoClient.BuildWiql("dynabookAssistant-Agent", new AdoFetchOptions
        {
            AssignedTo = "@Me",
            IterationPaths = ["dynabookAssistant-Agent\\迭代\\3.0.1"],
            SkipClosed = true,
        });

        Assert.Contains("[System.AssignedTo] = @Me", query);
        // 工作项 IterationPath 不含「迭代」根节点，所以要按别名过滤。
        Assert.Contains("[System.IterationPath] = 'dynabookAssistant-Agent\\3.0.1'", query);
        Assert.Contains("[System.IterationPath] UNDER 'dynabookAssistant-Agent\\3.0.1'", query);
        Assert.DoesNotContain("迭代\\3.0.1'", query);
        Assert.Contains("[System.State] <> 'Closed'", query);
        Assert.Contains("[System.State] <> '已关闭'", query);
    }

    [Fact]
    public void BuildWiql_EscapesQuotesInAssigneeName()
    {
        var query = AdoClient.BuildWiql("P", new AdoFetchOptions { AssignedTo = "O'Brien" });

        Assert.Contains("[System.AssignedTo] = 'O''Brien'", query);
    }

    [Fact]
    public void FilterIterations_KeepsSelectedDescendantsAndAncestors()
    {
        static AdoIterationDto Node(string path, string? parent) => new()
        {
            ExternalId = path,
            Path = path,
            ParentPath = parent,
            Name = path.Split('\\')[^1],
        };

        var all = new List<AdoIterationDto>
        {
            Node("P\\迭代", null),
            Node("P\\迭代\\迭代 1", "P\\迭代"),
            Node("P\\迭代\\迭代 2", "P\\迭代"),
            Node("P\\迭代\\3.0.1", "P\\迭代"),
            Node("P\\迭代\\3.0.1\\Sprint 1", "P\\迭代\\3.0.1"),
            Node("P\\迭代\\4.0", "P\\迭代"),
        };

        var filtered = AdoClient.FilterIterations(all, ["P\\迭代\\3.0.1"]);

        Assert.Equal(
            ["P\\迭代", "P\\迭代\\3.0.1", "P\\迭代\\3.0.1\\Sprint 1"],
            filtered.Select(item => item.Path).ToArray());

        // 不选 = 全部。
        Assert.Equal(all.Count, AdoClient.FilterIterations(all, []).Count);
    }

    [Fact]
    public void ApplySnapshot_RespectsDeclaredIterationPathsOnlyThroughClientFilter()
    {
        // 服务端不关心范围（客户端已经过滤）；这里只锁住路径别名匹配，防止漏同步某迭代的任务。
        Assert.True(AdoMapper.IterationMatches(["P\\迭代\\3.0.1"], "P\\3.0.1"));
        Assert.True(AdoMapper.IterationMatches(["P\\迭代\\3.0.1"], "P\\3.0.1\\Sprint 1"));
        Assert.False(AdoMapper.IterationMatches(["P\\迭代\\3.0.1"], "P\\3.0.2"));
        Assert.True(AdoMapper.IterationMatches([], "P\\3.0.2"));
        Assert.False(AdoMapper.IterationMatches(["P\\迭代\\3.0.1"], null));
    }

    [Fact]
    public void Push_MapsStatusPriorityAndNotesBackToAdo()
    {
        Assert.Equal(["活动", "Active"], AdoMapper.ToAdoStates("doing"));
        Assert.Equal(["已关闭", "Closed"], AdoMapper.ToAdoStates("closed"));
        Assert.Equal(["新建", "New"], AdoMapper.ToAdoStates(null));

        Assert.Equal(1, AdoMapper.ToAdoPriority(4));
        Assert.Equal(4, AdoMapper.ToAdoPriority(1));
        Assert.Equal(3, AdoMapper.ToAdoPriority(2));
        Assert.Equal(3, AdoMapper.ToAdoPriority(99));

        Assert.Equal("a &amp; b &lt;c&gt;<br />第二行<br />第三行",
            AdoMapper.ToAdoHtml("a & b <c>\r\n第二行\r第三行"));
    }

    [Fact]
    public async Task UpdateWorkItem_SendsJsonPatchWithRevisionGuard()
    {
        var handler = new StubHandler(
            """
            {"id":66,"rev":9,"fields":{"System.TeamProject":"dynabookAssistant-Agent"}}
            """);
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.UpdateWorkItemAsync(
            "http://172.30.184.25:8088/Utilities",
            "pat",
            66,
            "新标题",
            "第一行\n第二行",
            "doing",
            4,
            8);

        Assert.True(result.Ok);
        Assert.Equal(9, result.ExternalRev);
        Assert.Equal("PATCH", handler.LastMethod);
        Assert.Equal("application/json-patch+json", handler.LastContentType);
        Assert.Contains("/_apis/wit/workitems/66?api-version=5.1", handler.LastRequestUri, StringComparison.Ordinal);

        using var document = System.Text.Json.JsonDocument.Parse(handler.LastBody);
        var ops = document.RootElement.EnumerateArray().ToList();
        Assert.Equal("test", ops[0].GetProperty("op").GetString());
        Assert.Equal("/rev", ops[0].GetProperty("path").GetString());
        Assert.Equal(8, ops[0].GetProperty("value").GetInt32());

        var fields = ops.Skip(1).ToDictionary(
            op => op.GetProperty("path").GetString()!,
            op => op.GetProperty("value"),
            StringComparer.Ordinal);
        Assert.Equal("新标题", fields["/fields/System.Title"].GetString());
        Assert.Equal("第一行<br />第二行", fields["/fields/System.Description"].GetString());
        Assert.Equal(1, fields["/fields/Microsoft.VSTS.Common.Priority"].GetInt32());
    }

    [Fact]
    public async Task UpdateWorkItem_ReportsConflictWhenRevisionIsStale()
    {
        var handler = new StubHandler(
            """{"message":"TF401175:The version of the work item is not the latest."}""",
            System.Net.HttpStatusCode.Conflict);
        var client = new AdoClient(new HttpClient(handler));

        var result = await client.UpdateWorkItemAsync(
            "http://172.30.184.25:8088/Utilities", "pat", 66, "t", null, "doing", 2, 3);

        Assert.False(result.Ok);
        Assert.Contains("已经被改过", result.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public string LastRequestUri { get; private set; } = string.Empty;

        public string LastMethod { get; private set; } = string.Empty;

        public string LastBody { get; private set; } = string.Empty;

        public string LastContentType { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri?.ToString() ?? string.Empty;
            LastMethod = request.Method.Method;
            LastContentType = request.Content?.Headers.ContentType?.MediaType ?? string.Empty;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            return response;
        }
    }
}

