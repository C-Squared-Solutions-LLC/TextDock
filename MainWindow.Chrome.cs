using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TextDock;

// Window frame: the caption is removed by trimming WM_NCCALCSIZE, while the left, right and
// bottom sizing borders stay non-client. On Windows 11 those borders are invisible, so the
// window gets native resize handles, snap, shadow and rounded corners with no visible frame.
public partial class MainWindow
{
    internal const double TitleBarHeight = 30;

    private static readonly Color LightDefault = Color.FromRgb(0xF8, 0xFA, 0xFD);
    private static readonly Color DarkDefault = Color.FromRgb(0x1F, 0x1F, 0x1F);

    private Color _chromeBg;
    private bool _dark;
    private bool _themed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_NCCALCSIZE:
                return OnNcCalcSize(wParam, lParam, ref handled);
            case Native.WM_NCHITTEST:
                return OnNcHitTest(lParam, ref handled);
            case Native.WM_HOTKEY when wParam.ToInt64() == HotkeyId:
                handled = true;
                ToggleFromHotkey();
                break;
        }
        return IntPtr.Zero;
    }

    private IntPtr OnNcCalcSize(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (wParam == IntPtr.Zero) return IntPtr.Zero;

        var before = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(lParam);
        int top = before.rgrc0.Top;

        // Let Windows lay out the standard frame, then give the caption strip back to the client.
        Native.DefWindowProc(_hwnd, Native.WM_NCCALCSIZE, wParam, lParam);
        var after = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(lParam);

        // A maximized window hangs over the monitor edge by the frame thickness; keep the title bar on screen.
        after.rgrc0.Top = top + (Native.IsZoomed(_hwnd) ? Native.FrameThickness(_hwnd) : 0);
        Marshal.StructureToPtr(after, lParam, false);

        handled = true;
        return IntPtr.Zero;
    }

    private IntPtr OnNcHitTest(IntPtr lParam, ref bool handled)
    {
        // The side and bottom borders are still real non-client area.
        int hit = Native.DefWindowProc(_hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam).ToInt32();
        if (hit != Native.HTCLIENT)
        {
            handled = true;
            return new IntPtr(hit);
        }

        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF));
        int y = unchecked((short)((lp >> 16) & 0xFFFF));

        // The top border now sits inside the client area; carve out a resize strip by hand.
        if (WindowState == WindowState.Normal && Native.GetWindowRect(_hwnd, out var rect))
        {
            int band = Math.Max(4, Native.FrameThickness(_hwnd) / 2);
            if (y < rect.Top + band)
            {
                handled = true;
                int corner = band * 4;
                if (x < rect.Left + corner) return new IntPtr(Native.HTTOPLEFT);
                if (x >= rect.Right - corner) return new IntPtr(Native.HTTOPRIGHT);
                return new IntPtr(Native.HTTOP);
            }
        }

        var p = PointFromScreen(new Point(x, y));
        if (p.Y >= 0 && p.Y < TitleBarHeight && !IsOverTitleButton(p))
        {
            handled = true;
            return new IntPtr(Native.HTCAPTION);
        }
        return IntPtr.Zero;
    }

    private bool IsOverTitleButton(Point p)
    {
        if (InputHitTest(p) is not DependencyObject hit) return false;
        for (DependencyObject? d = hit; d is not null && d != this; d = ParentOf(d))
        {
            if (d is ButtonBase) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>Recolors the title bar to match the page background reported by inject.js.</summary>
    internal void ApplyChromeTheme(Color bg, bool dark)
    {
        if (_themed && bg == _chromeBg && dark == _dark) return;
        _themed = true;
        _chromeBg = bg;
        _dark = dark;

        var fg = dark ? Color.FromRgb(0xE3, 0xE3, 0xE3) : Color.FromRgb(0x1F, 0x1F, 0x1F);
        SetBrush("ChromeBg", bg);
        SetBrush("ChromeFg", fg);
        SetBrush("ChromeFgDim", Color.FromArgb(0x80, fg.R, fg.G, fg.B));
        SetBrush("ChromeHover", dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        SetBrush("ChromePressed", dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));
        SetBrush("ChromeAccent", dark ? Color.FromRgb(0x5E, 0xEA, 0xD4) : Color.FromRgb(0x0F, 0x76, 0x6E));

        if (_hwnd != IntPtr.Zero)
        {
            Native.SetDarkFrame(_hwnd, dark);
            ApplyAlertSurfaces(); // keep an orange alert border through theme switches
        }
        try
        {
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B);
        }
        catch (Exception ex)
        {
            Log.Error("webview background update failed", ex);
        }
        _tray.SetDark(dark);
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }
}
