using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Mcp;

namespace SDemo.Planner.Tests;

public sealed class McpServerTests
{
    private static JsonObject Request(string method, JsonNode? parameters = null, string id = "1") => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    private static JsonNode Call(McpServer server, string method, JsonNode? parameters = null)
    {
        var response = server.HandleJson(Request(method, parameters).ToJsonString());
        Assert.NotEmpty(response);
        return JsonNode.Parse(response)!;
    }

    private static JsonObject CallTool(McpServer server, string name, JsonNode? arguments, JsonObject? meta = null)
    {
        var parameters = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments?.DeepClone(),
        };

        if (meta is not null)
        {
            parameters["_meta"] = meta;
        }

        var response = Call(server, "tools/call", parameters);
        return response["result"]!["structuredContent"]!.AsObject();
    }

    [Fact]
    public void Initialize_ReturnsProtocolAndToolCapability()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var response = Call(server, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(McpServer.ProtocolVersion, response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.True(response["result"]!["capabilities"]!["tools"] is JsonObject);
        Assert.Equal(PlannerConstants.AppId, response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_ExposesThirteenToolsWithRiskMetadata()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var tools = Call(server, "tools/list")["result"]!["tools"]!.AsArray();

        Assert.Equal(13, tools.Count);
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "todo_create");
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "todo_search");

        var delete = tools.Single(t => t!["name"]!.GetValue<string>() == "todo_delete")!;
        Assert.Equal("high", delete["_meta"]!["sdemo/risk"]!.GetValue<string>());
        Assert.Equal("always", delete["_meta"]!["sdemo/approval"]!.GetValue<string>());
        Assert.True(delete["annotations"]!["destructiveHint"]!.GetValue<bool>());

        var bulk = tools.Single(t => t!["name"]!.GetValue<string>() == "todo_bulk_update")!;
        Assert.Equal("threshold", bulk["_meta"]!["sdemo/approval"]!.GetValue<string>());
        Assert.Equal(5, bulk["_meta"]!["sdemo/approvalThreshold"]!.GetValue<int>());

        var list = tools.Single(t => t!["name"]!.GetValue<string>() == "todo_list")!;
        Assert.True(list["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public void CreateListComplete_WorksThroughJsonRpc()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var create = CallTool(server, "todo_create", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["title"] = "把需求拆成三个里程碑",
                ["dueAt"] = "2026-11-03T18:00:00+08:00",
                ["priority"] = 3,
            }),
            ["clientRequestId"] = "req_create_1",
        }, new JsonObject
        {
            ["sdemo/sourceSessionId"] = "session_42",
            ["sdemo/sourceExcerpt"] = "帮我建一条「拆里程碑」的待办",
        });

        Assert.True(create["ok"]!.GetValue<bool>());
        var item = create["data"]!["items"]![0]!;
        var id = item["id"]!.GetValue<string>();
        Assert.Equal("ai", item["source"]!.GetValue<string>());
        Assert.Equal("session_42", item["sourceSessionId"]!.GetValue<string>());
        Assert.Equal(1, item["revision"]!.GetValue<int>());

        var list = CallTool(server, "todo_list", new JsonObject { ["status"] = "open" });
        Assert.Contains(list["data"]!["items"]!.AsArray(), t => t!["id"]!.GetValue<string>() == id);

        var complete = CallTool(server, "todo_complete", new JsonObject
        {
            ["id"] = id,
            ["expectedRevision"] = 1,
        });
        Assert.True(complete["ok"]!.GetValue<bool>());
        Assert.Equal("done", complete["data"]!["status"]!.GetValue<string>());
        Assert.Equal(2, complete["data"]!["revision"]!.GetValue<int>());
    }

    [Fact]
    public void Create_IsIdempotentWithinWindow()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);
        var arguments = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["title"] = "重复调用不应该建两条" }),
            ["clientRequestId"] = "idem_1",
        };

        var first = CallTool(server, "todo_create", arguments);
        var second = CallTool(server, "todo_create", arguments);

        Assert.Equal(
            first["data"]!["items"]![0]!["id"]!.GetValue<string>(),
            second["data"]!["items"]![0]!["id"]!.GetValue<string>());

        var list = CallTool(server, "todo_list", new JsonObject());
        Assert.Single(list["data"]!["items"]!.AsArray());
    }

    [Fact]
    public void MissingExpectedRevision_ReturnsValidationEnvelope()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var created = CallTool(server, "todo_create", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["title"] = "条目" }),
        });
        var id = created["data"]!["items"]![0]!["id"]!.GetValue<string>();

        var envelope = CallTool(server, "todo_complete", new JsonObject { ["id"] = id });

        Assert.False(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.Validation, envelope["code"]!.GetValue<string>());
        Assert.Contains("expectedRevision", envelope["message"]!.GetValue<string>());
    }

    [Fact]
    public void StaleRevision_ReturnsConflictWithCurrentRevision()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var created = CallTool(server, "todo_create", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["title"] = "条目" }),
        });
        var id = created["data"]!["items"]![0]!["id"]!.GetValue<string>();

        CallTool(server, "todo_complete", new JsonObject { ["id"] = id, ["expectedRevision"] = 1 });

        var conflict = CallTool(server, "todo_update", new JsonObject
        {
            ["id"] = id,
            ["expectedRevision"] = 1,
            ["patch"] = new JsonObject { ["title"] = "改个名" },
        });

        Assert.False(conflict["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.Conflict, conflict["code"]!.GetValue<string>());
        Assert.Equal(2, conflict["details"]!["currentRevision"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownTool_ReturnsErrorEnvelopeNotProtocolError()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var envelope = CallTool(server, "todo_nuke", new JsonObject());

        Assert.False(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.UnknownTool, envelope["code"]!.GetValue<string>());
    }

    [Fact]
    public void PlanFromText_ReturnsDraftAndAcceptWritesTodos()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var plan = CallTool(server, "todo_plan_from_text", new JsonObject
        {
            ["text"] = "1. 整理客户名单\n2. 准备报价单",
        }, new JsonObject { ["sdemo/sourceExcerpt"] = "帮我拆一下" });

        Assert.True(plan["ok"]!.GetValue<bool>());
        var draftId = plan["data"]!["draftId"]!.GetValue<string>();
        var draftItems = plan["data"]!["items"]!.AsArray();
        Assert.Equal(2, draftItems.Count);

        var accept = CallTool(server, "todo_accept_draft", new JsonObject { ["draftId"] = draftId });
        Assert.Equal(2, accept["data"]!["items"]!.AsArray().Count);

        var list = CallTool(server, "todo_list", new JsonObject());
        Assert.Equal(2, list["data"]!["items"]!.AsArray().Count);
    }

    [Fact]
    public void Search_ReturnsChineseHits()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);
        CallTool(server, "todo_create", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["title"] = "修复登录页异常" }),
        });

        var search = CallTool(server, "todo_search", new JsonObject { ["q"] = "登录" });

        Assert.True(search["ok"]!.GetValue<bool>());
        Assert.Single(search["data"]!["hits"]!.AsArray());
    }

    [Fact]
    public void Delete_ReturnsRestorableWindow()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);
        var created = CallTool(server, "todo_create", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["title"] = "待删" }),
        });
        var id = created["data"]!["items"]![0]!["id"]!.GetValue<string>();

        var deleted = CallTool(server, "todo_delete", new JsonObject
        {
            ["id"] = id,
            ["expectedRevision"] = 1,
            ["reason"] = "用户误建",
        });

        Assert.True(deleted["ok"]!.GetValue<bool>());
        var restorable = DateTimeOffset.Parse(deleted["data"]!["restorableUntil"]!.GetValue<string>());
        Assert.True(restorable > DateTimeOffset.UtcNow.AddDays(6));
    }

    [Fact]
    public void BatchRequest_ReturnsOnlyRequests()
    {
        using var host = new TestHost();
        var server = new McpServer(host.Service);

        var body = new JsonArray(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "a",
                ["method"] = "tools/list",
            },
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            }).ToJsonString();

        var response = server.HandleJson(body);
        var array = JsonNode.Parse(response)!.AsArray();

        Assert.Single(array);
        Assert.Equal("a", array[0]!["id"]!.GetValue<string>());
    }
}
