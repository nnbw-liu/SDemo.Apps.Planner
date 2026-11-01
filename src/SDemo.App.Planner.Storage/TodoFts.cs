using System.Text;
using System.Text.RegularExpressions;

namespace SDemo.App.Planner.Storage;

/// <summary>
/// FTS5 索引的 2-gram 预分词（复用 DesktopAgent WorkspaceMemoryIndex 的已验证方案）：
/// SQLite 的 unicode61 会把整段中文当一个 token、trigram 又不支持两字查询，
/// 所以索引与查询都在应用侧切：中文连续段切 2-gram、拉丁/数字切小写单词。
/// </summary>
public static class TodoFts
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>标题 ×3 提升权重（bm25 的 tf 近似）；备注 ×1。</summary>
    public static string BuildTerms(string title, string? notes)
    {
        var titleTerms = Join(Tokenize(title), repeat: 3);
        var noteTerms = Join(Tokenize(notes), repeat: 1);
        return string.Join(' ', titleTerms, noteTerms).Trim();
    }

    public static IReadOnlyList<string> Tokenize(string? text) => TokenizeCore(text, forQuery: false);

    /// <summary>
    /// 查询侧：与索引侧用同一套切分；单字中文直接保留（退化为 LIKE 由调用方处理）。
    /// </summary>
    public static IReadOnlyList<string> TokenizeQuery(string? text) => TokenizeCore(text, forQuery: true);

    public static string AndQuery(IReadOnlyList<string> tokens) => string.Join(" AND ", tokens.Select(Quote));

    public static string OrQuery(IReadOnlyList<string> tokens) => string.Join(" OR ", tokens.Select(Quote));

    public static string Quote(string token) => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static IReadOnlyList<string> TokenizeCore(string? text, bool forQuery)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        var run = new StringBuilder();
        bool? runIsCjk = null;

        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                continue;
            }

            var isCjk = IsCjk(character);
            if (runIsCjk is not null && runIsCjk != isCjk)
            {
                Flush();
            }

            runIsCjk = isCjk;
            run.Append(character);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (run.Length == 0)
            {
                runIsCjk = null;
                return;
            }

            var value = run.ToString().ToLowerInvariant();
            if (forQuery)
            {
                value = Whitespace.Replace(value, string.Empty);
            }

            run.Clear();
            runIsCjk = null;

            if (!IsCjk(value[0]))
            {
                tokens.Add(value);
                return;
            }

            if (value.Length == 1)
            {
                tokens.Add(value);
                return;
            }

            for (var index = 0; index + 2 <= value.Length; index++)
            {
                tokens.Add(value.Substring(index, 2));
            }
        }
    }

    private static string Join(IReadOnlyList<string> tokens, int repeat)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var distinct = tokens.Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(' ', Enumerable.Repeat(string.Join(' ', distinct), repeat));
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF'
        || character is >= '\u4E00' and <= '\u9FFF'
        || character is >= '\uF900' and <= '\uFAFF';
}
