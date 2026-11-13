using System.Net;
using System.Text;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;

namespace SDemo.Planner.Tests;

/// <summary>0.7.0：不先建本地项目，直接按「迭代 + 指派人」拉取 ADO 项目。</summary>
public sealed class AdoPullTests
{
    private const string Collection = "http://ado.test/Utilities";
    private const string CloudProject = "dynabookAssistant-Agent";

    [Fact]
    public async Task PullProject_CreatesLocalProjectAndImportsScopedWorkItems()
    {
        using var host = new TestHost();
        var handler = new AdoStubHandler();
        var service = CreateService(host, handler, enabled: true);
        var selectedIteration = CloudProject + "\\迭代\\3.0";

        var result = await service.PullProjectAsync(new AdoPullProjectInput
        {
            AdoProject = CloudProject,
            AssignedTo = "Xie, Huangteng",
            IterationPaths = [selectedIteration],
            SkipClosed = true,
        });

        Assert.True(result.Ok, result.Message);
        Assert.True(result.ProjectCreated);
        Assert.NotNull(result.ProjectId);

        var project = host.Service.GetProject(result.ProjectId!);
        Assert.NotNull(project);
        Assert.Equal(CloudProject, project!.Name);
        Assert.Equal(AdoMapper.SourceName, project.ExternalSource);
        Assert.Equal("ado-proj-1", project.ExternalId);

        var binding = host.Service.GetAdoBinding(project.Id);
        Assert.True(binding.Bound);
        Assert.Equal(CloudProject, binding.AdoProject);
        Assert.Equal("Xie, Huangteng", binding.AssignedTo);
        Assert.True(binding.SkipClosed);
        Assert.Contains(selectedIteration, binding.IterationPaths);

        var iterations = host.Service.ListIterations(project.Id);
        Assert.Contains(iterations, item => item.Name == "3.0");
        Assert.Contains(iterations, item => item.Name == "3.0.1");

        var todos = host.Service.ListTodos(new TodoQuery { Status = "all", ProjectId = project.Id, Limit = 50 });
        Assert.Equal(2, todos.Count);
        Assert.All(todos, todo => Assert.Equal(AdoMapper.SourceName, todo.ExternalSource));

        // 范围确实带进了 WIQL：指派人 + 迭代节点 + 排除已关闭。
        Assert.Contains("Xie, Huangteng", handler.LastWiqlBody, StringComparison.Ordinal);
        Assert.Contains("3.0", handler.LastWiqlBody, StringComparison.Ordinal);
        Assert.Contains("Closed", handler.LastWiqlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullProject_RejectsSameNameUnboundLocalProject()
    {
        using var host = new TestHost();
        var existing = host.Service.CreateProject(new ProjectInput { Name = CloudProject }, WriteContext.Manual());
        var service = CreateService(host, new AdoStubHandler(), enabled: true);

        var result = await service.PullProjectAsync(new AdoPullProjectInput { AdoProject = CloudProject });

        Assert.False(result.Ok);
        Assert.Contains("同名项目", result.Message, StringComparison.Ordinal);
        var projects = host.Service.ListProjects();
        Assert.Single(projects);
        Assert.Equal(existing.Id, projects[0].Id);
        Assert.False(host.Service.GetAdoBinding(existing.Id).Bound);
    }

    [Fact]
    public async Task PullProject_ReusesPreviouslyLinkedProject()
    {
        using var host = new TestHost();
        var service = CreateService(host, new AdoStubHandler(), enabled: true);

        var first = await service.PullProjectAsync(new AdoPullProjectInput { AdoProject = CloudProject });
        var second = await service.PullProjectAsync(new AdoPullProjectInput { AdoProject = CloudProject });

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        Assert.False(second.ProjectCreated);
        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Single(host.Service.ListProjects());
        Assert.Equal(2, host.Service.ListTodos(
            new TodoQuery { Status = "all", ProjectId = first.ProjectId, Limit = 50 }).Count);
    }

    [Fact]
    public async Task PullProject_FailsWhenConnectionDisabled()
    {
        using var host = new TestHost();
        var handler = new AdoStubHandler();
        var service = CreateService(host, handler, enabled: false);

        var result = await service.PullProjectAsync(new AdoPullProjectInput { AdoProject = CloudProject });

        Assert.False(result.Ok);
        Assert.Contains("关闭", result.Message, StringComparison.Ordinal);
        Assert.Null(result.ProjectId);
        Assert.Empty(handler.Requests);
        Assert.Empty(host.Service.ListProjects());
    }

    private static AdoImportService CreateService(TestHost host, HttpMessageHandler handler, bool enabled)
    {
        host.Service.SaveAdoConnection(new AdoConnectionInput
        {
            Enabled = enabled,
            CollectionUrl = Collection,
            Pat = "pat-for-tests",
        });

        return new AdoImportService(host.Service, new AdoClient(new HttpClient(handler)));
    }

    /// <summary>按 URL 路由的假 ADO：项目信息、迭代树、WIQL、工作项详情。</summary>
    private sealed class AdoStubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public string LastWiqlBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add(uri);

            if (uri.Contains("/classificationnodes/iterations", StringComparison.OrdinalIgnoreCase))
            {
                return Json(IterationsJson);
            }

            if (uri.Contains("/_apis/wit/wiql", StringComparison.OrdinalIgnoreCase))
            {
                LastWiqlBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
                return Json("""{"workItems":[{"id":101},{"id":102}]}""");
            }

            if (uri.Contains("/_apis/wit/workitems", StringComparison.OrdinalIgnoreCase))
            {
                return Json(WorkItemsJson);
            }

            if (uri.Contains("/_apis/projects/", StringComparison.OrdinalIgnoreCase))
            {
                return Json($$"""
                    {
                      "id": "ado-proj-1",
                      "name": "{{CloudProject}}",
                      "description": "云端项目说明"
                    }
                    """);
            }

            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private const string IterationsJson = """
            {
              "path": "dynabookAssistant-Agent\\迭代",
              "name": "迭代",
              "identifier": "node-root",
              "children": [
                {
                  "path": "dynabookAssistant-Agent\\迭代\\3.0",
                  "name": "3.0",
                  "identifier": "node-30",
                  "attributes": { "startDate": "2026-10-01T00:00:00Z", "finishDate": "2026-11-30T00:00:00Z" },
                  "children": [
                    {
                      "path": "dynabookAssistant-Agent\\迭代\\3.0\\3.0.1",
                      "name": "3.0.1",
                      "identifier": "node-301",
                      "children": []
                    }
                  ]
                }
              ]
            }
            """;

        private const string WorkItemsJson = """
            {
              "value": [
                {
                  "id": 101,
                  "rev": 3,
                  "fields": {
                    "System.Title": "云端任务一",
                    "System.State": "Active",
                    "System.WorkItemType": "任务",
                    "System.IterationPath": "dynabookAssistant-Agent\\3.0",
                    "System.AssignedTo": { "displayName": "Xie, Huangteng" },
                    "Microsoft.VSTS.Common.Priority": 2,
                    "System.Description": "<div>说明一</div>"
                  }
                },
                {
                  "id": 102,
                  "rev": 1,
                  "fields": {
                    "System.Title": "云端 Bug",
                    "System.State": "New",
                    "System.WorkItemType": "Bug",
                    "System.IterationPath": "dynabookAssistant-Agent\\3.0\\3.0.1",
                    "System.AssignedTo": { "displayName": "Xie, Huangteng" }
                  }
                }
              ]
            }
            """;
    }
}
