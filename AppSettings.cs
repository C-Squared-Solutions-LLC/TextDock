using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TextDock;

public sealed class AppSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool AllDesktops { get; set; } = true;
    public bool CompactStyle { get; set; } = true;
    public double Zoom { get; set; } = 0.9;
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+M";
    public bool TrayHintShown { get; set; }

    /// <summary>Blink the window while a starred conversation waits for your reply.</summary>
    public bool ImportantAlerts { get; set; } = true;

    /// <summary>Starred conversation ids (from the conversation URL, no names or content).</summary>
    public List<string> ImportantConversations { get; set; } = new();
    public WindowBounds? Bounds { get; set; }

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TextDock");

    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string CustomCssPath => Path.Combine(Dir, "custom.css");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (loaded is not null)
                {
                    loaded.Zoom = Math.Clamp(loaded.Zoom, 0.5, 2.0);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings load failed, using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings save failed", ex);
        }
    }
}

/// <summary>Normal (restored) window rect in workspace coordinates, as Get/SetWindowPlacement use.</summary>
public sealed class WindowBounds
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }
}

internal sealed record StartupOptions(bool StartHidden, int? DebugPort)
{
    public static StartupOptions Parse(string[] args)
    {
        bool hidden = false;
        int? port = null;
        foreach (var arg in args)
        {
            if (arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase))
                hidden = true;
            else if (arg.StartsWith("--debug-port=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg.AsSpan("--debug-port=".Length), out var p) && p is > 1024 and < 65536)
                port = p;
        }
        return new StartupOptions(hidden, port);
    }
}
