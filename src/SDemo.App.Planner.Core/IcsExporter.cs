using System.Text;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// ICS（RFC 5545）导出（PL-05）。v1 只做导出；只读订阅在 P5。
/// todo → VTODO（dueAt 是截止点）、event → VEVENT（起止区间）；不做 RRULE 编辑。
/// </summary>
public static class IcsExporter
{
    public static string Export(
        IReadOnlyList<TodoRow> todos,
        IReadOnlyDictionary<string, IReadOnlyList<string>> todoTags,
        IReadOnlyList<EventRow> events,
        IcsExportOptions options,
        DateTimeOffset nowUtc)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//SDemo//Planner 0.1.0//ZH",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            $"X-WR-CALNAME:{Escape(options.CalendarName ?? "SDemo 待办与日历")}",
        };

        var stamp = IsoTime.ToIcs(nowUtc);

        if (options.IncludeTodos)
        {
            foreach (var todo in todos)
            {
                if (!options.IncludeCompleted && todo.Status == "done")
                {
                    continue;
                }

                lines.Add("BEGIN:VTODO");
                lines.Add($"UID:{Escape(todo.Id)}@sdemo.planner");
                lines.Add($"DTSTAMP:{stamp}");
                lines.Add($"SUMMARY:{Escape(todo.Title)}");

                if (!string.IsNullOrWhiteSpace(todo.Notes))
                {
                    lines.Add($"DESCRIPTION:{Escape(todo.Notes)}");
                }

                if (todo.DueAt is { } due)
                {
                    lines.Add($"DUE:{IsoTime.ToIcs(ToOffset(due))}");
                }

                lines.Add($"PRIORITY:{MapPriority(todo.Priority)}");
                lines.Add($"STATUS:{MapStatus(todo.Status)}");

                if (todo.Status == "done" && todo.CompletedAt is { } completed)
                {
                    lines.Add($"COMPLETED:{IsoTime.ToIcs(ToOffset(completed))}");
                    lines.Add("PERCENT-COMPLETE:100");
                }

                var tags = todoTags.GetValueOrDefault(todo.Id);
                if (tags is { Count: > 0 })
                {
                    lines.Add($"CATEGORIES:{Escape(string.Join(',', tags))}");
                }

                lines.Add("END:VTODO");
            }
        }

        if (options.IncludeEvents)
        {
            foreach (var item in events)
            {
                lines.Add("BEGIN:VEVENT");
                lines.Add($"UID:{Escape(item.Id)}@sdemo.planner");
                lines.Add($"DTSTAMP:{stamp}");
                lines.Add($"DTSTART:{IsoTime.ToIcs(ToOffset(item.StartAt))}");
                lines.Add($"DTEND:{IsoTime.ToIcs(ToOffset(item.EndAt))}");
                lines.Add($"SUMMARY:{Escape(item.Title)}");

                var description = BuildEventDescription(item);
                if (description.Length > 0)
                {
                    lines.Add($"DESCRIPTION:{Escape(description)}");
                }

                if (!string.IsNullOrWhiteSpace(item.Location))
                {
                    lines.Add($"LOCATION:{Escape(item.Location)}");
                }

                lines.Add("STATUS:CONFIRMED");
                lines.Add("END:VEVENT");
            }
        }

        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    /// <summary>RFC 5545 §3.1：内容行每行不超过 75 个八位字节，续行以一个空格开头。</summary>
    internal static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + 8);
        var segmentBytes = 0;
        var limit = 75;
        var first = true;

        foreach (var character in line)
        {
            var bytes = Encoding.UTF8.GetByteCount(character.ToString());
            if (segmentBytes + bytes > limit && segmentBytes > 0)
            {
                builder.Append("\r\n ");
                segmentBytes = 1; // 续行的前导空格
                limit = 75;
                first = false;
            }

            builder.Append(character);
            segmentBytes += bytes;
        }

        _ = first;
        return builder.ToString();
    }

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal);
    }

    private static string BuildEventDescription(EventRow item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            parts.Add(item.Notes.Trim());
        }

        var participants = PlannerMapper.ParseParticipants(item.ParticipantsJson);
        if (participants.Count > 0)
        {
            parts.Add("参与人：" + string.Join('、', participants));
        }

        return string.Join('\n', parts);
    }

    private static string MapStatus(string status) => status switch
    {
        "done" => "COMPLETED",
        "doing" => "IN-PROCESS",
        _ => "NEEDS-ACTION",
    };

    /// <summary>我们的 0–4 映射到 ICS 的 1–9（1 最高）。</summary>
    private static int MapPriority(int priority) => priority switch
    {
        >= 4 => 1,
        3 => 2,
        2 => 5,
        1 => 7,
        _ => 9,
    };

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
