using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TextDock;

public partial class MainWindow : Window
{
    internal const string AppName = "TextDock";
    private const int HotkeyId = 0x474D;

    private readonly AppSettings _settings;
    private readonly StartupOptions _options;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _pinTimer;
    private IntPtr _hwnd;
    private bool _exiting;
    private bool _hotkeyActive;
    private long _lastDeactivatedTick;
    private WindowState _shownState = WindowState.Normal;
    private int _pinAttempts;
    private bool _pinUnsupported;

    internal MainWindow(AppSettings settings, StartupOptions options)
    {
        _settings = settings;
        _options = options;
        InitializeComponent();

        bool dark = Native.SystemUsesDarkTheme();
        _tray = new TrayIcon(this, settings, dark);
        ApplyChromeTheme(dark ? DarkDefault : LightDefault, dark);

        Icon = Icons.WindowIcon();
        Topmost = settings.AlwaysOnTop;
        TopmostToggle.IsChecked = settings.AlwaysOnTop;
        DesktopsToggle.IsChecked = settings.AllDesktops;
        if (settings.Bounds is not null) WindowStartupLocation = WindowStartupLocation.Manual;

        _pinTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _pinTimer.Tick += (_, _) => TryPin();

        Loaded += (_, _) => UpdateTitleIcon();
        DpiChanged += (_, _) => UpdateTitleIcon();
        Deactivated += (_, _) => _lastDeactivatedTick = Environment.TickCount64;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _shownState = WindowState;
            ApplyAlertSurfaces(); // minimized: taskbar flash instead of the title bar pulse
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) SchedulePin();
            ApplyAlertSurfaces(); // hidden: tray icon blink instead
        };
    }

    internal bool HotkeyActive => _hotkeyActive;

    internal void Start()
    {
        // Creates the HWND and runs OnSourceInitialized (hooks, saved placement) without showing anything.
        new WindowInteropHelper(this).EnsureHandle();

        if (_options.StartHidden)
        {
            // WebView2's host window is only built once the window has been shown, so show it
            // minimized without activation and hide it straight away.
            ShowActivated = false;
            WindowState = WindowState.Minimized;
            Show();
            Hide();
            ShowActivated = true;
        }
        else
        {
            WindowState = _shownState;
            Show();
        }
        _ = InitWebAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        RestorePlacement();
        Native.SetRoundedCorners(_hwnd);
        Native.SetDarkFrame(_hwnd, _dark);
        // WM_NCCALCSIZE is hooked now; have Windows recompute the frame so the caption goes away.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        RegisterHotkey();
    }

    // ---- show / hide --------------------------------------------------------------------

    internal void ShowAndFocus()
    {
        if (WindowState == WindowState.Minimized) WindowState = _shownState;
        if (!IsVisible) Show();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        if (_settings.AlwaysOnTop)
        {
            // Re-assert so we land above other topmost windows that came up since.
            Topmost = false;
            Topmost = true;
        }
        FocusWeb();
    }

    internal void HideToTray()
    {
        SavePlacement();
        _settings.Save();
        Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            _settings.Save();
            _tray.ShowInfo($"{AppName} is still running", _hotkeyActive
                ? $"Click the tray icon or press {_settings.Hotkey} to bring it back."
                : "Click the tray icon to bring it back.");
        }
    }

    /// <summary>Tray click. Clicking the tray deactivates us first, so "was just active" counts as active.</summary>
    internal void ToggleFromTray()
    {
        bool recentlyActive = IsActive || Environment.TickCount64 - _lastDeactivatedTick < 500;
        bool inFront = IsVisible && WindowState != WindowState.Minimized && (Topmost || recentlyActive);
        if (inFront) HideToTray();
        else ShowAndFocus();
    }

    private void ToggleFromHotkey()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive) HideToTray();
        else ShowAndFocus();
    }

    internal void ExitApp()
    {
        _exiting = true;
        Close();
    }

    internal void PrepareForShutdown()
    {
        _exiting = true;
        SavePlacement();
        _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _exiting = true;
        SavePlacement();
        _settings.Save();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyActive) Native.UnregisterHotKey(_hwnd, HotkeyId);
        _pinTimer.Stop();
        DisposeWeb();
        _tray.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    // ---- title bar ---------------------------------------------------------------------

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var corner = MenuButton.PointToScreen(new Point(MenuButton.ActualWidth, MenuButton.ActualHeight));
        _tray.ShowMenuAt(new System.Drawing.Point((int)corner.X, (int)corner.Y));
    }

    private void DesktopsToggle_Click(object sender, RoutedEventArgs e) => SetAllDesktops(DesktopsToggle.IsChecked == true);

    private void TopmostToggle_Click(object sender, RoutedEventArgs e) => SetAlwaysOnTop(TopmostToggle.IsChecked == true);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateTitleIcon()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        TitleIcon.Source = Icons.Frame((int)Math.Round(16 * scale));
    }

    // ---- always on top / all desktops ---------------------------------------------------

    internal void SetAlwaysOnTop(bool on)
    {
        _settings.AlwaysOnTop = on;
        _settings.Save();
        Topmost = on;
        TopmostToggle.IsChecked = on;
    }

    internal void SetAllDesktops(bool on)
    {
        _settings.AllDesktops = on;
        _settings.Save();
        DesktopsToggle.IsChecked = on;
        if (_pinUnsupported)
        {
            ApplyToolWindowMode(on);
            return;
        }
        if (on)
        {
            SchedulePin();
            return;
        }
        _pinTimer.Stop();
        if (VirtualDesktop.TrySetPinned(_hwnd, false, out var error)) Log.Info("unpinned from all desktops");
        else Log.Error("unpin failed: " + error);
    }

    /// <summary>
    /// Hidden windows lose their shell "view", and a re-shown window comes back unpinned,
    /// so this runs every time the window becomes visible. The view also appears a beat after
    /// the window does, hence the retry timer.
    /// </summary>
    private void SchedulePin()
    {
        if (!_settings.AllDesktops || _pinUnsupported || _hwnd == IntPtr.Zero) return;
        _pinAttempts = 0;
        _pinTimer.Stop();
        _pinTimer.Start();
    }

    private void TryPin()
    {
        if (!IsVisible || !_settings.AllDesktops)
        {
            _pinTimer.Stop();
            return;
        }
        _pinAttempts++;
        if (VirtualDesktop.TrySetPinned(_hwnd, true, out var error))
        {
            _pinTimer.Stop();
            Log.Info($"pinned to all desktops (attempt {_pinAttempts}, verified={VirtualDesktop.IsPinned(_hwnd)})");
            return;
        }
        if (_pinAttempts < 20) return;

        _pinTimer.Stop();
        Log.Error($"desktop pinning unavailable ({error}); using tool-window mode instead");
        _pinUnsupported = true;
        ApplyToolWindowMode(true);
    }

    /// <summary>
    /// Fallback: the virtual desktop manager ignores tool windows, so they show on every desktop.
    /// They also drop out of the taskbar and Alt+Tab, which is why this isn't the default.
    /// </summary>
    private void ApplyToolWindowMode(bool on)
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        long next = on ? (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW : ex & ~Native.WS_EX_TOOLWINDOW;
        if (next == ex) return;
        bool visible = IsVisible;
        if (visible) Hide();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(next));
        if (visible) Show();
    }

    // ---- hotkey -----------------------------------------------------------------------

    private void RegisterHotkey()
    {
        if (!Hotkey.TryParse(_settings.Hotkey, out var modifiers, out var vk))
        {
            Log.Error($"hotkey '{_settings.Hotkey}' not understood");
            return;
        }
        _hotkeyActive = Native.RegisterHotKey(_hwnd, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk);
        if (_hotkeyActive) Log.Info($"hotkey {_settings.Hotkey} registered");
        else Log.Error($"hotkey {_settings.Hotkey} is taken by another app (error {Marshal.GetLastWin32Error()})");
    }

    // ---- placement --------------------------------------------------------------------

    private void RestorePlacement()
    {
        var b = _settings.Bounds;
        if (b is null || b.Right - b.Left < 200 || b.Bottom - b.Top < 150) return;
        var wp = new Native.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<Native.WINDOWPLACEMENT>(),
            showCmd = Native.SW_HIDE, // position only; Start() decides when to show
            rcNormalPosition = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom },
        };
        // SetWindowPlacement pulls the rect back on screen if that monitor is gone.
        Native.SetWindowPlacement(_hwnd, ref wp);
        _shownState = b.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SavePlacement()
    {
        if (_hwnd == IntPtr.Zero) return;
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(_hwnd, ref wp)) return;
        var r = wp.rcNormalPosition;
        _settings.Bounds = new WindowBounds
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
            Maximized = (WindowState == WindowState.Minimized ? _shownState : WindowState) == WindowState.Maximized,
        };
    }
}
