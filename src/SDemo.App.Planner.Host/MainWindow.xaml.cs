using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using SDemo.App.Planner.Web;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 主窗口只负责承载 BlazorWebView。页面、弹窗与交互都在
/// SDemo.App.Planner.Web 里，WPF 这一层不复制任何业务视图逻辑。
/// 自绘标题栏：无系统边框 + WindowChrome 保留拖拽/缩放/贴边。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        FitToWorkArea();

        WebView.Services = services;
        WebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes),
        });

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            TryEnableRoundedCorners(handle);
        };
        StateChanged += (_, _) => UpdateCaptionGlyphs();
        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            PlannerLog.Info($"主窗口 actual={ActualWidth:F0}x{ActualHeight:F0} dpi={dpi.DpiScaleX:F2} work={SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0}");
        };
    }

    private void FitToWorkArea()
    {
        // 1280×840 是设计尺寸；小屏/高缩放下先收敛到可用工作区，避免标题栏或底部被裁掉。
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateCaptionGlyphs()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void TryEnableRoundedCorners(IntPtr handle)
    {
        try
        {
            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2（Win11）。
            var preference = 2;
            _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 没有这个属性，直角也能用。
        }
    }

    /// <summary>Web 端主题变化时同步标题栏与窗口边框（由 HostThemeBridge 调度到 UI 线程）。</summary>
    public void ApplyTheme(bool dark)
    {
        SetBrush("ChromeBg", dark ? "#0B0F16" : "#EEF1F6");
        SetBrush("ChromeTitleBar", dark ? "#0D1219" : "#F7F9FC");
        SetBrush("ChromeBorder", dark ? "#12FFFFFF" : "#14000000");
        SetBrush("ChromeText", dark ? "#E9EDF5" : "#1A2233");
        SetBrush("ChromeMuted", dark ? "#5D687C" : "#7A879C");
        SetBrush("ChromeCaptionFg", dark ? "#8490A5" : "#5B6675");
        SetBrush("ChromeHover", dark ? "#1B2330" : "#E4E9F1");
        SetBrush("ChromeHoverFg", dark ? "#E9EDF5" : "#111827");
        SetBrush("ChromePressed", dark ? "#232C3C" : "#D8DFEA");

        try
        {
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20，让系统绘制的边框/阴影跟主题一致。
            var useDark = dark ? 1 : 0;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                _ = DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // 非 Win11 忽略。
        }
    }

    private void SetBrush(string key, string hex)
    {
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = (Color)ColorConverter.ConvertFromString(hex);
            return;
        }

        // DynamicResource 已被内联进控件时替换资源项，后续引用会拿到新画刷。
        var color = (Color)ColorConverter.ConvertFromString(hex);
        Resources[key] = new SolidColorBrush(color);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO)
        {
            return IntPtr.Zero;
        }

        // WindowChrome 无边框最大化会越过工作区，这里把最大化尺寸钉在工作区内。
        var monitor = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return IntPtr.Zero;
        }

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
