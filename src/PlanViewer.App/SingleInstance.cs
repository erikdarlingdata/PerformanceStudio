using System;
using System.IO;
using System.Linq;

namespace PlanViewer.App;

/// <summary>
/// The names and message grammar shared by both halves of single-instance startup (#489):
/// the launcher side in <see cref="Program"/> that decides whether to run or to hand its
/// work to an already-running instance, and the receiver side in <see cref="MainWindow"/>'s
/// pipe server that acts on what arrives.
///
/// <para><b>Why single-instance at all.</b> Every instance holds a whole-file
/// <c>AppSettings</c> snapshot and Save writes the whole file, so two instances make the
/// settings file last-write-wins: whichever exits second silently clobbers the other's
/// open_tabs and every other setting. AtomicFile prevents torn files, not lost updates.
/// A launch with a file argument already forwarded to the running instance over the named
/// pipe; a bare launch ran a full second instance and was exactly the clobber case.</para>
///
/// <para><b>Why the grammar is this shape.</b> The pipe protocol is one line per
/// connection, historically always a file path, and two other sender/receiver pairs speak
/// it: the SSMS extension's AppLauncher (sends plain paths, cannot be updated in lockstep
/// with the app) and any older Studio build still running across an upgrade. So the
/// activation message is not a version field or a framed header — it is a single reserved
/// line that an OLD receiver safely ignores: the pre-#489 handler's only guard is
/// <c>File.Exists(line)</c>, and <see cref="ActivateSentinel"/> contains characters that
/// are illegal in Windows file names, so it can never name an existing file there. An old
/// receiver reads it, finds no such file, drops it, and keeps serving — the worst-case
/// skew is a bare second launch that exits without surfacing anything, not a crash or a
/// junk tab.</para>
/// </summary>
internal static class SingleInstance
{
    /// <summary>
    /// The pipe every Studio sender and receiver has always shared — also written by the
    /// SSMS extension's AppLauncher, which is why the name can never change casually.
    /// </summary>
    internal const string PipeName = "SQLPerformanceStudio_OpenFile";

    /// <summary>
    /// Unprefixed, so it lands in the default per-user-session <c>Local\</c> namespace on
    /// Windows — two users (or two RDP sessions) each get their own instance, which is the
    /// scope the settings file conflict actually has. Distinct from Lite's
    /// <c>PerformanceMonitorLite_SingleInstance</c>; the two apps must never see each other.
    /// </summary>
    internal const string MutexName = "SQLPerformanceStudio_SingleInstance";

    /// <summary>
    /// Escape hatch (#489): skip the single-instance check and run a full second instance.
    /// A user who runs two on purpose accepts settings last-write-wins as their informed
    /// choice. Stripped from argv before any file-open logic sees it.
    /// </summary>
    internal const string NewInstanceFlag = "--new-instance";

    /// <summary>
    /// The line a bare second launch sends to mean "surface your main window". The
    /// double-colons make it unrepresentable as a Windows file name on purpose — see the
    /// class comment for why that property is the entire backward-compatibility story.
    /// </summary>
    internal const string ActivateSentinel = "::activate::";

    /// <summary>What one received pipe line means. See <see cref="Classify"/>.</summary>
    internal enum PipeMessage
    {
        /// <summary>Blank, or a path that doesn't exist — dropped silently, exactly as the pre-#489 receiver did.</summary>
        Ignore,

        /// <summary>The <see cref="ActivateSentinel"/> — a bare second launch asking this window to surface.</summary>
        Activate,

        /// <summary>An existing file — the SSMS extension or a second launch handing over a path to open.</summary>
        OpenFile,
    }

    /// <summary>
    /// The receiver's dispatch decision for one pipe line, extracted from the pipe loop so
    /// it can be pinned by tests without a pipe.
    ///
    /// <para>The sentinel is checked before <c>File.Exists</c>, not after: on Windows the
    /// order can't matter (the sentinel is an illegal file name), but on Linux a file named
    /// <c>::activate::</c> is representable, and the reserved meaning must win over any
    /// such file. <c>File.Exists</c> on a garbage line returns false rather than throwing,
    /// which is what made the old receiver's guard safe and keeps this one safe too.</para>
    /// </summary>
    internal static PipeMessage Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return PipeMessage.Ignore;

        if (string.Equals(line, ActivateSentinel, StringComparison.Ordinal))
            return PipeMessage.Activate;

        if (File.Exists(line))
            return PipeMessage.OpenFile;

        return PipeMessage.Ignore;
    }

    /// <summary>True when argv carries <see cref="NewInstanceFlag"/> anywhere.</summary>
    internal static bool NewInstanceRequested(string[] args) =>
        args.Any(IsNewInstanceFlag);

    /// <summary>
    /// Argv minus every <see cref="NewInstanceFlag"/>, order otherwise preserved. Applied
    /// in BOTH places argv is consumed: <see cref="Program.Main"/> (so the forwarded path
    /// and the args handed to Avalonia are clean) and
    /// <see cref="MainWindow.OpenFromStartupArgs"/> (which reads the raw
    /// <c>Environment.GetCommandLineArgs()</c> itself, so Program's scrubbed copy never
    /// reaches it). Without the second scrub, "PerformanceStudio.exe --new-instance
    /// file.sqlplan" would see the flag at args[1] instead of the file. The flag happens to
    /// fail that path's <c>File.Exists</c> guard today, but the file arg behind it would
    /// still be skipped — being explicit here is what makes the flag invisible rather than
    /// merely unlucky.
    /// </summary>
    internal static string[] StripNewInstanceFlag(string[] args) =>
        args.Where(a => !IsNewInstanceFlag(a)).ToArray();

    /// <summary>
    /// Case-insensitive, because Windows users type flags in whatever case survived their
    /// muscle memory and there is no second flag for this one to collide with.
    /// </summary>
    private static bool IsNewInstanceFlag(string arg) =>
        string.Equals(arg, NewInstanceFlag, StringComparison.OrdinalIgnoreCase);
}
