using System;
using System.IO;

namespace PlanViewer.App.Services;

/// <summary>
/// Last-resort exception logging to the app's local data directory.
/// Added for crash reports like issue #415, where the app died with nothing
/// on disk and the reporter had to pull Event Viewer data themselves.
/// Must never throw: it runs inside exception handlers.
/// </summary>
internal static class CrashLogger
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object Sync = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PerformanceStudio", "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > MaxLogBytes)
                    file.Delete();

                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Swallow everything: logging a crash must not cause one.
        }
    }
}
