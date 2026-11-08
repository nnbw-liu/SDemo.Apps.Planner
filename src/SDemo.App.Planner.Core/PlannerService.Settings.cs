using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>MCP 总开关与逐工具开关，落在 app_setting 表，Host 与 Web 共用。</summary>
public sealed partial class PlannerService
{
    public McpSettingsDto GetMcpSettings()
    {
        var values = _store.GetSettings(McpSettingKeys.ToolPrefix);
        var globalEnabled = !string.Equals(_store.GetSetting(McpSettingKeys.Enabled), "false", StringComparison.Ordinal);

        var tools = McpToolDescriptors.All
            .Select(d => new McpToolSettingDto
            {
                Name = d.Name,
                Label = d.Label,
                Description = d.Description,
                Risk = d.Risk,
                ReadOnly = d.ReadOnly,
                Enabled = !values.TryGetValue(McpSettingKeys.Tool(d.Name), out var value)
                          || !string.Equals(value, "false", StringComparison.Ordinal),
            })
            .ToList();

        return new McpSettingsDto { Enabled = globalEnabled, Tools = tools };
    }

    public bool IsMcpToolEnabled(string name)
    {
        var settings = GetMcpSettings();
        return settings.Enabled
               && settings.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))?.Enabled == true;
    }

    public McpSettingsDto SetMcpEnabled(bool enabled)
    {
        _store.SetSettings(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [McpSettingKeys.Enabled] = enabled ? "true" : "false",
            },
            NowUtc());
        RaiseChanged();
        return GetMcpSettings();
    }

    public McpSettingsDto SetMcpToolEnabled(string name, bool enabled)
    {
        if (McpToolDescriptors.Find(name) is null)
        {
            throw PlannerException.Validation($"没有找到 MCP 工具「{name}」");
        }

        _store.SetSettings(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [McpSettingKeys.Tool(name)] = enabled ? "true" : "false",
            },
            NowUtc());
        RaiseChanged();
        return GetMcpSettings();
    }
}
