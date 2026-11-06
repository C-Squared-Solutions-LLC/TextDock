using System;
using System.IO;

namespace TextDock;

/// <summary>Tiny append-only log. Never write message content here, only app events.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextDock");

    public static string FilePath => Path.Combine(Dir, "textdock.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
        }
        catch
        {
            // Logging is best effort.
        }
        Info($"start pid={Environment.ProcessId} os={Environment.OSVersion.Version}");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging is best effort.
            }
        }
    }
}
