using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using PlanViewer.App.Services;
using Velopack;

namespace PlanViewer.App;

class Program
{
    private const string PipeName = "SQLPerformanceStudio_OpenFile";

    [STAThread]
    public static void Main(string[] args)
    {
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            // Clean up the .sqlplan association on uninstall. Velopack's uninstall
            // hooks are Windows-only, which lines up — the association cleanup that
            // needs them is Windows too (Linux ships as a plain zip, no uninstaller).
            velopack = velopack.OnBeforeUninstallFastCallback((_) => FileAssociationService.Unregister());
        }
        velopack.Run();

        // If another instance is running, send the file path to it and exit
        if (args.Length > 0 && TrySendToRunningInstance(args[0]))
            return;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Tries to hand the file path to an already-running instance over its named pipe.
    /// A failed/timed-out connect means no instance is listening, so the caller should
    /// launch normally. Returns true only if the path was actually delivered.
    /// </summary>
    /// <remarks>
    /// Detection is via the pipe itself rather than a named mutex: the previous mutex
    /// was disposed as soon as this method returned, so no instance ever held it and
    /// the forwarding path was never taken.
    /// </remarks>
    private static bool TrySendToRunningInstance(string filePath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // Short timeout: a running instance's listener is idle and connects
            // immediately; when none is running this is the only added launch delay.
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(filePath);
            writer.Flush();
            return true;
        }
        catch
        {
            // No instance listening (or pipe busy) — fall through to launch normally.
            return false;
        }
    }
}
