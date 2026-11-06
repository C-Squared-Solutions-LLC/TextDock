using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TextDock;

// Important conversations: the page (inject.js) tracks starred conversations and reports
// which ones have a message from the other side that you haven't answered. While any do,
// the title bar pulses orange, the window border turns orange, a minimized window flashes
// its taskbar button and a window hidden in the tray blinks the tray icon.
public partial class MainWindow
{
    private static readonly Color AlertOrange = Color.FromRgb(0xFB, 0x8C, 0x00);

    // Conversation ids are opaque tokens like "CgiD6BqyNT8UaRIBMg"; refuse anything else.
    private static readonly Regex ConversationId = new("^[A-Za-z0-9_=-]{1,200}$", RegexOptions.CultureInvariant);

    private sealed record AttentionItem(string Id, string Name);

    private List<AttentionItem> _attention = new();
    private bool _attentionBlink;
    private bool _pulsing;

    private void OnAttention(JsonElement msg)
    {
        _attention = msg.GetProperty("items").EnumerateArray()
            .Select(i => new AttentionItem(i.GetProperty("id").GetString() ?? "", i.GetProperty("name").GetString() ?? ""))
            .Where(i => ConversationId.IsMatch(i.Id))
            .ToList();
        _attentionBlink = msg.GetProperty("blink").GetBoolean();
        RefreshAlert();
    }

    private void OnSetImportant(JsonElement msg)
    {
        string id = msg.GetProperty("id").GetString() ?? "";
        if (!ConversationId.IsMatch(id)) return;
        bool on = msg.GetProperty("on").GetBoolean();
        var list = _settings.ImportantConversations;
        list.RemoveAll(x => x == id);
        if (on) list.Add(id);
        _settings.Save();
        Log.Info(on ? $"conversation starred ({list.Count} important)" : $"conversation unstarred ({list.Count} important)");
        _ = PushStyleAsync(); // so a page reload starts with the current list
    }

    internal void SetImportantAlerts(bool on)
    {
        _settings.ImportantAlerts = on;
        _settings.Save();
        RefreshAlert();
    }

    private void RefreshAlert()
    {
        bool show = _settings.ImportantAlerts && _attention.Count > 0;
        AlertPill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            string first = _attention[0].Name;
            AlertText.Text = _attention.Count == 1
                ? $"{first} needs a reply"
                : $"{first} +{_attention.Count - 1} need replies";
            AlertOpenButton.ToolTip = "Waiting on your reply:\n" + string.Join("\n", _attention.Select(a => a.Name));
        }
        SetPulse(show && _attentionBlink);
    }

    private void SetPulse(bool on)
    {
        if (on != _pulsing)
        {
            _pulsing = on;
            if (on)
            {
                var pulse = new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(650))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                AlertOverlay.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                AlertOverlay.BeginAnimation(OpacityProperty, null);
                AlertOverlay.Opacity = 0;
            }
            Log.Info(on ? "important alert on" : "important alert off");
        }
        ApplyAlertSurfaces();
    }

    /// <summary>
    /// Border, taskbar and tray follow the pulse. Runs again whenever the window is minimized,
    /// restored, hidden or shown, since each state has its own place to show the alert.
    /// </summary>
    private void ApplyAlertSurfaces()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_pulsing) Native.SetBorderColor(_hwnd, AlertOrange.R, AlertOrange.G, AlertOrange.B);
        else Native.ResetBorderColor(_hwnd);
        Native.FlashTaskbar(_hwnd, _pulsing && WindowState == WindowState.Minimized);
        _tray.SetAlert(_pulsing && !IsVisible);
    }

    private void AlertOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_attention.Count == 0) return;
        RunPageCommand($"openConversation({JsonSerializer.Serialize(_attention[0].Id)})");
    }

    private void AlertDismiss_Click(object sender, RoutedEventArgs e) => RunPageCommand("dismissAttention()");
}
