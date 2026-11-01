using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.App.Planner.Contracts;

/// <summary>
/// 线格式：camelCase、枚举写字符串、不写 null 字段。MCP 工具结果与 app 面 HTTP 共用同一份，
/// 避免「工具里叫 dueAt、UI 里叫 DueAt」这种两套名字。
/// </summary>
public static class PlannerJson
{
    public static readonly JsonSerializerOptions Wire = Create(indented: false);

    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,

        // 审计 / outbox / FTS 里都存中文；不放松转义会把每个汉字变成 \uXXXX，排障时没法读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
