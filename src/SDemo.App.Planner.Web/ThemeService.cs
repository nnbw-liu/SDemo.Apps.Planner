using Microsoft.JSInterop;

namespace SDemo.App.Planner.Web;

/// <summary>主题偏好：默认跟随系统，也可固定深色/浅色。</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>宿主窗口外壳（标题栏）跟着 Web 主题走。</summary>
public interface IThemeHost
{
    void Apply(bool dark);
}

/// <summary>
/// 主题状态。持久化在 localStorage，解析结果写 `document.documentElement.dataset.theme`；
/// 系统配色变化时由 plannerTheme 回调，宿主标题栏同步换色。
/// </summary>
public sealed class ThemeService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IThemeHost _host;
    private DotNetObjectReference<ThemeService>? _self;

    public ThemeService(IJSRuntime js, IThemeHost host)
    {
        _js = js;
        _host = host;
    }

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public bool IsDark { get; private set; } = true;

    public bool Initialized { get; private set; }

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (Initialized)
        {
            return;
        }

        _self ??= DotNetObjectReference.Create(this);
        var state = await _js.InvokeAsync<ThemeState>("plannerTheme.init", _self);
        Mode = state.ParsedMode;
        IsDark = state.Dark;
        Initialized = true;
        _host.Apply(IsDark);
        Changed?.Invoke();
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        Mode = mode;
        var state = await _js.InvokeAsync<ThemeState>("plannerTheme.set", mode.ToString().ToLowerInvariant());
        IsDark = state.Dark;
        _host.Apply(IsDark);
        Changed?.Invoke();
    }

    /// <summary>系统配色变化（仅「跟随系统」时生效）。</summary>
    [JSInvokable]
    public void OnSystemChanged(bool dark)
    {
        if (Mode != ThemeMode.System || IsDark == dark)
        {
            return;
        }

        IsDark = dark;
        _host.Apply(dark);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_self is null)
        {
            return;
        }

        try
        {
            await _js.InvokeVoidAsync("plannerTheme.dispose");
        }
        catch (JSDisconnectedException)
        {
            // 关闭窗口时 circuit 已断开，无需处理。
        }

        _self.Dispose();
        _self = null;
    }

    public sealed class ThemeState
    {
        /// <summary>JS 传来的是 "system" / "light" / "dark"。</summary>
        public string Mode { get; init; } = "system";

        public bool Dark { get; init; }

        public ThemeMode ParsedMode =>
            Enum.TryParse<ThemeMode>(Mode, ignoreCase: true, out var parsed) ? parsed : ThemeMode.System;
    }
}
