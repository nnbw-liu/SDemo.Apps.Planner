namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 全局常量。数值上限来自 planner-mcp-surface §7 与 docs/requirements.md，改这里等于改契约，
/// 必须同时改主仓文档与测试。
/// </summary>
public static class PlannerConstants
{
    public const string AppId = "sdemo.planner";
    public const string AppVersion = "0.4.1";

    /// <summary>宿主 API 版本（docs/contracts/host-protocol.md，主版本相同 + 次版本 ≤ 宿主）。</summary>
    public const string HostApiVersion = "1.0";
    public const string McpPath = "/mcp";

    /// <summary>与 DesktopAgent 的 TodoItem.MaxContentChars 对齐。</summary>
    public const int MaxTitleChars = 200;

    /// <summary>备注上限：防止上下文与文件体积爆掉。</summary>
    public const int MaxNotesChars = 4000;

    /// <summary>单次 create 最多 5 条，防止模型一口气灌 50 条。</summary>
    public const int MaxCreateItems = 5;

    /// <summary>单次响应最多 50 条，控制工具结果进上下文的体积。</summary>
    public const int MaxListLimit = 50;

    public const int DefaultListLimit = 20;

    /// <summary>幂等窗口：10 分钟内重复 clientRequestId 返回首次结果。</summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(10);

    /// <summary>回收站保留 7 天。</summary>
    public static readonly TimeSpan RecycleBinRetention = TimeSpan.FromDays(7);

    /// <summary>批量更新 / 采纳草稿的审批阈值。</summary>
    public const int BulkApprovalThreshold = 5;

    public const int DraftAcceptApprovalThreshold = 10;

    public const int MaxTagsPerTodo = 10;

    public const int MaxTagChars = 32;

    public const int MaxLinksPerTodo = 10;

    public const int MaxEventParticipants = 200;

    /// <summary>草稿单条文本上限（todo_plan_from_text.text）。</summary>
    public const int MaxPlanTextChars = 20_000;

    public const int MaxRequestBytes = 64 * 1024;
}
