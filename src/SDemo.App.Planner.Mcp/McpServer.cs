using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Mcp;

/// <summary>
/// MCP Streamable HTTP 的 JSON-RPC 处理核心（传输在 Host 的 AppServer 里）。
/// 只实现工具面需要的子集：initialize / ping / tools/list / tools/call，其余方法返回 -32601。
///
/// 三件事必须由这里保证：
/// 1. <b>来源由服务端注入</b>：source 永远是 ai/meeting，来自 <c>params._meta</c>，模型无法自称 manual；
/// 2. <b>审批所需的风险元数据随 tools/list 下发</b>，Agent 只按等级执行；
/// 3. <b>每次调用落审计</b>，参数只存截断摘要，不存备注全文。
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] KnownProtocolVersions =
        ["2024-11-05", "2025-03-26", "2025-06-18"];

    private readonly PlannerService _service;
    private readonly PlannerStore _store;

    public McpServer(PlannerService service)
    {
        _service = service;
        _store = service.Store;
    }

    /// <summary>
    /// 处理一条 JSON-RPC 消息（可能是批量数组）。返回要写回 HTTP 的 JSON 字符串；
    /// 空字符串表示「只有通知、无需响应」（HTTP 层回 202）。
    /// </summary>
    public string HandleJson(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, -32700, "Parse error: 请求体不是合法 JSON").ToJsonString();
        }

        if (root is JsonArray array)
        {
            var results = new JsonArray();
            foreach (var item in array)
            {
                var response = HandleSingle(item);
                if (response is not null)
                {
                    results.Add(response);
                }
            }

            return results.Count == 0 ? string.Empty : results.ToJsonString();
        }

        return HandleSingle(root)?.ToJsonString() ?? string.Empty;
    }

    private JsonObject? HandleSingle(JsonNode? node)
    {
        if (node is not JsonObject request)
        {
            return ErrorResponse(null, -32600, "Invalid Request：不是 JSON-RPC 对象");
        }

        var id = request["id"]?.DeepClone();
        var methodNode = request["method"];
        if (methodNode is null || methodNode.GetValueKind() != JsonValueKind.String)
        {
            return id is null ? null : ErrorResponse(id, -32600, "Invalid Request：缺少 method");
        }

        var method = methodNode.GetValue<string>();
        var isNotification = id is null;

        try
        {
            switch (method)
            {
                case "initialize":
                    return isNotification ? null : RpcResult(id, InitializeResult(request["params"]));

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    return null;

                case "ping":
                    return isNotification ? null : RpcResult(id, new JsonObject());

                case "tools/list":
                    return isNotification ? null : RpcResult(id, ToolsListResult());

                case "tools/call":
                    return isNotification
                        ? null
                        : RpcResult(id, HandleToolCall(request["params"] as JsonObject, requestId: NewRequestId()));

                default:
                    return isNotification
                        ? null
                        : ErrorResponse(id, -32601, $"Method not found：{method}");
            }
        }
        catch (Exception ex)
        {
            return isNotification
                ? null
                : ErrorResponse(id, -32603, $"Internal error：{ex.GetType().Name}");
        }
    }

    private static JsonObject InitializeResult(JsonNode? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValueKind() == JsonValueKind.String
            ? parameters["protocolVersion"]!.GetValue<string>()
            : ProtocolVersion;

        var negotiated = KnownProtocolVersions.Contains(requested, StringComparer.Ordinal)
            ? requested
            : ProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = PlannerConstants.AppId,
                ["title"] = "待办与日历",
                ["version"] = PlannerConstants.AppVersion,
            },
            ["instructions"] =
                "sdemo.planner 提供跨会话持久待办（todo_*），与会话级 todo_write 不是一回事。"
                + "写操作请带 expectedRevision；删除/清空/批量≥5 需要用户确认。"
                + "时间参数必须带时区偏移（例如 2026-11-01T18:00:00+08:00）。",
        };
    }

    private JsonObject ToolsListResult()
    {
        var settings = _service.GetMcpSettings();
        var enabled = settings.Tools.Where(t => t.Enabled).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var tools = new JsonArray();
        foreach (var tool in McpToolCatalog.Tools)
        {
            if (settings.Enabled && enabled.Contains(tool.Name))
            {
                tools.Add(tool.ToJson());
            }
        }

        return new JsonObject { ["tools"] = tools };
    }

    private JsonObject HandleToolCall(JsonObject? parameters, string requestId)
    {
        var started = Stopwatch.StartNew();
        var name = parameters?["name"]?.GetValueKind() == JsonValueKind.String
            ? parameters["name"]!.GetValue<string>()
            : null;
        var arguments = parameters?["arguments"] as JsonObject;
        var meta = parameters?["_meta"] as JsonObject;

        var tool = McpToolCatalog.Find(name ?? string.Empty);
        if (tool is null)
        {
            var unknown = ErrorEnvelope(requestId, ErrorCodes.UnknownTool, $"未知工具「{name}」", "先 tools/list 看可用工具");
            RecordAudit(name ?? "(null)", arguments, 0, ErrorCodes.UnknownTool, started.ElapsedMilliseconds, requestId);
            return ToolResult(unknown, isError: true);
        }

        var settings = _service.GetMcpSettings();
        if (!settings.Enabled)
        {
            var disabled = ErrorEnvelope(
                requestId,
                ErrorCodes.McpDisabled,
                "MCP 已在设置中关闭",
                "打开 设置 → MCP 服务，再重试");
            RecordAudit(tool.Name, arguments, 0, ErrorCodes.McpDisabled, started.ElapsedMilliseconds, requestId);
            return ToolResult(disabled, isError: true);
        }

        if (settings.Tools.FirstOrDefault(t => string.Equals(t.Name, tool.Name, StringComparison.Ordinal))?.Enabled != true)
        {
            var disabled = ErrorEnvelope(
                requestId,
                ErrorCodes.McpToolDisabled,
                $"工具「{tool.Name}」已在设置中关闭",
                "打开 设置 → MCP 服务，启用该工具后重试");
            RecordAudit(tool.Name, arguments, 0, ErrorCodes.McpToolDisabled, started.ElapsedMilliseconds, requestId);
            return ToolResult(disabled, isError: true);
        }

        var context = BuildWriteContext(meta, requestId);
        var paramSummary = Summarize(arguments);

        JsonObject envelope;
        var itemCount = 0;
        try
        {
            envelope = tool.Name switch
            {
                "todo_list" => HandleList(arguments, requestId),
                "todo_get" => HandleGet(arguments, requestId),
                "todo_create" => HandleCreate(arguments, context, requestId),
                "todo_update" => HandleUpdate(arguments, context, requestId),
                "todo_complete" => HandleComplete(arguments, context, requestId, complete: true),
                "todo_reopen" => HandleComplete(arguments, context, requestId, complete: false),
                "todo_delete" => HandleDelete(arguments, context, requestId),
                "todo_bulk_update" => HandleBulkUpdate(arguments, context, requestId),
                "todo_clear_list" => HandleClearList(arguments, context, requestId),
                "todo_plan_from_text" => HandlePlan(arguments, context, requestId),
                "todo_accept_draft" => HandleAcceptDraft(arguments, context, requestId),
                "todo_search" => HandleSearch(arguments, requestId),
                "todo_stats" => HandleStats(requestId),
                _ => ErrorEnvelope(requestId, ErrorCodes.UnknownTool, $"未知工具「{tool.Name}」", null),
            };

            itemCount = CountItems(arguments, tool.Name);
            if (envelope["data"]?["items"] is JsonArray dataItems)
            {
                itemCount = Math.Max(itemCount, dataItems.Count);
            }

            if (envelope["ok"]!.GetValue<bool>() && tool.Writes && arguments?["clientRequestId"] is { } idNode
                && idNode.GetValueKind() == JsonValueKind.String)
            {
                _store.SaveIdempotency(idNode.GetValue<string>(), envelope.ToJsonString());
            }
        }
        catch (PlannerException ex)
        {
            envelope = ErrorEnvelope(requestId, ex.Code, ex.Message, ex.Hint, ex.Details);
        }
        catch (Exception ex)
        {
            envelope = ErrorEnvelope(requestId, ErrorCodes.Internal, $"处理失败：{ex.Message}", "稍后重试；持续失败请报告", null);
        }
        finally
        {
            started.Stop();
        }

        var isError = !envelope["ok"]!.GetValue<bool>();
        var resultCode = isError
            ? envelope["code"]?.GetValue<string>() ?? ErrorCodes.Internal
            : "OK";
        RecordAudit(tool.Name, arguments, itemCount, resultCode, started.ElapsedMilliseconds, requestId);

        return ToolResult(envelope, isError);
    }

    // ── 各工具实现 ────────────────────────────────────────────────────────

    private JsonObject HandleList(JsonObject? arguments, string requestId)
    {
        var args = Deserialize<TodoListArgs>(arguments);
        var items = _service.ListTodos(new TodoQuery
        {
            Status = args.Status,
            ListId = args.ListId,
            DueBefore = args.DueBefore,
            DueAfter = args.DueAfter,
            Tag = args.Tag,
            Q = args.Q,
            Limit = args.Limit,
        });

        return Success(requestId, new JsonObject
        {
            ["items"] = JsonSerializer.SerializeToNode(items, PlannerJson.Wire),
            ["count"] = items.Count,
        });
    }

    private JsonObject HandleGet(JsonObject? arguments, string requestId)
    {
        var args = Deserialize<TodoGetArgs>(arguments);
        if (string.IsNullOrWhiteSpace(args.Id))
        {
            throw PlannerException.Validation("id 不能为空");
        }

        var item = _service.GetTodo(args.Id) ?? throw PlannerException.NotFound($"没有找到待办「{args.Id}」");
        var now = DateTimeOffset.UtcNow;
        var linkedEvents = _service.ListEvents(now, now.AddDays(90))
            .Where(e => string.Equals(e.LinkedTodoId, item.Id, StringComparison.Ordinal))
            .Take(5)
            .ToList();

        return Success(requestId, new JsonObject
        {
            ["item"] = JsonSerializer.SerializeToNode(item, PlannerJson.Wire),
            ["events"] = JsonSerializer.SerializeToNode(linkedEvents, PlannerJson.Wire),
        }, revision: item.Revision);
    }

    private JsonObject HandleCreate(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoCreateArgs>(arguments);
        if (args.Items.Count == 0)
        {
            throw PlannerException.Validation("items 至少一条", "按 items[] 传 {title, notes?, dueAt?, priority?, listId?, tags?, links?}");
        }

        if (!string.IsNullOrWhiteSpace(args.ClientRequestId))
        {
            var cached = _store.TryGetIdempotency(args.ClientRequestId);
            if (cached is not null)
            {
                return JsonNode.Parse(cached)!.AsObject();
            }
        }

        var inputs = args.Items.Select(item => new TodoCreateInput
        {
            Title = item.Title,
            Notes = item.Notes,
            DueAt = item.DueAt,
            Priority = item.Priority,
            ListId = item.ListId,
            Tags = item.Tags,
            Links = item.Links,
        }).ToList();

        var writeContext = context with { ClientRequestId = args.ClientRequestId };
        var result = _service.CreateTodos(inputs, writeContext);

        return Success(
            requestId,
            new JsonObject
            {
                ["items"] = JsonSerializer.SerializeToNode(result.Items, PlannerJson.Wire),
            },
            revision: null,
            warnings: result.Warnings);
    }

    private JsonObject HandleUpdate(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoUpdateArgs>(arguments);
        var revision = RequireRevision(args.ExpectedRevision);
        var result = _service.UpdateTodo(args.Id, revision, args.Patch, context);

        return Success(
            requestId,
            JsonSerializer.SerializeToNode(result.Item, PlannerJson.Wire),
            revision: result.Item.Revision,
            warnings: result.Warnings);
    }

    private JsonObject HandleComplete(JsonObject? arguments, WriteContext context, string requestId, bool complete)
    {
        var args = Deserialize<TodoCompleteArgs>(arguments);
        var revision = RequireRevision(args.ExpectedRevision);
        var result = complete
            ? _service.CompleteTodo(args.Id, revision, context)
            : _service.ReopenTodo(args.Id, revision, context);

        return Success(
            requestId,
            JsonSerializer.SerializeToNode(result.Item, PlannerJson.Wire),
            revision: result.Item.Revision,
            warnings: result.Warnings);
    }

    private JsonObject HandleDelete(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoDeleteArgs>(arguments);
        var revision = RequireRevision(args.ExpectedRevision);
        var result = _service.DeleteTodo(args.Id, revision, args.Reason, context);

        return Success(requestId, new JsonObject
        {
            ["id"] = result.Id,
            ["deletedAt"] = result.DeletedAt.ToString("O"),
            ["restorableUntil"] = result.RestorableUntil.ToString("O"),
            ["revision"] = result.Revision,
        });
    }

    private JsonObject HandleBulkUpdate(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoBulkUpdateArgs>(arguments);
        if (args.Ids.Count == 0)
        {
            throw PlannerException.Validation("ids 至少一条");
        }

        var result = _service.BulkUpdate(args.Ids, args.Patch, context);
        return Success(requestId, new JsonObject
        {
            ["updated"] = result.Updated,
            ["items"] = JsonSerializer.SerializeToNode(result.Items, PlannerJson.Wire),
            ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, PlannerJson.Wire),
        });
    }

    private JsonObject HandleClearList(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoClearListArgs>(arguments);
        if (string.IsNullOrWhiteSpace(args.ListId))
        {
            throw PlannerException.Validation("listId 不能为空");
        }

        var result = _service.ClearList(args.ListId, context);
        return Success(requestId, new JsonObject
        {
            ["deleted"] = result.Deleted,
            ["listId"] = result.ListId,
        });
    }

    private JsonObject HandlePlan(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoPlanFromTextArgs>(arguments);
        var draft = _service.PlanFromText(args.Text, args.Hint, args.ListId, context);

        return Success(requestId, JsonSerializer.SerializeToNode(draft, PlannerJson.Wire));
    }

    private JsonObject HandleAcceptDraft(JsonObject? arguments, WriteContext context, string requestId)
    {
        var args = Deserialize<TodoAcceptDraftArgs>(arguments);
        if (string.IsNullOrWhiteSpace(args.DraftId))
        {
            throw PlannerException.Validation("draftId 不能为空");
        }

        var request = new DraftAcceptRequest
        {
            DraftId = args.DraftId,
            ListId = args.ListId,
            Items = args.Items?.Select(i => new DraftAcceptItem
            {
                DraftItemId = i.DraftItemId,
                Reject = i.Reject,
                Title = i.Title,
                Notes = i.Notes,
                DueAt = i.DueAt,
                Priority = i.Priority,
            }).ToList(),
        };

        var result = _service.AcceptDraft(request, context);
        return Success(requestId, new JsonObject
        {
            ["draftId"] = result.DraftId,
            ["accepted"] = result.Accepted,
            ["items"] = JsonSerializer.SerializeToNode(result.Items, PlannerJson.Wire),
            ["rejected"] = result.Rejected,
        }, warnings: result.Warnings);
    }

    private JsonObject HandleSearch(JsonObject? arguments, string requestId)
    {
        var args = Deserialize<TodoSearchArgs>(arguments);
        var result = _service.Search(args.Q, args.Limit);
        return Success(requestId, JsonSerializer.SerializeToNode(result, PlannerJson.Wire));
    }

    private JsonObject HandleStats(string requestId) =>
        Success(requestId, JsonSerializer.SerializeToNode(_service.Stats(), PlannerJson.Wire));

    // ── 信封 / 审计 ──────────────────────────────────────────────────────

    private static WriteContext BuildWriteContext(JsonObject? meta, string requestId) => new()
    {
        Source = TodoSource.Ai,
        SessionId = meta?["sdemo/sourceSessionId"]?.GetValueKind() == JsonValueKind.String
            ? meta["sdemo/sourceSessionId"]!.GetValue<string>()
            : null,
        Excerpt = meta?["sdemo/sourceExcerpt"]?.GetValueKind() == JsonValueKind.String
            ? meta["sdemo/sourceExcerpt"]!.GetValue<string>()
            : null,
        RequestId = requestId,
    };

    private void RecordAudit(
        string tool,
        JsonObject? arguments,
        int itemCount,
        string resultCode,
        long durationMs,
        string requestId)
    {
        try
        {
            _store.RecordMcpAudit(tool, Summarize(arguments), itemCount, resultCode, durationMs, requestId);
        }
        catch (Exception)
        {
            // 审计失败不能吞掉工具结果；MCP 的可用性优先。
        }
    }

    private static string Summarize(JsonObject? arguments)
    {
        if (arguments is null)
        {
            return "(no args)";
        }

        var raw = arguments.ToJsonString().Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return raw.Length > 240 ? raw[..240] + "…" : raw;
    }

    private static int CountItems(JsonObject? arguments, string toolName)
    {
        if (arguments is null)
        {
            return 0;
        }

        return toolName switch
        {
            "todo_create" => arguments["items"] is JsonArray items ? items.Count : 0,
            "todo_bulk_update" => arguments["ids"] is JsonArray ids ? ids.Count : 0,
            "todo_accept_draft" => arguments["items"] is JsonArray draftItems ? draftItems.Count : 0,
            _ => 1,
        };
    }

    private static JsonObject Success(
        string requestId,
        JsonNode? data,
        int? revision = null,
        IReadOnlyList<string>? warnings = null)
    {
        var envelope = new JsonObject
        {
            ["ok"] = true,
            ["requestId"] = requestId,
            ["data"] = data ?? new JsonObject(),
        };

        if (revision is not null)
        {
            envelope["revision"] = revision.Value;
        }

        if (warnings is { Count: > 0 })
        {
            envelope["warnings"] = JsonSerializer.SerializeToNode(warnings, PlannerJson.Wire);
        }

        return envelope;
    }

    private static JsonObject ErrorEnvelope(
        string requestId,
        string code,
        string message,
        string? hint,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var envelope = new JsonObject
        {
            ["ok"] = false,
            ["requestId"] = requestId,
            ["code"] = code,
            ["message"] = message,
        };

        if (hint is not null)
        {
            envelope["hint"] = hint;
        }

        if (details is not null)
        {
            envelope["details"] = JsonSerializer.SerializeToNode(details, PlannerJson.Wire);
        }

        return envelope;
    }

    private static JsonObject ToolResult(JsonObject envelope, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = envelope.ToJsonString(),
        }),
        ["structuredContent"] = envelope.DeepClone(),
        ["isError"] = isError,
    };

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static T Deserialize<T>(JsonObject? arguments)
        where T : class, new()
    {
        try
        {
            return arguments is null
                ? new T()
                : arguments.Deserialize<T>(PlannerJson.Wire) ?? new T();
        }
        catch (JsonException ex)
        {
            throw PlannerException.Validation(
                $"参数不是合法 JSON：{ex.Message}",
                "按 inputSchema 传参；时间必须带时区偏移");
        }
    }

    private static int RequireRevision(int? revision) =>
        revision ?? throw PlannerException.Validation(
            "必须带 expectedRevision",
            "先 todo_get 拿到当前 revision，再带同一个值重试");

    private static string NewRequestId() => "r_" + Guid.NewGuid().ToString("N")[..16];
}
