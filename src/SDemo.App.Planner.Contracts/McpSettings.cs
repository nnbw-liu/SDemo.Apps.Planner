namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 设置页展示用的 MCP 工具元数据。工具的真实声明（schema/审批）在 Mcp 项目，
/// 这里只保留名称、说明与风险的只读镜像；测试会校验两边一致，避免设置页漏工具。
/// </summary>
public sealed record McpToolDescriptor(
    string Name,
    string Label,
    string Description,
    RiskLevel Risk,
    bool ReadOnly);

public static class McpToolDescriptors
{
    public static IReadOnlyList<McpToolDescriptor> All { get; } =
    [
        new("todo_list", "查询待办", "默认只返回未完成项，按截止时间与优先级排序。", RiskLevel.Low, true),
        new("todo_get", "读取待办详情", "取单条待办的完整字段、来源与操作记录。", RiskLevel.Low, true),
        new("todo_create", "新建待办", "创建 1–5 条待办；标题必填，疑似完全重复会被拒绝。", RiskLevel.Medium, false),
        new("todo_update", "修改待办", "修改标题、备注、状态、优先级、截止时间等，必须带版本号。", RiskLevel.Medium, false),
        new("todo_complete", "完成待办", "把单条待办标记为已完成，重复调用幂等。", RiskLevel.Low, false),
        new("todo_reopen", "重开待办", "把已完成的待办重新打开，重复调用幂等。", RiskLevel.Low, false),
        new("todo_delete", "删除待办", "软删除单条待办，回收站保留 7 天，每次都需要确认。", RiskLevel.High, false),
        new("todo_bulk_update", "批量修改", "批量改状态、日期或清单，影响 5 条及以上需要确认。", RiskLevel.High, false),
        new("todo_clear_list", "清空清单", "软删除整个清单，回收站保留 7 天，每次都需要确认。", RiskLevel.High, false),
        new("todo_plan_from_text", "拆解文本为草稿", "把需求或会议文本拆成待办草稿，不写正式待办。", RiskLevel.Low, true),
        new("todo_accept_draft", "采纳草稿", "把草稿转成正式待办，可逐条拒绝，10 条及以上需要确认。", RiskLevel.Medium, false),
        new("todo_search", "全文检索", "检索标题与备注，中文两字即可命中。", RiskLevel.Low, true),
        new("todo_stats", "统计", "统计今日到期、逾期与完成率。", RiskLevel.Low, true),
    ];

    public static McpToolDescriptor? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}

/// <summary>设置接口与 MCP 入口共用的持久化键。</summary>
public static class McpSettingKeys
{
    public const string Enabled = "mcp.enabled";
    public const string ToolPrefix = "mcp.tool.";

    public static string Tool(string name) => ToolPrefix + name;
}

public sealed record McpToolSettingDto
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    public RiskLevel Risk { get; init; }

    public bool ReadOnly { get; init; }

    public bool Enabled { get; init; }
}

/// <summary>MCP 总开关 + 逐工具开关。总开关关闭时所有工具都不可见、不可调用。</summary>
public sealed record McpSettingsDto
{
    public bool Enabled { get; init; } = true;

    public IReadOnlyList<McpToolSettingDto> Tools { get; init; } = [];
}
