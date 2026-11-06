using System;
using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TextDock;

/// <summary>
/// Tray icon plus the one menu used everywhere: right-click on the tray icon and the
/// title bar's "..." button both open it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _window;
    private readonly AppSettings _settings;
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Forms.Timer _alertTimer = new() { Interval = 650 };
    private Drawing.Icon? _unreadIcon;
    private Drawing.Icon? _alertIcon;
    private int _unreadCount;
    private bool _alertPhase;
    private bool _balloonIsMessage;
    private bool _dark;

    private readonly Forms.ToolStripMenuItem _show;
    private readonly Forms.ToolStripMenuItem _topmost;
    private readonly Forms.ToolStripMenuItem _desktops;
    private readonly Forms.ToolStripMenuItem _compact;
    private readonly Forms.ToolStripMenuItem _zoom;
    private readonly Forms.ToolStripMenuItem _notifications;
    private readonly Forms.ToolStripMenuItem _importantAlerts;
    private readonly Forms.ToolStripMenuItem _closeToTray;
    private readonly Forms.ToolStripMenuItem _autostart;

    public TrayIcon(MainWindow window, AppSettings settings, bool dark)
    {
        _window = window;
        _settings = settings;
        _dark = dark;
        _icon = Icons.TrayIcon();

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Drawing.Font("Segoe UI", 9f),
        };

        _show = Item("Show", () =>
        {
            if (_window.IsVisible && _window.WindowState != WindowState.Minimized) _window.HideToTray();
            else _window.ShowAndFocus();
        });
        _show.Font = new Drawing.Font(_menu.Font, Drawing.FontStyle.Bold);
        _topmost = Item("Always on top", () => _window.SetAlwaysOnTop(!_settings.AlwaysOnTop));
        _desktops = Item("Show on all desktops", () => _window.SetAllDesktops(!_settings.AllDesktops));
        _compact = Item("Compact layout", () => _window.SetCompact(!_settings.CompactStyle));
        _zoom = new Forms.ToolStripMenuItem("Zoom");
        foreach (var pct in new[] { 75, 80, 90, 100, 110, 125 })
        {
            double z = pct / 100.0;
            var zi = Item($"{pct}%", () => _window.SetZoom(z));
            zi.Tag = z;
            _zoom.DropDownItems.Add(zi);
        }
        if (_zoom.DropDown is Forms.ToolStripDropDownMenu zoomMenu)
        {
            zoomMenu.ShowImageMargin = false;
            zoomMenu.ShowCheckMargin = true;
        }
        _notifications = Item("Message notifications", () => _window.SetNotifications(!_settings.Notifications));
        _importantAlerts = Item("Blink for important conversations",
            () => _window.SetImportantAlerts(!_settings.ImportantAlerts));
        _closeToTray = Item("Close button hides to tray", () =>
        {
            _settings.CloseToTray = !_settings.CloseToTray;
            _settings.Save();
        });
        _autostart = Item("Start with Windows", () => Autostart.Set(!Autostart.IsEnabled));

        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _show,
            new Forms.ToolStripSeparator(),
            _topmost,
            _desktops,
            _compact,
            _zoom,
            new Forms.ToolStripSeparator(),
            _notifications,
            _importantAlerts,
            _closeToTray,
            _autostart,
            new Forms.ToolStripSeparator(),
            Item("Select conversations", _window.StartSelecting, "Ctrl+click"),
            Item("Select unknown senders", _window.SelectUnknownSenders),
            new Forms.ToolStripSeparator(),
            Item("Edit custom CSS…", _window.EditCustomCss),
            Item("Reload", _window.ReloadPage, "F5"),
            Item("Developer tools", _window.OpenDevTools, "F12"),
            new Forms.ToolStripSeparator(),
            Item($"Quit {MainWindow.AppName}", _window.ExitApp),
        });
        _menu.Opening += (_, _) => SyncMenu();
        ApplyMenuTheme();

        _alertTimer.Tick += (_, _) =>
        {
            _alertPhase = !_alertPhase;
            UpdateIcon();
        };

        _notify = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = MainWindow.AppName,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ToggleFromTray();
        };
        _notify.BalloonTipClicked += (_, _) =>
        {
            if (_balloonIsMessage) _window.OpenFromNotification();
            else _window.ShowAndFocus();
        };
    }

    /// <summary>Opens the menu with its top-right corner at a screen point (device pixels).</summary>
    public void ShowMenuAt(Drawing.Point screenPoint) =>
        _menu.Show(screenPoint, Forms.ToolStripDropDownDirection.BelowLeft);

    public void ShowMessage(string title, string body)
    {
        _balloonIsMessage = true;
        _notify.ShowBalloonTip(6000, Clip(title, 63), Clip(body, 255), Forms.ToolTipIcon.None);
    }

    public void ShowInfo(string title, string body)
    {
        _balloonIsMessage = false;
        _notify.ShowBalloonTip(5000, Clip(title, 63), Clip(body, 255), Forms.ToolTipIcon.Info);
    }

    public void SetUnread(int count)
    {
        _unreadCount = count;
        _notify.Text = count switch
        {
            0 => MainWindow.AppName,
            1 => $"{MainWindow.AppName}: 1 unread",
            _ => $"{MainWindow.AppName}: {count} unread",
        };
        UpdateIcon();
    }

    /// <summary>Blinks an orange dot on the tray icon; used while the window is hidden.</summary>
    public void SetAlert(bool on)
    {
        if (on == _alertTimer.Enabled) return;
        _alertPhase = on;
        if (on) _alertTimer.Start();
        else _alertTimer.Stop();
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        Drawing.Icon icon;
        if (_alertTimer.Enabled && _alertPhase) icon = _alertIcon ??= Icons.WithDot(_icon, Icons.AlertOrange);
        else if (_unreadCount > 0) icon = _unreadIcon ??= Icons.WithDot(_icon, Icons.UnreadRed);
        else icon = _icon;
        if (!ReferenceEquals(_notify.Icon, icon)) _notify.Icon = icon;
    }

    public void SetDark(bool dark)
    {
        if (dark == _dark) return;
        _dark = dark;
        ApplyMenuTheme();
    }

    public void Dispose()
    {
        _alertTimer.Stop();
        _alertTimer.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _unreadIcon?.Dispose();
        _alertIcon?.Dispose();
    }

    private void SyncMenu()
    {
        bool shown = _window.IsVisible && _window.WindowState != WindowState.Minimized;
        _show.Text = shown ? "Hide" : "Show";
        _show.ShortcutKeyDisplayString = _window.HotkeyActive ? _settings.Hotkey : null;
        _topmost.Checked = _settings.AlwaysOnTop;
        _desktops.Checked = _settings.AllDesktops;
        _compact.Checked = _settings.CompactStyle;
        _notifications.Checked = _settings.Notifications;
        _importantAlerts.Checked = _settings.ImportantAlerts;
        _closeToTray.Checked = _settings.CloseToTray;
        _autostart.Checked = Autostart.IsEnabled;
        foreach (Forms.ToolStripItem item in _zoom.DropDownItems)
        {
            if (item is Forms.ToolStripMenuItem mi && mi.Tag is double z)
                mi.Checked = Math.Abs(z - _settings.Zoom) < 0.005;
        }
    }

    private void ApplyMenuTheme()
    {
        var renderer = new MenuRenderer(_dark);
        _menu.Renderer = renderer;
        _zoom.DropDown.Renderer = renderer;
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick, string? shortcut = null)
    {
        var item = new Forms.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static string Clip(string? text, int max)
    {
        text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

/// <summary>Flat menu that follows the page's light/dark theme.</summary>
internal sealed class MenuRenderer : Forms.ToolStripProfessionalRenderer
{
    private readonly Palette _p;

    public MenuRenderer(bool dark) : this(dark ? Palette.Dark : Palette.Light) { }

    private MenuRenderer(Palette palette) : base(new Colors(palette))
    {
        _p = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new Drawing.SolidBrush(_p.Back);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _p.Text : _p.Muted;
        // The shortcut column is drawn through this same hook; mute it.
        if (e.Item is Forms.ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
            && e.Text == mi.ShortcutKeyDisplayString)
            e.TextColor = _p.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.ContentRectangle;
        int y = bounds.Top + bounds.Height / 2;
        using var pen = new Drawing.Pen(_p.Border);
        e.Graphics.DrawLine(pen, bounds.Left + 6, y, bounds.Right - 6, y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var g = e.Graphics;
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = r.Height / 16f;
        using var pen = new Drawing.Pen(_p.Accent, 1.7f * s)
        {
            StartCap = Drawing.Drawing2D.LineCap.Round,
            EndCap = Drawing.Drawing2D.LineCap.Round,
            LineJoin = Drawing.Drawing2D.LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new Drawing.PointF(r.Left + 3.5f * s, r.Top + 8.5f * s),
            new Drawing.PointF(r.Left + 6.5f * s, r.Top + 11.5f * s),
            new Drawing.PointF(r.Left + 12.5f * s, r.Top + 4.5f * s),
        });
    }

    private sealed record Palette(Drawing.Color Back, Drawing.Color Hover, Drawing.Color Border,
        Drawing.Color Text, Drawing.Color Muted, Drawing.Color Accent)
    {
        public static readonly Palette Light = new(
            Drawing.Color.FromArgb(0xFB, 0xFB, 0xFB), Drawing.Color.FromArgb(0xEA, 0xEA, 0xEA),
            Drawing.Color.FromArgb(0xDA, 0xDA, 0xDA), Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F),
            Drawing.Color.FromArgb(0x80, 0x80, 0x80), Drawing.Color.FromArgb(0x0F, 0x76, 0x6E));

        public static readonly Palette Dark = new(
            Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B), Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D),
            Drawing.Color.FromArgb(0x48, 0x48, 0x48), Drawing.Color.FromArgb(0xF0, 0xF0, 0xF0),
            Drawing.Color.FromArgb(0x9A, 0x9A, 0x9A), Drawing.Color.FromArgb(0x5E, 0xEA, 0xD4));
    }

    private sealed class Colors : Forms.ProfessionalColorTable
    {
        private readonly Palette _p;

        public Colors(Palette p)
        {
            _p = p;
            UseSystemColors = false;
        }

        public override Drawing.Color ToolStripDropDownBackground => _p.Back;
        public override Drawing.Color ImageMarginGradientBegin => _p.Back;
        public override Drawing.Color ImageMarginGradientMiddle => _p.Back;
        public override Drawing.Color ImageMarginGradientEnd => _p.Back;
        public override Drawing.Color MenuBorder => _p.Border;
        public override Drawing.Color MenuItemBorder => _p.Hover;
        public override Drawing.Color MenuItemSelected => _p.Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => _p.Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => _p.Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => _p.Hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => _p.Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => _p.Hover;
        public override Drawing.Color SeparatorDark => _p.Border;
        public override Drawing.Color SeparatorLight => _p.Back;
        public override Drawing.Color CheckBackground => _p.Back;
        public override Drawing.Color CheckSelectedBackground => _p.Hover;
        public override Drawing.Color CheckPressedBackground => _p.Hover;
    }
}
