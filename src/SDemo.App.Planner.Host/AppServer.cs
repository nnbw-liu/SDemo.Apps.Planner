using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Mcp;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 应用的 app 面 HTTP（docs/contracts/host-protocol.md §6）：只监听 127.0.0.1 的随机端口，
/// 除 <c>/v1/health</c> 外全部校验 Bearer token；MCP 走同一个端口的 <c>/mcp</c>。
///
/// 传输层只做「鉴权 + 路由 + 体积上限」，工具语义全在 <see cref="McpServer"/>。
/// </summary>
internal sealed class AppServer : IDisposable
{
    private const int MaxRequestBytes = PlannerConstants.MaxRequestBytes;

    private readonly string _token;
    private readonly string _appId;
    private readonly string _appVersion;
    private readonly PlannerService _service;
    private readonly McpServer _mcp;
    private readonly DateTimeOffset _startedAt;

    private HttpListener? _listener;
    private Task? _loop;
    private int _stopped;

    public AppServer(string token, PlannerService service, McpServer mcp, string appId, string appVersion)
    {
        _token = token;
        _service = service;
        _mcp = mcp;
        _appId = appId;
        _appVersion = appVersion;
        _startedAt = DateTimeOffset.Now;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>收到 /v1/shutdown 后回调；由 UI 线程执行关窗退出。</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>宿主指令（show / hide / focus / refresh / themeChanged / configChanged）。</summary>
    public Action<WidgetCommandPayload>? CommandReceived { get; set; }

    /// <summary>组件可见性与透明度快照；由 WidgetWindow 注入。</summary>
    public Func<(bool Visible, double Opacity)>? StateProvider { get; set; }

    internal static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo",
        "DesktopAssembly",
        "runtime");

    internal static string GetRuntimePath(string appId) => Path.Combine(RuntimeDirectory, appId + ".json");

    public void Start()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var port = FindFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                WriteRuntimeFile();
                _loop = Task.Run(LoopAsync);
                PlannerLog.Info($"app 面已启动：127.0.0.1:{port}");
                return;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex;
                listener.Close();
                PlannerLog.Warn($"绑定 127.0.0.1:{port} 失败（第 {attempt + 1} 次）：{ex.Message}");
            }
        }

        throw new InvalidOperationException("无法绑定本机回环端口。", lastError);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        try
        {
            _loop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // 停止时的取消异常。
        }

        DeleteRuntimeFile(_appId);
    }

    internal static void DeleteRuntimeFile(string appId)
    {
        try
        {
            var path = GetRuntimePath(appId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 退出清理失败不阻塞。
        }
    }

    private void WriteRuntimeFile()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        var payload = new
        {
            pid = Environment.ProcessId,
            port = Port,
            token = _token,
            startedAt = _startedAt.ToString("O", CultureInfo.InvariantCulture),
            appVersion = _appVersion,
            hostApiVersion = PlannerConstants.HostApiVersion,
        };

        var path = GetRuntimePath(_appId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, PlannerJson.Wire), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private async Task LoopAsync()
    {
        while (!_stopped.Equals(1))
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopped == 1)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var requestId = request.Headers["X-Request-Id"] is { Length: > 0 } provided
            ? provided
            : "r_" + Guid.NewGuid().ToString("N")[..12];

        response.Headers["X-Request-Id"] = requestId;

        try
        {
            var path = NormalizePath(request.Url?.AbsolutePath ?? "/");
            var method = request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "/v1/health")
            {
                await WriteJsonAsync(response, 200, new
                {
                    ok = true,
                    pid = Environment.ProcessId,
                    since = _startedAt.ToString("O", CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);
                return;
            }

            if (!IsAuthorized(request))
            {
                PlannerLog.Warn($"401 {method} {path}（token 不匹配）");
                await WriteErrorAsync(response, 401, "APP_UNAUTHORIZED", "token 不匹配", "用 runtime.json 里的 token").ConfigureAwait(false);
                return;
            }

            if (path == PlannerConstants.McpPath && method == "POST")
            {
                await HandleMcpAsync(context, requestId).ConfigureAwait(false);
                return;
            }

            if (path == "/v1/handshake" && method == "POST")
            {
                await HandleHandshakeAsync(request, response, requestId).ConfigureAwait(false);
                return;
            }

            if (path == "/v1/shutdown" && method == "POST")
            {
                await WriteJsonAsync(response, 200, new { accepted = true }).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    ShutdownRequested?.Invoke();
                });
                return;
            }

            var segments = path.Trim('/').Split('/');
            if (segments.Length >= 3 && segments[0] == "v1" && segments[1] == "widgets")
            {
                var widgetId = segments[2];
                if (!string.Equals(widgetId, PlannerConstants.WidgetId, StringComparison.Ordinal))
                {
                    await WriteErrorAsync(response, 404, "APP_NOT_FOUND", $"未知组件「{widgetId}」", null).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 5 && segments[3] == "command" && method == "POST")
                {
                    await HandleCommandAsync(request, response, requestId).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 4 && segments[3] == "state" && method == "GET")
                {
                    var state = StateProvider?.Invoke() ?? (Visible: false, Opacity: 0d);
                    await WriteJsonAsync(response, 200, new
                    {
                        widgetId,
                        visible = state.Visible,
                        opacity = state.Opacity,
                    }).ConfigureAwait(false);
                    return;
                }
            }

            await WriteErrorAsync(response, 404, "APP_NOT_FOUND", "路径不存在", $"{method} {path}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlannerLog.Error($"处理请求失败 {request.HttpMethod} {request.Url}", ex);
            try
            {
                await WriteErrorAsync(response, 500, "APP_INTERNAL", "处理请求失败", null).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 响应已开始写出时无法再改状态码。
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // 客户端提前断开。
            }
        }
    }

    private async Task HandleMcpAsync(HttpListenerContext context, string requestId)
    {
        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(context.Response, 413, "APP_TOO_LARGE", "请求体超过 64KB", null).ConfigureAwait(false);
            return;
        }

        string payload;
        try
        {
            payload = _mcp.HandleJson(body);
        }
        catch (Exception ex)
        {
            PlannerLog.Error("MCP 处理失败", ex);
            await WriteErrorAsync(context.Response, 500, "PLANNER_INTERNAL", "MCP 处理失败", null).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(payload))
        {
            context.Response.StatusCode = 202;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private async Task HandleHandshakeAsync(HttpListenerRequest request, HttpListenerResponse response, string requestId)
    {
        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        var hostApiVersion = "1.0";
        if (body is { Length: > 0 })
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("hostApiVersion", out var version)
                    && version.ValueKind == JsonValueKind.String)
                {
                    hostApiVersion = version.GetString() ?? hostApiVersion;
                }
            }
            catch (JsonException)
            {
                await WriteErrorAsync(response, 400, "APP_BAD_REQUEST", "握手 body 不是合法 JSON", null).ConfigureAwait(false);
                return;
            }
        }

        if (!IsHostVersionCompatible(hostApiVersion, PlannerConstants.HostApiVersion))
        {
            await WriteErrorAsync(response, 400, "HOST_VERSION_MISMATCH",
                $"宿主 API 版本 {hostApiVersion} 与本应用不兼容（需要 {PlannerConstants.HostApiVersion}）",
                "升级宿主或应用").ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(response, 200, new
        {
            appVersion = _appVersion,
            hostApiVersion = PlannerConstants.HostApiVersion,
            capabilities = new[] { "native-window" },
        }).ConfigureAwait(false);
    }

    private async Task HandleCommandAsync(HttpListenerRequest request, HttpListenerResponse response, string requestId)
    {
        var body = await ReadBodyAsync(request).ConfigureAwait(false);
        if (body is null)
        {
            await WriteErrorAsync(response, 413, "APP_TOO_LARGE", "请求体超过 64KB", null).ConfigureAwait(false);
            return;
        }

        WidgetCommandPayload? command;
        try
        {
            command = body.Length == 0
                ? null
                : JsonSerializer.Deserialize<WidgetCommandPayload>(body, PlannerJson.Wire);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(response, 400, "APP_BAD_REQUEST", "指令 body 不是合法 JSON", null).ConfigureAwait(false);
            return;
        }

        if (command is null || string.IsNullOrWhiteSpace(command.Command))
        {
            await WriteErrorAsync(response, 400, "APP_BAD_REQUEST", "缺少 command", "show / hide / focus / refresh / themeChanged / configChanged").ConfigureAwait(false);
            return;
        }

        if (command.Command is not ("show" or "hide" or "focus" or "refresh" or "themeChanged" or "configChanged"))
        {
            await WriteErrorAsync(response, 400, "APP_BAD_REQUEST", $"未知指令「{command.Command}」", null).ConfigureAwait(false);
            return;
        }

        CommandReceived?.Invoke(command);
        await WriteJsonAsync(response, 200, new { accepted = true }).ConfigureAwait(false);
    }

    private async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxRequestBytes)
        {
            return null;
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        return text.Length > MaxRequestBytes ? null : text;
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header["Bearer ".Length..].Trim();
        var expected = _token;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static bool IsHostVersionCompatible(string? required, string host)
    {
        if (!TryParseVersion(required, out var requiredMajor, out var requiredMinor)
            || !TryParseVersion(host, out var hostMajor, out var hostMinor))
        {
            return false;
        }

        return requiredMajor == hostMajor && requiredMinor <= hostMinor;
    }

    private static bool TryParseVersion(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, PlannerJson.Wire);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        return response.OutputStream.WriteAsync(bytes).AsTask();
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string code, string message, string? hint) =>
        WriteJsonAsync(response, statusCode, new
        {
            error = new { code, message, hint },
        });

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
