using System.Text;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// <c>todo_plan_from_text</c> 的确定性拆分器（PL-09）。
///
/// 本应用零模型凭证，所以这里**不做语义理解**：按行/编号/句号切段，一条一段，
/// 每段带上原文 excerpt。真正的「按里程碑拆」「只要本周能做的」由 DesktopAgent 的模型完成，
/// 它把拆好的标题再经 todo_create 落库；本工具只是给模型一个不写库的草稿容器。
///
/// 刻意不解析「明天下午三点」这类自然语言时间——契约要求时间必须带偏移，
/// 自然语言转时间属于 Agent 的职责，落库前必须变成显式 ISO-8601。
/// </summary>
public static class DraftTextSplitter
{
    public const int MaxDraftItems = 50;

    public static IReadOnlyList<(string Title, string Excerpt)> Split(string text)
    {
        var items = new List<(string Title, string Excerpt)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripBullet(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (var segment in SplitSentences(line))
            {
                var title = segment.Length > PlannerConstants.MaxTitleChars
                    ? segment[..PlannerConstants.MaxTitleChars]
                    : segment;
                var excerpt = segment.Length > 1024 ? segment[..1024] : segment;
                var key = Similarity.Normalize(title);
                if (key.Length == 0 || !seen.Add(key))
                {
                    continue;
                }

                items.Add((title, excerpt));
                if (items.Count >= MaxDraftItems)
                {
                    return items;
                }
            }
        }

        return items;
    }

    /// <summary>去掉常见列表标记：- * • □ [ ] [x] 1. 1) 1、 (1) 一、。</summary>
    internal static string StripBullet(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        while (text.Length > 0 && (text[0] is '-' or '*' or '•' or '□' or '■' or '·' or '>' or '[' or ']'))
        {
            text = text[1..].TrimStart();
        }

        // [x] / [ ] 这类复选框标记可能已经在上面的循环里被拆成 "x]"。
        if (text.StartsWith("x]", StringComparison.OrdinalIgnoreCase) || text.StartsWith(" ]", StringComparison.Ordinal))
        {
            text = text[1..].TrimStart();
        }

        // 数字编号：1. / 1) / 1、 / (1) / 一、
        var index = 0;
        while (index < text.Length && (char.IsDigit(text[index]) || IsCjkNumber(text[index]))
               && index < 4)
        {
            index++;
        }

        if (index > 0 && index < text.Length && text[index] is '.' or ')' or '、' or '）')
        {
            text = text[(index + 1)..].TrimStart();
        }

        if (text.StartsWith('(') && text.IndexOf(')') is > 0 and < 5)
        {
            text = text[(text.IndexOf(')') + 1)..].TrimStart();
        }

        return text;
    }

    internal static IReadOnlyList<string> SplitSentences(string line)
    {
        if (line.Length <= 80)
        {
            return [line];
        }

        var parts = new List<string>();
        var builder = new StringBuilder();

        foreach (var character in line)
        {
            builder.Append(character);
            if (character is '。' or '；' or ';' or '！' or '!' or '？' or '?' or '\t')
            {
                Flush();
            }
        }

        Flush();
        return parts.Count == 0 ? [line] : parts;

        void Flush()
        {
            var value = builder.ToString().Trim().TrimEnd('。', '；', ';', '！', '!', '？', '?', '\t', ',').Trim();
            builder.Clear();
            if (value.Length > 0)
            {
                parts.Add(value);
            }
        }
    }

    private static bool IsCjkNumber(char character) =>
        character is '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or '十';
}
