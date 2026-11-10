using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 面向 SDemo Hub 的内嵌 Kestrel：/v1/info 握手 + Blazor Server 组件页。
/// 组件页只接受 &lt;appId&gt;.localhost + host-only cookie，避免同机其他应用读取。
/// </summary>
internal sealed class HubServer : IAsyncDisposable
{
    private const string CookieName = "sdemo_session";
    private readonly string _appId;
    private readonly string _token;
    private readonly PlannerService _service;
    private readonly int _mcpPort;
    private WebApplication? _app;

    public HubServer(string appId, string token, PlannerService service, int mcpPort)
    {
        _appId = appId;
        _token = token;
        _service = service;
        _mcpPort = mcpPort;
    }

    public int Port { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>由桌面模式注入；headless 模式下为 null，调用返回 409。</summary>
    public Func<bool>? ShowMainWindow { get; set; }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
            ApplicationName = typeof(PlannerServerApp).Assembly.GetName().Name ?? "SDemo.Planner",
        });

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(_service);
        builder.Services.AddScoped<PlannerUi>();
        builder.Services.AddScoped<ThemeService>();
        builder.Services.AddSingleton<IThemeHost, NullThemeHost>();
        builder.Services.AddSingleton<IAppLifecycle, ServerAppLifecycle>();
        builder.Services.AddSingleton<HolidaySyncService>();
        builder.Services.AddSingleton<AdoClient>();
        builder.Services.AddSingleton<AdoImportService>();
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

        var app = builder.Build();
        _app = app;

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/v1"))
            {
                await next();
                return;
            }

            if (!string.Equals(context.Request.Host.Host, $"{_appId}.localhost", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("widget host must be <appId>.localhost");
                return;
            }

            if (!CookieMatches(context.Request.Cookies[CookieName]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("missing or invalid widget session");
                return;
            }

            await next();
        });

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapGet("/v1/health", () => Results.Json(new
        {
            status = "ok",
            appId = _appId,
            version = PlannerConstants.AppVersion,
        }));

        app.MapGet("/v1/info", (HttpRequest request) =>
        {
            if (!IsAuthorized(request))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(BuildInfo());
        });

        app.MapPost("/v1/main-window/open", (HttpRequest request) =>
        {
            if (!IsAuthorized(request))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var show = ShowMainWindow;
            if (show is null || !show())
            {
                return Results.Json(new { error = "main window is not available in headless mode" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.NoContent();
        });

        app.MapGet("/", () => Results.Redirect("/widgets/calendar"));

        app.MapRazorComponents<PlannerServerApp>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(Routes).Assembly);

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Kestrel 未返回监听地址。");
        }

        Port = uri.Port;
        WriteConnectionFile();
        PlannerLog.Info($"Hub 面已就绪：http://127.0.0.1:{Port}（组件 /widgets/calendar、/widgets/tasks）");
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                PlannerLog.Warn($"Hub 面停止失败：{ex.Message}");
            }

            await app.DisposeAsync();
        }

        MarkStopped();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private bool IsAuthorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return FixedTimeEquals(header["Bearer ".Length..].Trim(), _token);
    }

    private bool CookieMatches(string? value) =>
        !string.IsNullOrEmpty(value) && FixedTimeEquals(value, _token);

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private object BuildInfo() => new
    {
        schemaVersion = 1,
        appId = _appId,
        displayName = "事序",
        version = PlannerConstants.AppVersion,
        kind = "app",
        baseUrl = $"http://127.0.0.1:{Port}",
        capabilities = new[] { "mcp", "mainWindow", "widgets" },
        mcp = new
        {
            transport = "http",
            url = $"http://127.0.0.1:{_mcpPort}/mcp",
        },
        mainWindow = new
        {
            title = "事序",
            width = 1180,
            height = 760,
        },
        widgets = new object[]
        {
            new
            {
                id = "calendar",
                displayName = "日历",
                description = "月视图、日程、任务截止与假期",
                entry = "/widgets/calendar",
                size = new { width = 840, height = 780 },
                minSize = new { width = 680, height = 560 },
                defaultVisible = true,
            },
            new
            {
                id = "tasks",
                displayName = "任务",
                description = "未完成任务、优先级 / 截止排序、项目迭代筛选",
                entry = "/widgets/tasks",
                size = new { width = 480, height = 680 },
                minSize = new { width = 360, height = 420 },
                defaultVisible = true,
                // 设置表单 Schema 子集（widget-contract §8）：宿主渲染「显示条数」，
                // 存进 widgets.json 后以 ?rows=N 拼到 entry 上。
                config = new
                {
                    type = "object",
                    properties = new
                    {
                        rows = new
                        {
                            type = "integer",
                            title = "显示条数",
                            description = "任务列表最多显示几行",
                            minimum = PlannerConstants.WidgetTaskRowsMin,
                            maximum = PlannerConstants.WidgetTaskRowsMax,
                            @default = PlannerConstants.WidgetTaskRowsDefault,
                        },
                    },
                },
            },
        },
    };

    private void WriteConnectionFile()
    {
        var payload = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["appId"] = _appId,
            ["displayName"] = "事序",
            ["version"] = PlannerConstants.AppVersion,
            ["kind"] = "app",
            ["executable"] = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SDemo.Planner.exe"),
            ["arguments"] = new JsonArray(),
            ["workingDirectory"] = AppContext.BaseDirectory,
            ["port"] = Port,
            ["baseUrl"] = $"http://127.0.0.1:{Port}",
            ["token"] = _token,
            ["pid"] = Environment.ProcessId,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["state"] = "running",
            ["mcp"] = new JsonObject
            {
                ["transport"] = "http",
                ["url"] = $"http://127.0.0.1:{_mcpPort}/mcp",
            },
            ["mainWindow"] = new JsonObject
            {
                ["title"] = "事序",
                ["width"] = 1180,
                ["height"] = 760,
            },
            ["widgets"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "calendar",
                    ["displayName"] = "日历",
                    ["description"] = "月视图、日程、任务截止与假期",
                    ["entry"] = "/widgets/calendar",
                    ["size"] = new JsonObject { ["width"] = 840, ["height"] = 780 },
                    ["minSize"] = new JsonObject { ["width"] = 680, ["height"] = 560 },
                    ["defaultVisible"] = true,
                },
                new JsonObject
                {
                    ["id"] = "tasks",
                    ["displayName"] = "任务",
                    ["description"] = "未完成任务、优先级 / 截止排序、项目迭代筛选",
                    ["entry"] = "/widgets/tasks",
                    ["size"] = new JsonObject { ["width"] = 480, ["height"] = 680 },
                    ["minSize"] = new JsonObject { ["width"] = 360, ["height"] = 420 },
                    ["defaultVisible"] = true,
                    ["config"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["rows"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["title"] = "显示条数",
                                ["description"] = "任务列表最多显示几行",
                                ["minimum"] = PlannerConstants.WidgetTaskRowsMin,
                                ["maximum"] = PlannerConstants.WidgetTaskRowsMax,
                                ["default"] = PlannerConstants.WidgetTaskRowsDefault,
                            },
                        },
                    },
                },
            },
        };

        WriteAtomic(ConnectionFilePath(), payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private void MarkStopped()
    {
        var path = ConnectionFilePath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null)
            {
                return;
            }

            node["state"] = "stopped";
            node["stoppedAt"] = DateTimeOffset.UtcNow.ToString("O");
            node["pid"] = 0;
            node.Remove("token");
            WriteAtomic(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            PlannerLog.Warn($"写 stopped 状态失败：{ex.Message}");
        }
    }

    private string ConnectionFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDemo", "Hub", "apps");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, _appId + ".json");
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class NullThemeHost : IThemeHost
    {
        public void Apply(bool dark)
        {
        }
    }

    private sealed class ServerAppLifecycle : IAppLifecycle
    {
        public Action? ExitRequested { get; set; }

        public void Exit()
        {
            // Blazor Server 组件不负责退出整个进程；退出仍由托盘/主窗口控制。
        }
    }
}
