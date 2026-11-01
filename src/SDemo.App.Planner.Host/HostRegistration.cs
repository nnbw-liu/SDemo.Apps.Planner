using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Contracts;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 应用 → 宿主的 native 窗口登记（host-protocol §6.2）。不引用 Shell.Contracts，
/// 只用公开的协议字段与环境变量，保证两个仓零编译依赖。
/// </summary>
internal sealed class HostRegistration
{
    private const string HostUrlEnvironmentVariable = "SDEMO_HOST_URL";
    private const string HostFileName = "host.json";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _widgetId;
    private readonly string _dpiAware;

    public HostRegistration(string token)
    {
        _appId = PlannerConstants.AppId;
        _widgetId = PlannerConstants.WidgetId;
        _dpiAware = "permonitorv2";
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>上报窗口句柄；最多 3 次，失败只写日志。</summary>
    public async Task RegisterQuietly(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        await SendQuietly("register", new { widgetId = _widgetId, hwnd, dpiAware = _dpiAware });
    }

    /// <summary>窗口关闭时尽量通知宿主；失败不影响退出。</summary>
    public async Task UnregisterQuietly(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        await SendQuietly("unregister", new { widgetId = _widgetId, hwnd });
    }

    private async Task SendQuietly(string action, object payload)
    {
        var json = JsonSerializer.Serialize(payload, PlannerJson.Wire);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var hostUrl = ResolveHostUrl();
            if (hostUrl is null)
            {
                PlannerLog.Warn($"宿主未运行，{action} 跳过（第 {attempt} 次）");
            }
            else
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _http
                        .PostAsync($"{hostUrl}/v1/apps/{Uri.EscapeDataString(_appId)}/{action}", content)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        PlannerLog.Info($"宿主 {action} 成功");
                        return;
                    }

                    PlannerLog.Warn($"宿主 {action} 返回 HTTP {(int)response.StatusCode}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
                {
                    PlannerLog.Warn($"宿主 {action} 失败：{ex.Message}");
                }
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private static string? ResolveHostUrl()
    {
        var injected = Environment.GetEnvironmentVariable(HostUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(injected))
        {
            return injected.Trim().TrimEnd('/');
        }

        var hostFile = Path.Combine(AppServer.RuntimeDirectory, HostFileName);
        try
        {
            if (!File.Exists(hostFile))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(hostFile, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("port", out var portNode) || !portNode.TryGetInt32(out var port)
                || port is <= 0 or > 65535)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("pid", out var pidNode) && pidNode.TryGetInt32(out var pid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited)
                    {
                        return null;
                    }
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            return $"http://127.0.0.1:{port}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PlannerLog.Warn($"读取 host.json 失败：{ex.Message}");
            return null;
        }
    }
}
