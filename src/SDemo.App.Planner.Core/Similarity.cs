using System.Text;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 疑似重复判断（PL-09）。由应用侧算，不让模型猜：
/// 标题归一化后完全相同 → 判重复；2-gram Jaccard ≥ 0.8 → 疑似。
/// </summary>
public static class Similarity
{
    /// <summary>
    /// 疑似重复阈值（2-gram Jaccard）。0.75 是「标题八成是同一件事」的宽松档：
    /// 宁可多弹一条提示让用户裁决，也不要静默写入一堆近义条目。
    /// </summary>
    public const double SuspectThreshold = 0.75;

    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        foreach (var character in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static double Jaccard(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var a = Grams(left);
        var b = Grams(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count(g => b.Contains(g));
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>返回 null 表示不重复；返回 id 表示疑似重复（exact=true 表示完全同名）。</summary>
    public static (string Id, string Title, bool Exact)? Find(
        string title,
        IReadOnlyList<(string Id, string Title, string? Notes)> candidates)
    {
        var normalized = Normalize(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        (string Id, string Title, bool Exact)? best = null;
        var bestScore = 0d;

        foreach (var candidate in candidates)
        {
            var other = Normalize(candidate.Title);
            if (other.Length == 0)
            {
                continue;
            }

            if (normalized == other)
            {
                return (candidate.Id, candidate.Title, true);
            }

            var score = Jaccard(normalized, other);
            if (score >= SuspectThreshold && score > bestScore)
            {
                bestScore = score;
                best = (candidate.Id, candidate.Title, false);
            }
        }

        return best;
    }

    /// <summary>中文 2-gram；拉丁按整词。用于标题相似度。</summary>
    private static HashSet<string> Grams(string text)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        var run = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                run.Append(character);
                continue;
            }

            Flush();
        }

        Flush();
        return grams;

        void Flush()
        {
            if (run.Length == 0)
            {
                return;
            }

            var value = run.ToString();
            run.Clear();

            if (value.Length <= 2)
            {
                grams.Add(value);
                return;
            }

            for (var i = 0; i + 2 <= value.Length; i++)
            {
                grams.Add(value.Substring(i, 2));
            }
        }
    }
}
