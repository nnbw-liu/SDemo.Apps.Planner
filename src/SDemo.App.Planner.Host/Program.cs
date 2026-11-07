using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SDemo.App.Planner.Contracts;
using SDemo.App.Planner.Core;
using SDemo.App.Planner.Mcp;
using SDemo.App.Planner.Storage;
using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

internal static class Program
{
    private const string MutexName = "SDemo.App.sdemo.planner";
    private const string AppTokenEnvironmentVariable = "SDEMO_APP_TOKEN";
    private const string HostUrlEnvironmentVariable = "SDEMO_HOST_URL";

    [STAThread]
    private static int Main(string[] args)
    {
        var headless = HasFlag(args, "--headless");
        var widgetOnly = HasFlag(args, "--widget");
        var instance = GetOption(args, "--instance");
        var databasePath = GetOption(args, "--db") ?? PlannerPaths.DatabasePath;
        var hostPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostUrlEnvironmentVariable));

        var mutexName = instance is null ? MutexName : $"{MutexName}.{instance}";
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            PlannerLog.Warn("已有实例在运行，第二个进程退出。");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            PlannerLog.Error("未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            PlannerLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        var token = Environment.GetEnvironmentVariable(AppTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        PlannerStore store;
        try
        {
            store = new PlannerStore(databasePath);
            store.Migrate();
        }
        catch (Exception ex)
        {
            PlannerLog.Error($"数据库迁移失败：{databasePath}", ex);
            if (!headless)
            {
                MessageBox.Show($"planner.db 迁移失败：\n{ex.Message}\n\n路径：{databasePath}", "sdemo.planner",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return 2;
        }

        var service = new PlannerService(store);
        try
        {
            var purged = service.PurgeExpired();
            if (purged > 0)
            {
                PlannerLog.Info($"启动清理：{purged} 条超过回收站保留期");
            }
        }
        catch (Exception ex)
        {
            PlannerLog.Warn($"启动清理失败：{ex.Message}");
        }

        var exportPath = GetOption(args, "--export-ics");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            try
            {
                var full = service.ExportIcsToFile(exportPath);
                PlannerLog.Info($"ICS 已导出：{full}");
                Console.WriteLine(full);
                return 0;
            }
            catch (Exception ex)
            {
                PlannerLog.Error($"ICS 导出失败：{exportPath}", ex);
                Console.Error.WriteLine(ex.Message);
                return 4;
            }
        }

        var mcp = new McpServer(service);
        using var server = new AppServer(token, service, mcp, PlannerConstants.AppId, PlannerConstants.AppVersion);

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            PlannerLog.Error("app 面启动失败", ex);
            if (!headless)
            {
                MessageBox.Show($"无法启动本地 MCP 服务：\n{ex.Message}", "sdemo.planner",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return 3;
        }

        if (headless)
        {
            using var done = new ManualResetEventSlim(false);
            server.ShutdownRequested = done.Set;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                done.Set();
            };

            PlannerLog.Info($"headless 模式就绪（db={databasePath}）");
            done.Wait();
            return 0;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(service);
        services.AddSingleton<PlannerUi>();
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<HolidaySyncService>();
        var lifecycle = new HostAppLifecycle();
        services.AddSingleton<IAppLifecycle>(lifecycle);
        services.AddSingleton<IThemeHost, HostThemeBridge>();
        services.AddScoped<ThemeService>();
        using var provider = services.BuildServiceProvider();

        return RunDesktop(server, service, token, provider, lifecycle, widgetOnly, hostPresent);
    }

    private static int RunDesktop(
        AppServer server,
        PlannerService service,
        string token,
        IServiceProvider services,
        HostAppLifecycle lifecycle,
        bool widgetOnly,
        bool hostPresent)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow? main = null;
        WidgetWindow? widget = null;
        TrayIcon? tray = null;
        var allowClose = false;

        var reminders = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        reminders.Tick += (_, _) => PumpReminders(service);

        void WireMain(MainWindow window)
        {
            window.Closing += (_, e) =>
            {
                if (allowClose)
                {
                    return;
                }

                // 关闭主窗口只收进托盘；提醒依赖进程继续活着。
                e.Cancel = true;
                window.Hide();
                PlannerLog.Info("主窗口已收进托盘，日常提醒继续运行");
            };

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(main, window))
                {
                    main = null;
                }
            };
        }

        void OpenMain()
        {
            if (main is null)
            {
                main = new MainWindow(services);
                WireMain(main);
            }

            main.Show();
            if (main.WindowState == WindowState.Minimized)
            {
                main.WindowState = WindowState.Normal;
            }

            main.Activate();
        }

        widget = new WidgetWindow(service, OpenMain);

        if (widgetOnly)
        {
            // 仅组件模式：给宿主/调试用，不创建主窗口。
        }
        else if (!hostPresent)
        {
            main = new MainWindow(services);
            WireMain(main);
        }
        else
        {
            // 宿主拉起时只显示组件；主窗口按需从组件里打开。
        }

        void ExitApp()
        {
            allowClose = true;
            tray?.Dispose();
            tray = null;
            widget?.Close();
            main?.Close();
            app.Shutdown();
        }

        lifecycle.ExitRequested = () => app.Dispatcher.BeginInvoke(new Action(ExitApp));

        server.CommandReceived = widget.ApplyCommand;
        server.StateProvider = widget.GetState;
        server.ShutdownRequested = () => app.Dispatcher.BeginInvoke(new Action(ExitApp));

        var registration = new HostRegistration(token);
        widget.Show();
        if (hostPresent)
        {
            // 宿主负责显示与摆放；这里先建窗再隐藏，避免抢焦点。
            widget.Hide();
        }
        var hwnd = (long)widget.Hwnd;
        _ = registration.RegisterQuietly(hwnd);
        widget.Closed += (_, _) => _ = registration.UnregisterQuietly(hwnd);

        if (main is not null)
        {
            app.MainWindow = main;
            if (!hostPresent)
            {
                widget.Show();
            }
        }
        else
        {
            app.MainWindow = widget;
        }

        tray = new TrayIcon(OpenMain, ExitApp);

        try
        {
            reminders.Start();
            PumpReminders(service);

            // 必须让 Application 的事件循环先启动，再让窗口承载 WebView2；
            // 提前 Show() 会触发 EnsureCoreWebView2Async 的时序异常。
            if (main is not null)
            {
                app.Run(main);
            }
            else
            {
                app.Run();
            }
        }
        finally
        {
            reminders.Stop();
            tray?.Dispose();
            tray = null;
            server.Stop();
            AppServer.DeleteRuntimeFile(PlannerConstants.AppId);
            PlannerLog.Info("应用退出");
        }

        return 0;
    }

    private static void PumpReminders(PlannerService service)
    {
        try
        {
            var due = service.CollectDueReminders();
            if (due.Count > 0)
            {
                PlannerLog.Info($"触发 {due.Count} 条日常提醒：{string.Join("、", due.Select(r => r.Title))}");
                ReminderToastWindow.ShowReminders(due);
            }
        }
        catch (Exception ex)
        {
            PlannerLog.Error("提醒检查失败", ex);
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..].Trim('"');
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1].Trim('"');
            }
        }

        return null;
    }
}
