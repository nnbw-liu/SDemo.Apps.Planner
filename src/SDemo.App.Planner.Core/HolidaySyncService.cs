using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 从公开接口读取中国法定节假日。只负责取数/解析，落库由 PlannerService 完成。
/// 接口示例：https://timor.tech/api/holiday/year/2026
/// </summary>
public sealed class HolidaySyncService(HttpClient http)
{
    public const string EndpointTemplate = "https://timor.tech/api/holiday/year/{0}";

    public async Task<IReadOnlyList<HolidayInput>> FetchAsync(int year, CancellationToken cancellationToken = default)
    {
        var url = string.Format(CultureInfo.InvariantCulture, EndpointTemplate, year);
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(json, year);
    }

    public static IReadOnlyList<HolidayInput> Parse(string json, int year)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
        {
            throw new InvalidOperationException($"节假日接口返回错误码 {code.GetInt32()}");
        }

        if (!root.TryGetProperty("holiday", out var holiday) || holiday.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("节假日接口返回格式不包含 holiday 对象");
        }

        var result = new List<HolidayInput>();
        foreach (var property in holiday.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var item = property.Value;
            if (!item.TryGetProperty("date", out var dateElement) || dateElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var dateText = dateElement.GetString();
            if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || date.Year != year)
            {
                continue;
            }

            var isRest = item.TryGetProperty("holiday", out var restElement)
                && restElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && restElement.GetBoolean();
            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            result.Add(new HolidayInput
            {
                Date = date,
                Name = string.IsNullOrWhiteSpace(name) ? (isRest ? "法定假日" : "调休上班") : name.Trim(),
                IsWorkday = !isRest,
            });
        }

        return result
            .GroupBy(h => h.Date)
            .Select(g => g.First())
            .OrderBy(h => h.Date)
            .ToList();
    }
}
