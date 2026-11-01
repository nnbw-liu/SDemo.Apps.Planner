using System.Text.RegularExpressions;

namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 跨应用引用（docs/integration.md §4.1）：只存字符串，永不调对方接口。
/// 形如 <c>sdemo.&lt;appId&gt;/&lt;entity&gt;/&lt;id&gt;</c>；解析责任在 DesktopAgent。
/// </summary>
public static partial class UriRef
{
    [GeneratedRegex(@"^sdemo\.([a-z0-9]+(?:\.[a-z0-9-]+)+)/([a-z0-9-]+)/([A-Za-z0-9_.@#%-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UriPattern();

    public static string Build(string appId, string entity, string id) =>
        $"sdemo.{appId}/{entity}/{id}";

    public static bool TryParse(string? uri, out string appId, out string entity, out string id)
    {
        appId = string.Empty;
        entity = string.Empty;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        var match = UriPattern().Match(uri.Trim());
        if (!match.Success)
        {
            return false;
        }

        appId = match.Groups[1].Value;
        entity = match.Groups[2].Value;
        id = match.Groups[3].Value;
        return true;
    }

    /// <summary>校验一组 links：要么是 sdemo.* URI，要么是 http(s) 绝对地址。</summary>
    public static bool IsAcceptableLink(string? link, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(link))
        {
            reason = "链接为空";
            return false;
        }

        var trimmed = link.Trim();
        if (trimmed.Length > 2048)
        {
            reason = "链接超过 2048 字符";
            return false;
        }

        if (TryParse(trimmed, out _, out _, out _))
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        reason = "链接必须是 sdemo.<app>/<entity>/<id> 或 http(s) 地址";
        return false;
    }
}
