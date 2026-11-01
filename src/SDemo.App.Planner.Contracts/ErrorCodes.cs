namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 业务错误码。与 docs/Apps/Planner/planner-mcp-surface.md §5 一一对应；
/// 新加码先改主仓工具面文档，再改这里。
/// </summary>
public static class ErrorCodes
{
    public const string NotFound = "TODO_NOT_FOUND";
    public const string Conflict = "TODO_CONFLICT";
    public const string Validation = "TODO_VALIDATION";
    public const string Limit = "TODO_LIMIT";
    public const string DuplicateSuspect = "TODO_DUPLICATE_SUSPECT";
    public const string Locked = "TODO_LOCKED";
    public const string Unauthorized = "MCP_UNAUTHORIZED";
    public const string DraftNotFound = "DRAFT_NOT_FOUND";
    public const string InvalidUri = "TODO_INVALID_URI";

    /// <summary>MCP 协议层错误（未知工具 / 参数不是合法 JSON / 方法不存在）。</summary>
    public const string UnknownTool = "MCP_UNKNOWN_TOOL";
    public const string BadRequest = "MCP_BAD_REQUEST";
    public const string Internal = "PLANNER_INTERNAL";
}

/// <summary>
/// 统一错误信封。错误信息必须是给模型看的中文短句 + 可执行 hint，不抛原始异常文本。
/// </summary>
public sealed record ApiError(
    string Code,
    string Message,
    string? Hint = null,
    IReadOnlyDictionary<string, object?>? Details = null)
{
    public static ApiError Create(string code, string message, string? hint = null) =>
        new(code, message, hint);
}

/// <summary>成功信封（见 planner-mcp-surface §5）。</summary>
public sealed record ApiEnvelope<T>(
    bool Ok,
    string RequestId,
    int? Revision,
    T? Data,
    IReadOnlyList<string>? Warnings = null)
{
    public static ApiEnvelope<T> Success(string requestId, T data, int? revision = null, IReadOnlyList<string>? warnings = null) =>
        new(true, requestId, revision, data, warnings);

    public static ApiEnvelope<T> Failure(string requestId, ApiError error) =>
        new(false, requestId, null, default, null);
}
