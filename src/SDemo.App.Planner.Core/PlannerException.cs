using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 领域错误：带业务错误码 + 给模型看的 hint。MCP 层把它转成错误信封，
/// UI 层把它显示成一句人话，两边都不抛原始异常文本。
/// </summary>
public sealed class PlannerException : Exception
{
    public PlannerException(
        string code,
        string message,
        string? hint = null,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
        Details = details;
    }

    public string Code { get; }

    public string? Hint { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }

    public static PlannerException NotFound(string message) =>
        new(ErrorCodes.NotFound, message, "不要重试；向用户确认 id 是否已被删除");

    public static PlannerException Conflict(int currentRevision) =>
        new(
            ErrorCodes.Conflict,
            "条目已被修改，拒绝覆盖",
            "重新 todo_get 后带新的 expectedRevision 重试一次；仍失败就告知用户",
            new Dictionary<string, object?> { ["currentRevision"] = currentRevision });

    public static PlannerException Validation(string message, string? hint = null) =>
        new(ErrorCodes.Validation, message, hint);

    public static PlannerException Limit(string message) =>
        new(ErrorCodes.Limit, message, "拆分调用");

    public static PlannerException DuplicateSuspect(string title, string existingId) =>
        new(
            ErrorCodes.DuplicateSuspect,
            $"「{title}」与已有条目疑似重复，未写入",
            "让用户裁决；确认要建请改标题或经 UI 手动新建",
            new Dictionary<string, object?> { ["existingId"] = existingId });

    public static PlannerException Locked(Exception inner) =>
        new(ErrorCodes.Locked, "数据库被占用", "等 1s 重试，最多 2 次", new Dictionary<string, object?> { ["error"] = inner.GetType().Name });

    public static PlannerException DraftNotFound(string draftId) =>
        new(ErrorCodes.DraftNotFound, $"没有找到草稿「{draftId}」", "重新 todo_plan_from_text");

    public static PlannerException InvalidUri(string uri, string? reason) =>
        new(ErrorCodes.InvalidUri, $"非法引用「{uri}」：{reason ?? "格式不符"}", "只接受 sdemo.<app>/<entity>/<id> 或 http(s) 地址");
}
