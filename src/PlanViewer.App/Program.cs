using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PlanViewer.App.Services;
using Velopack;

namespace PlanViewer.App;

class Program
{
    /// <summary>
    /// Held — never released — by the instance that owns the single-instance slot (#489).
    /// The OS tears the mutex down when the process exits, crash included, so there is no
    /// release path to get wrong; a static field keeps the handle rooted for the whole run
    /// (the previous mutex attempt died precisely because its handle was disposed the
    /// moment the acquiring method returned, so no instance ever actually held it).
    /// </summary>
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort diagnostics: issue #415 was a crash-to-desktop that left
        // nothing on disk. These can't stop a dispatcher-thread crash, but they
        // leave a stack trace in the crash log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLogger.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            // Clean up the .sqlplan association on uninstall. Velopack's uninstall
            // hooks are Windows-only, which lines up — the association cleanup that
            // needs them is Windows too (Linux ships as a plain zip, no uninstaller).
            velopack = velopack.OnBeforeUninstallFastCallback((_) => FileAssociationService.Unregister());
        }
        velopack.Run();

        /* #489: every instance holds a whole-file AppSettings snapshot and Save writes the
           whole file, so a second instance makes the settings file last-write-wins — the
           instance that exits second silently clobbers the other's open_tabs and every
           other setting (AtomicFile prevents torn writes, not lost updates). File-argument
           launches already forwarded to the running instance over the pipe; a BARE second
           launch ran a full instance and was exactly the clobber case. So unless the user
           explicitly asks for a second instance, a launch that finds one running hands it
           its work — a file path, or a bare "surface yourself" — and exits. */
        var newInstanceRequested = SingleInstance.NewInstanceRequested(args);

        // The flag is a launcher directive, not a file: strip it so nothing downstream can
        // mistake it for a path ("PerformanceStudio.exe --new-instance file.sqlplan" must
        // still open the file). MainWindow re-reads the raw argv and scrubs it again itself.
        var effectiveArgs = SingleInstance.StripNewInstanceFlag(args);

        if (!newInstanceRequested)
        {
            /* The pre-#489 forwarding, kept first and unchanged: a with-file launch tries
               the pipe before anything else. Beyond being the common case, probing before
               the mutex makes the WITH-FILE path version-skew-proof — an already-running
               build that predates the mutex answers its pipe but holds no mutex, and a
               mutex-first flow would run a second full window beside it instead of handing
               the file over.

               A BARE launch during that same skew is the one #489 case deliberately left
               open: it cannot probe first, because an old receiver silently drops the
               sentinel (File.Exists guard) while delivery still reports success — the
               launch would exit having surfaced nothing, which is worse than a second
               instance. So a bare launch beside a pre-mutex build claims the free mutex
               and runs fully: the pre-#489 status quo, for one transient upgrade window
               that ends when the old instance exits. Documented rather than solved; a
               real fix needs an acknowledged (duplex) surfacing protocol. */
            if (effectiveArgs.Length > 0 && TrySendToRunningInstance(effectiveArgs[0], maxAttempts: 1))
                return;

            if (!TryBecomeSingleInstanceOwner())
            {
                /* Another instance owns the slot but hasn't answered its pipe yet — bare
                   launches never probed above, and a with-file probe may have raced the
                   owner's boot (the pipe server starts in the MainWindow constructor,
                   which on a cold start is seconds after its Main). Retry for ~2s before
                   giving up on delivery. */
                var message = effectiveArgs.Length > 0
                    ? effectiveArgs[0]
                    : SingleInstance.ActivateSentinel;
                if (TrySendToRunningInstance(message, maxAttempts: 4))
                    return;

                /* Delivery failed after retries: the owner is wedged, exiting, or still
                   booting slowly. Losing the user's action — their double-clicked file, or
                   the app simply appearing at all — is worse than a rare second instance,
                   so fall through and run fully. This is also the honest residue of the
                   startup race: two simultaneous bare launches can BOTH end up proceeding
                   when the loser's retries run out before the winner's pipe exists. The
                   mutex closes most of that window; what remains falls back to the
                   pre-#489 last-write-wins behavior, which is the accepted floor. */
            }
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(effectiveArgs);

        // Reached only at app shutdown. Statics are GC roots, so the field alone keeps the
        // owner's mutex handle alive; this read exists to say out loud that the handle's
        // LIFETIME is the point (and to keep the field from reading as write-only).
        GC.KeepAlive(_singleInstanceMutex);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Tries to claim the single-instance slot (#489). True means this process is the
    /// owner and should run; false means another instance holds the slot and this launch
    /// should hand its work over instead.
    /// </summary>
    private static bool TryBecomeSingleInstanceOwner()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out var createdNew);
            if (createdNew)
            {
                _singleInstanceMutex = mutex;
                return true;
            }

            /* Another process owns it. Close our handle right away: if this launch ends up
               running anyway (the pipe fallback above), a lingering handle would keep the
               kernel object alive after the real owner exits, and a THIRD launch would then
               see the name taken with nobody serving the pipe behind it. */
            mutex.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            /* Mutex machinery unavailable — an ACL mismatch on the name, a restrictive
               sandbox, or a platform where the named-mutex shim misbehaves (on Unix these
               are file-backed under /tmp; PlatformNotSupportedException would land here
               too). Claiming ownership is the conservative answer: this instance runs
               fully, which is exactly the pre-#489 behavior for every launch.

               Said out loud rather than swallowed, because the degradation is otherwise
               invisible: if this fires on every launch, single-instancing has quietly
               no-oped and #489's settings clobber is back with no symptom pointing here.
               stderr is the right channel — a Windows GUI launch has no console and loses
               it harmlessly, while the Unix platforms this is most likely to fire on are
               exactly where launching from a terminal is common. A unit test exercises
               the named-mutex machinery per platform in CI so a shim that throws fails
               loudly there first. */
            Console.Error.WriteLine(
                $"PerformanceStudio: single-instance detection unavailable ({ex.GetType().Name}); running as a full instance.");
            return true;
        }
    }

    /// <summary>
    /// Tries to hand one line — a file path, or the activation sentinel — to an
    /// already-running instance over its named pipe. Returns true only if the line was
    /// actually delivered; the caller decides what a failed delivery costs.
    /// </summary>
    private static bool TrySendToRunningInstance(string message, int maxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                // Between attempts only: the owner is presumably mid-boot, so give its
                // pipe server a beat to come up rather than burning connects back-to-back.
                Thread.Sleep(100);
            }

            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstance.PipeName, PipeDirection.Out);
                // 500ms per attempt: a running instance's listener is idle and connects
                // immediately, while Connect burns the full timeout when nothing is
                // listening — so the single-attempt probe on a with-file launch adds at
                // most the same half second it always has, and the 4-attempt retry path
                // totals roughly the ~2s boot grace it exists for.
                client.Connect(500);
                using var writer = new StreamWriter(client);
                writer.WriteLine(message);
                writer.Flush();
                return true;
            }
            catch
            {
                // Not listening yet, or the single server slot was mid-conversation with
                // another client — retry if the budget allows, otherwise report undelivered.
            }
        }

        return false;
    }
}
