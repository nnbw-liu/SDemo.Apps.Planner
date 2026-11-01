using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 宿主 → 应用的指令（docs/contracts/host-protocol.md §6.1）。
/// 宿主只传值；窗口层级 / 位置 / 尺寸仍由宿主执行，应用不越权。
/// </summary>
public sealed record WidgetCommandPayload
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("opacity")]
    public double? Opacity { get; init; }

    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    /// <summary>从 config 里读 maxItems；缺省 8，与 widget.json 的 schema 一致。</summary>
    public int MaxItems => Config is { ValueKind: JsonValueKind.Object } config
                           && config.TryGetProperty("maxItems", out var value)
                           && value.TryGetInt32(out var maxItems)
        ? Math.Clamp(maxItems, 3, 30)
        : 8;
}
