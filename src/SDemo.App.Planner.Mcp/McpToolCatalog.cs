using System.Text.Json.Nodes;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Mcp;

/// <summary>
/// 一个 MCP 工具的完整声明：schema + 风险等级 + 审批策略。风险等级**由应用声明**，
/// MainAgent 只按等级执行，不硬编码工具名（docs/integration.md §4）。
/// </summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson,
    RiskLevel Risk,
    ApprovalMode Approval,
    int? ApprovalThreshold,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool Writes)
{
    public JsonObject ToJson()
    {
        var meta = new JsonObject
        {
            ["sdemo/risk"] = Risk.ToString().ToLowerInvariant(),
            ["sdemo/approval"] = Approval.ToString().ToLowerInvariant(),
            ["sdemo/writes"] = Writes,
        };

        if (ApprovalThreshold is { } threshold)
        {
            meta["sdemo/approvalThreshold"] = threshold;
        }

        return new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = JsonNode.Parse(InputSchemaJson),
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
            },
            ["_meta"] = meta,
        };
    }
}

/// <summary>
/// 13 个 <c>todo_*</c> 工具（planner-mcp-surface §3）。<c>calendar_*</c> 留到 P3，
/// 与 DesktopAgent 联调时一起加，避免 P2 的工具面提前膨胀。
/// </summary>
public static class McpToolCatalog
{
    private const string TodoListSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "status": { "type": "string", "enum": ["open", "doing", "done", "all"], "default": "open" },
            "listId": { "type": "string" },
            "dueBefore": { "type": "string", "format": "date-time", "description": "ISO-8601，必须带时区偏移" },
            "dueAfter": { "type": "string", "format": "date-time", "description": "ISO-8601，必须带时区偏移" },
            "tag": { "type": "string" },
            "q": { "type": "string", "description": "标题/备注关键词" },
            "limit": { "type": "integer", "minimum": 1, "maximum": 50, "default": 20 }
          }
        }
        """;

    private const string TodoGetSchema = """
        {
          "type": "object",
          "required": ["id"],
          "additionalProperties": false,
          "properties": { "id": { "type": "string" } }
        }
        """;

    private const string TodoCreateSchema = """
        {
          "type": "object",
          "required": ["items"],
          "additionalProperties": false,
          "properties": {
            "items": {
              "type": "array",
              "minItems": 1,
              "maxItems": 5,
              "items": {
                "type": "object",
                "required": ["title"],
                "additionalProperties": false,
                "properties": {
                  "title": { "type": "string", "maxLength": 200 },
                  "notes": { "type": "string", "maxLength": 4000 },
                  "dueAt": { "type": "string", "format": "date-time", "description": "ISO-8601，必须带偏移" },
                  "priority": { "type": "integer", "minimum": 0, "maximum": 4, "default": 2 },
                  "listId": { "type": "string" },
                  "tags": { "type": "array", "maxItems": 10, "items": { "type": "string", "maxLength": 32 } },
                  "links": { "type": "array", "maxItems": 10, "items": { "type": "string" } }
                }
              }
            },
            "clientRequestId": { "type": "string", "description": "幂等键，10 分钟内重复即返回首次结果" }
          }
        }
        """;

    private const string TodoUpdateSchema = """
        {
          "type": "object",
          "required": ["id", "expectedRevision", "patch"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string" },
            "expectedRevision": { "type": "integer", "minimum": 1, "description": "乐观并发：先 todo_get 拿 revision" },
            "patch": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "title": { "type": "string", "maxLength": 200 },
                "notes": { "type": "string", "maxLength": 4000 },
                "clearNotes": { "type": "boolean" },
                "status": { "type": "string", "enum": ["open", "doing", "done"] },
                "priority": { "type": "integer", "minimum": 0, "maximum": 4 },
                "dueAt": { "type": "string", "format": "date-time" },
                "clearDueAt": { "type": "boolean" },
                "listId": { "type": "string" },
                "tags": { "type": "array", "maxItems": 10, "items": { "type": "string", "maxLength": 32 } },
                "links": { "type": "array", "maxItems": 10, "items": { "type": "string" } }
              }
            }
          }
        }
        """;

    private const string TodoCompleteSchema = """
        {
          "type": "object",
          "required": ["id", "expectedRevision"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string" },
            "expectedRevision": { "type": "integer", "minimum": 1 }
          }
        }
        """;

    private const string TodoDeleteSchema = """
        {
          "type": "object",
          "required": ["id", "expectedRevision", "reason"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string" },
            "expectedRevision": { "type": "integer", "minimum": 1 },
            "reason": { "type": "string", "maxLength": 256, "description": "删除原因，写入审计" }
          }
        }
        """;

    private const string TodoBulkUpdateSchema = """
        {
          "type": "object",
          "required": ["ids", "patch"],
          "additionalProperties": false,
          "properties": {
            "ids": { "type": "array", "minItems": 1, "maxItems": 50, "items": { "type": "string" } },
            "patch": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "title": { "type": "string", "maxLength": 200 },
                "notes": { "type": "string", "maxLength": 4000 },
                "clearNotes": { "type": "boolean" },
                "status": { "type": "string", "enum": ["open", "doing", "done"] },
                "priority": { "type": "integer", "minimum": 0, "maximum": 4 },
                "dueAt": { "type": "string", "format": "date-time" },
                "clearDueAt": { "type": "boolean" },
                "listId": { "type": "string" },
                "tags": { "type": "array", "maxItems": 10, "items": { "type": "string", "maxLength": 32 } },
                "links": { "type": "array", "maxItems": 10, "items": { "type": "string" } }
              }
            }
          }
        }
        """;

    private const string TodoClearListSchema = """
        {
          "type": "object",
          "required": ["listId"],
          "additionalProperties": false,
          "properties": { "listId": { "type": "string" } }
        }
        """;

    private const string TodoPlanFromTextSchema = """
        {
          "type": "object",
          "required": ["text"],
          "additionalProperties": false,
          "properties": {
            "text": { "type": "string", "maxLength": 20000 },
            "hint": { "type": "string", "description": "例如「按里程碑拆」「只要本周能做的」" },
            "listId": { "type": "string" }
          }
        }
        """;

    private const string TodoAcceptDraftSchema = """
        {
          "type": "object",
          "required": ["draftId"],
          "additionalProperties": false,
          "properties": {
            "draftId": { "type": "string" },
            "items": {
              "type": "array",
              "description": "逐条决策；省略则全部采纳",
              "items": {
                "type": "object",
                "required": ["draftItemId"],
                "additionalProperties": false,
                "properties": {
                  "draftItemId": { "type": "string" },
                  "reject": { "type": "boolean", "default": false },
                  "title": { "type": "string", "maxLength": 200 },
                  "notes": { "type": "string", "maxLength": 4000 },
                  "dueAt": { "type": "string", "format": "date-time" },
                  "priority": { "type": "integer", "minimum": 0, "maximum": 4 }
                }
              }
            },
            "listId": { "type": "string" }
          }
        }
        """;

    private const string TodoSearchSchema = """
        {
          "type": "object",
          "required": ["q"],
          "additionalProperties": false,
          "properties": {
            "q": { "type": "string", "maxLength": 200 },
            "limit": { "type": "integer", "minimum": 1, "maximum": 50, "default": 20 }
          }
        }
        """;

    private const string EmptySchema = """
        { "type": "object", "additionalProperties": false, "properties": {} }
        """;

    public static IReadOnlyList<McpToolDefinition> Tools { get; } =
    [
        new("todo_list", "查询待办条目。默认只返回未完成项；按截止时间与优先级排序。", TodoListSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: true, Destructive: false, Idempotent: true, Writes: false),
        new("todo_get", "取单条待办详情（含来源 provenance 与关联日程摘要）。", TodoGetSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: true, Destructive: false, Idempotent: true, Writes: false),
        new("todo_create", "创建 1–5 条待办。标题必填；时间必须带时区偏移。疑似完全重复会被拒绝，让用户裁决。", TodoCreateSchema,
            RiskLevel.Medium, ApprovalMode.Never, null, ReadOnly: false, Destructive: false, Idempotent: false, Writes: true),
        new("todo_update", "修改单条待办（标题/备注/状态/优先级/截止/清单/标签/引用）。必须带 expectedRevision，冲突不静默覆盖。", TodoUpdateSchema,
            RiskLevel.Medium, ApprovalMode.Never, null, ReadOnly: false, Destructive: false, Idempotent: false, Writes: true),
        new("todo_complete", "勾完成（幂等）。必须带 expectedRevision。", TodoCompleteSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: false, Destructive: false, Idempotent: true, Writes: true),
        new("todo_reopen", "重新打开已完成的待办（幂等）。必须带 expectedRevision。", TodoCompleteSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: false, Destructive: false, Idempotent: true, Writes: true),
        new("todo_delete", "删除单条待办（软删，回收站保留 7 天）。每次都必须经用户确认。", TodoDeleteSchema,
            RiskLevel.High, ApprovalMode.Always, null, ReadOnly: false, Destructive: true, Idempotent: false, Writes: true),
        new("todo_bulk_update", "批量改状态/日期/清单；影响 ≥5 条时必须确认。", TodoBulkUpdateSchema,
            RiskLevel.High, ApprovalMode.Threshold, PlannerConstants.BulkApprovalThreshold,
            ReadOnly: false, Destructive: false, Idempotent: false, Writes: true),
        new("todo_clear_list", "清空整个清单（软删，回收站保留 7 天）。每次都必须经用户确认。", TodoClearListSchema,
            RiskLevel.High, ApprovalMode.Always, null, ReadOnly: false, Destructive: true, Idempotent: false, Writes: true),
        new("todo_plan_from_text", "把一段需求/会议文本拆成待办初版。只产草稿，不写正式待办。", TodoPlanFromTextSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: true, Destructive: false, Idempotent: false, Writes: false),
        new("todo_accept_draft", "采纳草稿（可只采纳部分，逐条 reject）。≥10 条时必须确认。", TodoAcceptDraftSchema,
            RiskLevel.Medium, ApprovalMode.Threshold, PlannerConstants.DraftAcceptApprovalThreshold,
            ReadOnly: false, Destructive: false, Idempotent: false, Writes: true),
        new("todo_search", "全文检索标题与备注（FTS5 + 2-gram 预分词，中文两字可命中）。", TodoSearchSchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: true, Destructive: false, Idempotent: true, Writes: false),
        new("todo_stats", "今日到期 / 逾期 / 完成率统计。只统计 todo，不含 event。", EmptySchema,
            RiskLevel.Low, ApprovalMode.Never, null, ReadOnly: true, Destructive: false, Idempotent: true, Writes: false),
    ];

    public static McpToolDefinition? Find(string name) =>
        name is null ? null : Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
