using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace SDemo.App.Planner.Host;

/// <summary>
/// 系统托盘图标。使用 Win32 Shell_NotifyIcon，避免引入 WinForms
/// （本项目曾因 UseWindowsForms 与 WPF 的 Application/MessageBox 等类型冲突）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 22;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint IDI_APPLICATION = 32512;

    private readonly Action _show;
    private readonly Action _exit;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly ContextMenu _menu;
    private NOTIFYICONDATA _data;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Action show, Action exit)
    {
        _show = show;
        _exit = exit;

        var parameters = new HwndSourceParameters("PlannerTrayIcon")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        (_icon, _ownsIcon) = LoadAppIcon();
        _menu = BuildMenu();

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "事序 · 项目与日常",
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        if (!_added)
        {
            PlannerLog.Warn("托盘图标注册失败。");
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu
        {
            HasDropShadow = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Template = (ControlTemplate)XamlReader.Parse(MenuTemplate),
        };

        menu.Resources.Add(typeof(MenuItem), XamlReader.Parse(MenuItemStyle));
        menu.Resources.Add(typeof(Separator), XamlReader.Parse(SeparatorStyle));

        var header = new StackPanel { Margin = new Thickness(9, 7, 12, 9) };
        header.Children.Add(new TextBlock
        {
            Text = "事序",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#e9edf5"),
        });
        header.Children.Add(new TextBlock
        {
            Text = "项目 · 迭代 · 日常",
            FontSize = 9.8,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Brush("#5d687c"),
        });

        var open = new MenuItem
        {
            Header = "打开事序",
            Icon = Glyph("\uE8A7", "#8ba4ff"),
            InputGestureText = "双击托盘图标",
        };
        open.Click += (_, _) => _show();

        var exit = new MenuItem
        {
            Header = "退出",
            Icon = Glyph("\uE7E8", "#ff8f9e"),
            Foreground = Brush("#ffb3bd"),
        };
        exit.Click += (_, _) => _exit();

        menu.Items.Add(header);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Glyph(string glyph, string color) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 13,
        Foreground = Brush(color),
    };

    private const string MenuTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="{x:Type ContextMenu}">
          <Border Margin="12" Padding="6" CornerRadius="12"
                  Background="#171c27" BorderBrush="#2b3547" BorderThickness="1"
                  SnapsToDevicePixels="True">
            <Border.Effect>
              <DropShadowEffect BlurRadius="22" ShadowDepth="4" Direction="270"
                                Opacity="0.55" Color="#000000" />
            </Border.Effect>
            <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle" />
          </Border>
        </ControlTemplate>
        """;

    private const string MenuItemStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type MenuItem}">
          <Setter Property="OverridesDefaultStyle" Value="True" />
          <Setter Property="Foreground" Value="#dfe6f2" />
          <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI" />
          <Setter Property="FontSize" Value="12.5" />
          <Setter Property="Cursor" Value="Hand" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="{x:Type MenuItem}">
                <Border x:Name="Bd" Margin="1" Padding="9,6" CornerRadius="8"
                        Background="Transparent" SnapsToDevicePixels="True">
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="18" />
                      <ColumnDefinition Width="*" />
                      <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <ContentPresenter Grid.Column="0" ContentSource="Icon"
                                      HorizontalAlignment="Center" VerticalAlignment="Center" />
                    <ContentPresenter Grid.Column="1" ContentSource="Header" Margin="9,0,16,0"
                                      VerticalAlignment="Center" RecognizesAccessKey="True" />
                    <TextBlock Grid.Column="2" Text="{TemplateBinding InputGestureText}"
                               Foreground="#5d687c" FontSize="9.8" VerticalAlignment="Center" />
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="#252d3d" />
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.45" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private const string SeparatorStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type Separator}">
          <Setter Property="OverridesDefaultStyle" Value="True" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="{x:Type Separator}">
                <Border Height="1" Margin="9,4" Background="#242d3d" />
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var mouseMessage = lParam.ToInt32() & 0xFFFF;
            switch (mouseMessage)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _show();
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);

        // GetCursorPos 返回物理像素；WPF 的 AbsolutePoint 用的是 DIP，
        // 高 DPI 下不换算会偏移到鼠标右下方。
        var toDip = _source.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = toDip.Transform(new Point(point.X, point.Y));

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = dip.X;
        _menu.VerticalOffset = dip.Y;

        // 让菜单在点击空白处时能正常关闭；必须早于打开菜单，
        // 否则 ForegroundWindow 变化会把刚弹出的菜单关掉。
        SetForegroundWindow(_source.Handle);
        _menu.IsOpen = true;
    }

    private static (IntPtr Icon, bool Owns) LoadAppIcon()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe) && ExtractIconEx(exe, 0, out _, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            return (small, true);
        }

        return (LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION), false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
