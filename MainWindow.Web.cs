using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace TextDock;

public partial class MainWindow
{
    private const string AppOrigin = "https://messages.google.com";
    private const string StartUrl = AppOrigin + "/web/conversations";

    // Hosts that always load inside the app (Messages itself plus Google sign-in).
    private static readonly string[] InAppHosts =
    {
        "messages.google.com", "accounts.google.com", "accounts.youtube.com", "consent.google.com", "gds.google.com",
    };

    private const string CustomCssTemplate = """
        /* TextDock custom styles
         *
         * Saved changes apply immediately. This file loads after the built-in compact
         * styles, so rules here win on equal specificity. The built-in rules only apply
         * while <html> has the "tdk-compact" class (menu > Compact layout).
         * Press F12 in TextDock to inspect the page.
         *
         * The built-in sheet exposes a few knobs. Defaults:
         *
         * html.tdk-compact {
         *   --tdk-list-avatar: 34px;
         *   --tdk-msg-avatar: 28px;
         *   --tdk-row-pad: 7px;
         *   --tdk-header-h: 46px;
         *   --tdk-icon-btn: 34px;
         * }
         */

        """;

    private static readonly string WebDataDir = Path.Combine(Log.Dir, "WebView2");

    private string _baseCss = "";
    private string _injectJs = "";
    private string _userCss = "";
    private string? _bootstrapScriptId;
    private FileSystemWatcher? _cssWatcher;
    private DispatcherTimer? _cssDebounce;
    private CoreWebView2Notification? _lastNotification;
    private int _unread = -1;

    private async Task InitWebAsync()
    {
        try
        {
            _baseCss = ReadEmbedded("TextDock.compact.css");
            _injectJs = ReadEmbedded("TextDock.inject.js");
            _userCss = ReadUserCss();

            // Keep timers and the renderer at full speed while hidden in the tray or covered by
            // other windows; otherwise Chromium throttles the page and messages show up late.
            var args = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
            if (_options.DebugPort is int port) args += $" --remote-debugging-port={port}";

            var env = await CoreWebView2Environment.CreateAsync(null, WebDataDir, new CoreWebView2EnvironmentOptions(args));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;

            var s = core.Settings;
            s.IsStatusBarEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreDevToolsEnabled = true;

            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.NotificationReceived += OnNotificationReceived;
            core.ProcessFailed += OnProcessFailed;
            core.SourceChanged += (_, _) => UpdateHomeButton();
            core.NavigationCompleted += (_, _) =>
            {
                if (Math.Abs(Web.ZoomFactor - _settings.Zoom) > 0.001) Web.ZoomFactor = _settings.Zoom;
            };
            Web.ZoomFactorChanged += OnZoomFactorChanged;

            try
            {
                // Pre-grant so the page never shows its "turn on notifications" prompt.
                await core.Profile.SetPermissionStateAsync(
                    CoreWebView2PermissionKind.Notifications, AppOrigin, CoreWebView2PermissionState.Allow);
            }
            catch (Exception ex)
            {
                Log.Error("notification permission preset failed", ex);
            }

            await RegisterBootstrapAsync();
            Web.ZoomFactor = _settings.Zoom;
            core.Navigate(StartUrl);
            WatchUserCss();
            Log.Info($"webview ready (runtime {env.BrowserVersionString})");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError($"{AppName} needs the Microsoft Edge WebView2 Runtime.\n\n" +
                      $"Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start {AppName} again.");
            ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error("webview init failed", ex);
            ShowError($"The embedded browser failed to start:\n\n{ex.Message}");
        }
    }

    private void ShowError(string text)
    {
        if (IsVisible) MessageBox.Show(this, text, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(text, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---- page <-> host --------------------------------------------------------------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            switch (msg.GetProperty("type").GetString())
            {
                case "theme":
                    var bg = msg.GetProperty("bg");
                    ApplyChromeTheme(Color.FromRgb(Channel(bg[0]), Channel(bg[1]), Channel(bg[2])),
                        msg.GetProperty("dark").GetBoolean());
                    break;
                case "unread":
                    SetUnread(msg.GetProperty("count").GetInt32());
                    break;
                case "attention":
                    OnAttention(msg);
                    break;
                case "setImportant":
                    OnSetImportant(msg);
                    break;
                case "brand":
                    bool hidden = msg.GetProperty("hidden").GetBoolean();
                    PageMenuButton.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
                    TitleContent.Margin = new Thickness(hidden ? 2 : 11, 0, 0, 0);
                    break;
                case "confirmDelete":
                    var request = new DeleteRequest(
                        msg.GetProperty("token").GetString() ?? "",
                        msg.GetProperty("count").GetInt32(),
                        msg.GetProperty("names").EnumerateArray().Select(n => n.GetString() ?? "").ToList());
                    // Not from inside the WebView2 event: a modal loop there can re-enter the browser.
                    Dispatcher.InvokeAsync(() => ConfirmDeleteAsync(request));
                    break;
                case "deleteDone":
                    int deleted = msg.GetProperty("deleted").GetInt32();
                    int failed = msg.GetProperty("failed").GetInt32();
                    string reason = msg.GetProperty("abortReason").GetString() ?? "";
                    Log.Info($"bulk delete finished: deleted={deleted} failed={failed} abort='{reason}'");
                    if (reason.Length > 0)
                    {
                        _tray.ShowInfo("Bulk delete stopped",
                            $"Messages' menu didn't look as expected ({reason}), so nothing else was touched. {deleted} deleted.");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("bad message from page", ex);
        }
    }

    private static byte Channel(JsonElement v) => (byte)Math.Clamp((int)Math.Round(v.GetDouble()), 0, 255);

    private sealed record DeleteRequest(string Token, int Count, List<string> Names);

    /// <summary>
    /// The page asks; this dialog decides. The page only deletes when handed back the one-time
    /// token from its request, so nothing in the page can start a delete on its own.
    /// </summary>
    private async Task ConfirmDeleteAsync(DeleteRequest request)
    {
        var text = new StringBuilder();
        text.AppendLine(request.Count == 1 ? "Delete 1 conversation?" : $"Delete {request.Count} conversations?");
        text.AppendLine();
        foreach (var name in request.Names) text.AppendLine("    " + name);
        if (request.Count > request.Names.Count) text.AppendLine($"    …and {request.Count - request.Names.Count} more");
        text.AppendLine();
        text.Append("This happens on your phone too. With Trash turned on in Messages they can be " +
                    "restored from Trash; otherwise they're gone for good.");

        var answer = MessageBox.Show(this, text.ToString(), "Delete conversations",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        bool go = answer == MessageBoxResult.Yes;
        Log.Info(go ? $"bulk delete confirmed ({request.Count})" : "bulk delete cancelled");
        if (Web.CoreWebView2 is null) return;
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync(go
                ? $"window.__textdock && window.__textdock.runDelete({JsonSerializer.Serialize(request.Token)});"
                : "window.__textdock && window.__textdock.cancelDelete();");
        }
        catch (Exception ex)
        {
            Log.Error("bulk delete hand-off failed", ex);
        }
    }

    internal void StartSelecting() => RunPageCommand("startSelecting()");

    internal void SelectUnknownSenders() => RunPageCommand("selectUnknown()");

    private void PageMenuButton_Click(object sender, RoutedEventArgs e) => RunPageCommand("openMenu()");

    private async void RunPageCommand(string call)
    {
        ShowAndFocus();
        if (Web.CoreWebView2 is null) return;
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync($"window.__textdock && window.__textdock.{call};");
        }
        catch (Exception ex)
        {
            Log.Error($"page command {call} failed", ex);
        }
    }

    private void SetUnread(int count)
    {
        count = Math.Max(0, count);
        if (count == _unread) return;
        _unread = count;
        UnreadText.Text = count > 99 ? "99+" : count.ToString();
        UnreadBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = count > 0 ? $"{AppName} ({count})" : AppName;
        TaskbarInfo.Overlay = count > 0 ? Icons.Badge(count) : null;
        TaskbarInfo.Description = Title;
        _tray.SetUnread(count);
    }

    // ---- navigation -------------------------------------------------------------------

    private static bool IsInAppHost(Uri uri) =>
        Array.Exists(InAppHosts, h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsMessagesHost(Uri uri) =>
        uri.Host.Equals("messages.google.com", StringComparison.OrdinalIgnoreCase);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is "about" or "data" or "blob") return;
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            e.Cancel = true;
            OpenExternal(uri);
            return;
        }
        if (IsInAppHost(uri)) return;

        // Some other https site. A link the user clicked inside Messages goes to the browser.
        // Redirects and sign-in hops (work accounts can bounce through an SSO provider) stay
        // in the app so pairing can finish; the home button gets back from anywhere.
        bool fromMessages = Uri.TryCreate(Web.CoreWebView2?.Source, UriKind.Absolute, out var current)
                            && IsMessagesHost(current);
        if (fromMessages && e.IsUserInitiated && !e.IsRedirected)
        {
            e.Cancel = true;
            OpenExternal(uri);
        }
    }

    private void UpdateHomeButton()
    {
        bool offSite = Uri.TryCreate(Web.CoreWebView2?.Source, UriKind.Absolute, out var uri) && !IsMessagesHost(uri);
        HomeButton.Visibility = offSite ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Navigate(StartUrl);

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri);
        // Google sign-in popups stay in a WebView2-managed window so the auth flow can finish.
        if (uri is not null && uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase)) return;
        e.Handled = true;
        if (uri is not null) OpenExternal(uri);
    }

    private static void OpenExternal(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "mailto" or "tel" or "sms"))
        {
            Log.Info($"blocked external open for scheme '{uri.Scheme}'");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open in browser failed", ex);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return;
        if (e.PermissionKind is CoreWebView2PermissionKind.Notifications or CoreWebView2PermissionKind.ClipboardRead)
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.SavesInProfile = true;
        }
        // Microphone/camera fall through to WebView2's own prompt.
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"webview process failed: {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            Web.CoreWebView2?.Reload();
        }
    }

    // ---- notifications -----------------------------------------------------------------

    private void OnNotificationReceived(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        e.Handled = true; // we show it ourselves; WebView2's default UI isn't used
        var n = e.Notification;
        if (!_settings.Notifications) return;

        // The page closes its notifications after 7s, but the toast lives on in Action Center,
        // so keep the reference for a later click.
        _lastNotification = n;
        _tray.ShowMessage(string.IsNullOrWhiteSpace(n.Title) ? "New message" : n.Title, n.Body);
        n.ReportShown();
        Log.Info("notification shown");
    }

    internal void OpenFromNotification()
    {
        ShowAndFocus();
        try
        {
            // Lets the page run its click handler, which opens the conversation.
            _lastNotification?.ReportClicked();
        }
        catch (Exception ex)
        {
            Log.Error("notification click failed", ex);
        }
        _lastNotification = null;
    }

    internal void SetNotifications(bool on)
    {
        _settings.Notifications = on;
        _settings.Save();
        _ = PushStyleAsync(); // the page's own chime is muted only while toasts are on
    }

    // ---- zoom & styles --------------------------------------------------------------

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        double z = Math.Round(Web.ZoomFactor, 2);
        if (Math.Abs(z - _settings.Zoom) < 0.001) return;
        _settings.Zoom = z;
        _settings.Save();
    }

    internal void SetZoom(double zoom)
    {
        _settings.Zoom = zoom;
        _settings.Save();
        if (Web.CoreWebView2 is not null) Web.ZoomFactor = zoom;
    }

    internal void SetCompact(bool on)
    {
        _settings.CompactStyle = on;
        _settings.Save();
        _ = PushStyleAsync();
    }

    private string StyleConfigJson() => JsonSerializer.Serialize(new
    {
        compact = _settings.CompactStyle,
        css = _baseCss,
        userCss = _userCss,
        muteChime = _settings.Notifications,
        important = _settings.ImportantConversations,
    });

    /// <summary>
    /// inject.js is a function expression; the call with the current style config is appended here.
    /// Re-registered whenever styles change so the next page load starts with the right CSS.
    /// </summary>
    private async Task RegisterBootstrapAsync()
    {
        var core = Web.CoreWebView2;
        if (_bootstrapScriptId is not null) core.RemoveScriptToExecuteOnDocumentCreated(_bootstrapScriptId);
        _bootstrapScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"{_injectJs}\n({StyleConfigJson()});");
    }

    private async Task PushStyleAsync()
    {
        if (Web.CoreWebView2 is null) return;
        try
        {
            await RegisterBootstrapAsync();
            await Web.CoreWebView2.ExecuteScriptAsync(
                $"window.__textdock && window.__textdock.configure({StyleConfigJson()});");
        }
        catch (Exception ex)
        {
            Log.Error("style update failed", ex);
        }
    }

    private string ReadUserCss()
    {
        try
        {
            if (!File.Exists(AppSettings.CustomCssPath)) return "";
            using var fs = new FileStream(AppSettings.CustomCssPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return _userCss; // editor still holds the file; keep what we had
        }
    }

    private void WatchUserCss()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            _cssDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _cssDebounce.Tick += (_, _) =>
            {
                _cssDebounce.Stop();
                _userCss = ReadUserCss();
                _ = PushStyleAsync();
            };
            _cssWatcher = new FileSystemWatcher(AppSettings.Dir, "custom.css")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void Changed(object? s, FileSystemEventArgs a) => Dispatcher.InvokeAsync(() =>
            {
                _cssDebounce.Stop();
                _cssDebounce.Start();
            });
            _cssWatcher.Changed += Changed;
            _cssWatcher.Created += Changed;
            _cssWatcher.Deleted += Changed;
            _cssWatcher.Renamed += Changed;
            _cssWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("custom.css watcher failed", ex);
        }
    }

    internal void EditCustomCss()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            if (!File.Exists(AppSettings.CustomCssPath)) File.WriteAllText(AppSettings.CustomCssPath, CustomCssTemplate);
            try
            {
                Process.Start(new ProcessStartInfo(AppSettings.CustomCssPath) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.CustomCssPath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("opening custom.css failed", ex);
        }
    }

    // ---- misc -------------------------------------------------------------------------

    internal void ReloadPage() => Web.CoreWebView2?.Reload();

    internal void OpenDevTools() => Web.CoreWebView2?.OpenDevToolsWindow();

    private void FocusWeb()
    {
        if (Web.CoreWebView2 is not null) Web.Focus();
    }

    private void DisposeWeb()
    {
        _cssWatcher?.Dispose();
        _cssDebounce?.Stop();
        Web.Dispose();
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
