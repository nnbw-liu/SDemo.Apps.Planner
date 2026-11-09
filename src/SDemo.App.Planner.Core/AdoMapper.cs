using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// ADO → 事序的纯映射。不碰网络、不碰存储，单测直接覆盖。
/// ADO 是权威源：状态/类型/优先级都按固定规则翻译，避免「猜」。
/// </summary>
public static partial class AdoMapper
{
    public const string SourceName = "azure-devops";

    /// <summary>ADO 状态 → 事序状态。中文/英文都认，未知状态按「新建」处理。</summary>
    public static string ToStorageStatus(string? adoState)
    {
        // 中文状态原样比较，英文忽略大小写（ADO 客户端有时给 "Active"/"active"）。
        var state = (adoState ?? string.Empty).Trim();
        return state switch
        {
            "新建" => "open",
            "活动" => "doing",
            "已解决" => "done",
            "已验证" => "verified",
            "已关闭" => "closed",
            "已删除" => "canceled",
            _ => state.ToLowerInvariant() switch
            {
                "new" or "proposed" or "to do" => "open",
                "active" or "in progress" or "committed" => "doing",
                "resolved" => "done",
                "verified" => "verified",
                "closed" or "done" => "closed",
                "removed" => "canceled",
                _ => "open",
            },
        };
    }

    /// <summary>ADO 工作项类型 → 事序 Kind。用户情景/Feature 目前也当任务用（事序没有史诗层）。</summary>
    public static string ToStorageKind(string? adoType)
    {
        var type = (adoType ?? string.Empty).Trim();
        if (type is "缺陷")
        {
            return "bug";
        }

        if (type is "调研")
        {
            return "research";
        }

        if (type is "杂务")
        {
            return "chore";
        }

        return type.ToLowerInvariant() switch
        {
            "bug" or "bug 修复" => "bug",
            "research" or "spike" => "research",
            "chore" => "chore",
            _ => "task",
        };
    }

    /// <summary>ADO 优先级 1（最高）… 4 → 事序 4 … 1；缺失或越界按默认 2。</summary>
    public static int ToPriority(int? adoPriority)
    {
        return adoPriority switch
        {
            1 => 4,
            2 => 3,
            3 => 2,
            4 => 1,
            _ => 2,
        };
    }

    /// <summary>迭代起止推状态：没开始=planned，进行中=active，已过结束=done。</summary>
    public static string ToIterationStatus(DateTimeOffset? startAt, DateTimeOffset? endAt, DateTimeOffset now)
    {
        if (startAt is { } start && now < start)
        {
            return "planned";
        }

        if (endAt is { } end && now > end)
        {
            return "done";
        }

        return startAt is null && endAt is null ? "planned" : "active";
    }

    /// <summary>
    /// ADO 描述是 HTML。转成可读纯文本：块级标签换行，去掉标签与多余空行。
    /// 已经是纯文本时原样返回。上限 4000 字符（与 todo.notes 一致）。
    /// </summary>
    public static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = BlockBreakRegex().Replace(html, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00a0', ' ');

        var lines = text.Split('\n')
            .Select(line => WhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        var joined = string.Join("\n", lines).Trim();

        if (joined.Length == 0)
        {
            return null;
        }

        return joined.Length > PlannerConstants.MaxNotesChars
            ? joined[..PlannerConstants.MaxNotesChars]
            : joined;
    }

    /// <summary>迭代路径统一去掉首尾反斜杠（ADO 工作项的 IterationPath 不带前导 `\`）。</summary>
    public static string NormalizePath(string? path) => (path ?? string.Empty).Trim().Trim('\\');

    /// <summary>ADO 迭代节点的完整路径按层级拆开；用于决定「大迭代 / 小迭代」。</summary>
    public static string[] SplitPath(string? path) =>
        NormalizePath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 分类节点路径 → 工作项 <c>System.IterationPath</c> 口径（ADO 会去掉「迭代」这一层根节点）。
    /// 例：<c>项目\迭代\3.0.1</c> → <c>项目\3.0.1</c>；不足三层时返回 null。
    /// </summary>
    public static string? IterationAlias(string? path)
    {
        var parts = SplitPath(path);
        return parts.Length < 3 ? null : parts[0] + "\\" + string.Join("\\", parts.Skip(2));
    }

    /// <summary>选中的迭代路径是否包含该工作项所在的迭代（按别名口径比较）。</summary>
    public static bool IterationMatches(IReadOnlyList<string> selectedPaths, string? workItemIterationPath)
    {
        if (selectedPaths.Count == 0)
        {
            return true;
        }

        var actual = NormalizePath(workItemIterationPath);
        if (actual.Length == 0)
        {
            return false;
        }

        foreach (var selected in selectedPaths)
        {
            var alias = IterationAlias(selected) ?? NormalizePath(selected);
            if (alias.Length > 0
                && (string.Equals(actual, alias, StringComparison.OrdinalIgnoreCase)
                    || actual.StartsWith(alias + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public static string WorkItemUrl(string collectionUrl, string project, long id) =>        $"{collectionUrl.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_workitems/edit/{id.ToString(CultureInfo.InvariantCulture)}";

    public static string ProjectUrl(string collectionUrl, string project) =>
        $"{collectionUrl.TrimEnd('/')}/{Uri.EscapeDataString(project)}";

    /// <summary>集合地址统一去掉尾部斜杠（用户经常把 `/Utilities/` 一起贴进来）。</summary>
    public static string NormalizeCollectionUrl(string? collectionUrl) =>
        (collectionUrl ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>迭代在 ADO 里的工作项列表页。</summary>
    public static string? IterationUrl(string? projectUrl, string? iterationPath)
    {
        if (string.IsNullOrWhiteSpace(projectUrl) || string.IsNullOrWhiteSpace(iterationPath))
        {
            return projectUrl;
        }

        return $"{projectUrl.TrimEnd('/')}/_workitems?iterationPath={Uri.EscapeDataString(iterationPath)}";
    }

    // ── 反向映射（事序 → ADO），单个任务推送用 ───────────────────────────────

    /// <summary>
    /// 事序状态 → ADO 状态的候选名（中文流程在前，英文流程在后）。
    /// 服务端只认自己进程里的状态名，所以按顺序试，第一个被接受的就用。
    /// </summary>
    public static IReadOnlyList<string> ToAdoStates(string? storageStatus) => storageStatus switch
    {
        "doing" => ["活动", "Active"],
        "done" => ["已解决", "Resolved"],
        "verified" => ["已验证", "Verified"],
        "closed" => ["已关闭", "Closed"],
        "canceled" => ["已删除", "Removed"],
        _ => ["新建", "New"],
    };

    /// <summary>事序优先级（0 低 … 4 高）→ ADO 优先级（1 高 … 4 低），与 <see cref="ToPriority"/> 互逆。</summary>
    public static int ToAdoPriority(int priority) => priority switch
    {
        4 => 1,
        3 => 2,
        1 => 4,
        _ => 3,
    };

    /// <summary>纯文本说明 → ADO 的 HTML 说明（转义 + 换行变 <c>&lt;br /&gt;</c>）。</summary>
    public static string ToAdoHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var escaped = text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return escaped
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "<br />", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6])\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex WhitespaceRegex();
}
