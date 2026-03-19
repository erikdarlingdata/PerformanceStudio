using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Velopack;

namespace PlanViewer.App;

class Program
{
    private const string PipeName = "SQLPerformanceStudio_OpenFile";
    private const string MutexName = "SQLPerformanceStudio_SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

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
    /// Tries to connect to an already-running instance and send the file path.
    /// Returns true if the message was delivered (caller should exit).
    /// </summary>
    private static bool TrySendToRunningInstance(string filePath)
    {
        bool createdNew;
        using var mutex = new Mutex(true, MutexName, out createdNew);

        if (createdNew)
        {
            // We're the first instance — release and let normal startup proceed
            mutex.ReleaseMutex();
            return false;
        }

        // Another instance owns the mutex — try sending the file path
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3 second timeout
            using var writer = new StreamWriter(client);
            writer.WriteLine(filePath);
            writer.Flush();
            return true;
        }
        catch
        {
            // Pipe not available — fall through to launch normally
            return false;
        }
    }
}
