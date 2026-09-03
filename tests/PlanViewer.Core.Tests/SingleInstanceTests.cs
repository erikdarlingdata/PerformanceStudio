using System.Threading;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #489: two Studio instances each hold a whole-file AppSettings snapshot and Save writes
/// the whole file, so the instance that exits second silently clobbers the other's
/// open_tabs and every other setting. The fix makes a bare second launch hand a
/// "surface yourself" sentinel to the running instance over the existing OpenFile pipe
/// (with-file launches already forwarded), with <c>--new-instance</c> as the deliberate
/// escape hatch.
///
/// <para>The process-level halves — the named mutex in Program.Main and the exit of the
/// non-owning launch — cannot run under this harness, which boots App directly and never
/// enters Main; they are verified by inspection and commented in Program.cs. What CAN be
/// pinned, and is here, are the two seams everything else leans on: the receiver's message
/// grammar (sentinel vs file path vs garbage, including the property that makes the
/// sentinel safe to send to a pre-#489 receiver) and the argv scrubbing that keeps the
/// flag from shadowing a real file argument.</para>
/// </summary>
public class SingleInstanceTests
{
    /* ---- Classify: the receiver's message grammar ---------------------------------- */

    [Fact]
    public void TheActivationSentinelClassifiesAsActivate()
    {
        Assert.Equal(
            SingleInstance.PipeMessage.Activate,
            SingleInstance.Classify(SingleInstance.ActivateSentinel));
    }

    [Fact]
    public void AnExistingFileClassifiesAsOpenFile()
    {
        var path = TempSql("SELECT 1;");
        try
        {
            Assert.Equal(SingleInstance.PipeMessage.OpenFile, SingleInstance.Classify(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A path that stopped existing between send and receive was dropped silently before
    /// #489 and must still be — the receiver has no way to open it and no business
    /// guessing.
    /// </summary>
    [Fact]
    public void AMissingPathClassifiesAsIgnore()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "never_written.sqlplan");
        Assert.Equal(SingleInstance.PipeMessage.Ignore, SingleInstance.Classify(missing));
    }

    [Fact]
    public void BlankLinesClassifyAsIgnore()
    {
        Assert.Equal(SingleInstance.PipeMessage.Ignore, SingleInstance.Classify(null));
        Assert.Equal(SingleInstance.PipeMessage.Ignore, SingleInstance.Classify(string.Empty));
        Assert.Equal(SingleInstance.PipeMessage.Ignore, SingleInstance.Classify("   "));
    }

    /// <summary>
    /// The entire backward-compatibility story of the sentinel, pinned: a pre-#489
    /// receiver's only guard is <c>File.Exists(line)</c>, so the sentinel is safe to send
    /// to an old running build exactly as long as it can never name an existing file.
    ///
    /// <para>Pinned as a STRING property, not with File.Exists — the gate review caught
    /// that a File.Exists assertion is vacuous on the ubuntu CI runner, where ':' is a
    /// legal filename character and the check only proves no such file sits in the test
    /// CWD. What actually guarantees old-Windows-receiver safety is the colon, which
    /// Windows rejects in file names; asserting the characters directly means "tidying"
    /// the token into a path-representable word fails this test on every platform. An old
    /// LINUX receiver next to an adversarially created "::activate::" file remains the
    /// accepted floor (new receivers classify the sentinel before File.Exists, so only
    /// pre-#489 builds are exposed, and only until they restart).</para>
    /// </summary>
    [Fact]
    public void TheSentinelCanNeverBeMistakenForAFileByAnOldWindowsReceiver()
    {
        Assert.Contains(':', SingleInstance.ActivateSentinel);
        Assert.StartsWith("::", SingleInstance.ActivateSentinel, StringComparison.Ordinal);
        Assert.EndsWith("::", SingleInstance.ActivateSentinel, StringComparison.Ordinal);

        /* Belt and braces for the platform where the guarantee lives: on Windows the
           token must actually be rejected by the file-name rules, not just assumed to be. */
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(
                SingleInstance.ActivateSentinel,
                c => Path.GetInvalidFileNameChars().Contains(c));
        }
    }

    /* ---- StripNewInstanceFlag: argv scrubbing -------------------------------------- */

    /// <summary>
    /// "PerformanceStudio.exe --new-instance file.sqlplan" must still open the file: the
    /// flag disappears and the path keeps its place as the first real argument, because
    /// OpenFromStartupArgs only ever looks at args[1].
    /// </summary>
    [Fact]
    public void TheNewInstanceFlagIsRemovedAndTheFileArgSurvives()
    {
        var raw = new[] { "PerformanceStudio.exe", "--new-instance", @"C:\plans\slow.sqlplan" };

        var scrubbed = SingleInstance.StripNewInstanceFlag(raw);

        Assert.Equal(new[] { "PerformanceStudio.exe", @"C:\plans\slow.sqlplan" }, scrubbed);
        Assert.True(SingleInstance.NewInstanceRequested(raw));
    }

    [Fact]
    public void TheFlagIsRecognizedInAnyCase()
    {
        var raw = new[] { "PerformanceStudio.exe", "--NEW-INSTANCE" };

        Assert.True(SingleInstance.NewInstanceRequested(raw));
        Assert.Equal(new[] { "PerformanceStudio.exe" }, SingleInstance.StripNewInstanceFlag(raw));
    }

    /// <summary>
    /// The overwhelmingly common argv has no flag in it, and scrubbing must be invisible
    /// there — same contents, same order, nothing else filtered.
    /// </summary>
    [Fact]
    public void ArgvWithoutTheFlagPassesThroughUntouched()
    {
        var raw = new[] { "PerformanceStudio.exe", @"C:\plans\slow.sqlplan" };

        Assert.Equal(raw, SingleInstance.StripNewInstanceFlag(raw));
        Assert.False(SingleInstance.NewInstanceRequested(raw));
    }

    /* ---- The window-side dispatch -------------------------------------------------- */

    /// <summary>
    /// What a bare second launch's sentinel actually buys the user: the running window
    /// comes back from minimized. Activate() is also called but has nothing observable
    /// headlessly; the state restore is the part that can silently regress.
    /// </summary>
    [Fact]
    public void ASentinelDispatchRestoresAMinimizedWindow()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.WindowState = WindowState.Minimized;

            window.DispatchPipeMessage(
                SingleInstance.PipeMessage.Activate, SingleInstance.ActivateSentinel);

            Assert.Equal(WindowState.Normal, window.WindowState);
        });
    }

    /// <summary>
    /// The path every existing sender relies on — the SSMS extension and a second launch
    /// handing over its file — still lands the file in a tab, and now also surfaces a
    /// minimized window instead of loading into one that stays in the taskbar.
    /// </summary>
    [Fact]
    public void AFileDispatchOpensTheFileAndRestoresAMinimizedWindow()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS from_pipe;");
            try
            {
                var window = new MainWindow();
                window.WindowState = WindowState.Minimized;
                var queryTabsBefore = QueryTabs(window).Count();

                window.DispatchPipeMessage(SingleInstance.PipeMessage.OpenFile, path);

                Assert.Equal(queryTabsBefore + 1, QueryTabs(window).Count());
                Assert.Equal(path, ((QuerySessionControl)QueryTabs(window).Last().Content!).SourceFilePath);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The end-to-end argv property, through the real startup router: the constructor
    /// consumes raw <c>Environment.GetCommandLineArgs()</c>, so OpenFromStartupArgs must
    /// scrub the flag itself — Program.Main's scrubbed copy never reaches it. With the
    /// flag sitting where the file used to be, the file behind it still opens.
    /// </summary>
    [Fact]
    public void OpenFromStartupArgsOpensTheFileBehindTheNewInstanceFlag()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS behind_the_flag;");
            try
            {
                var window = new MainWindow();
                var queryTabsBefore = QueryTabs(window).Count();

                window.OpenFromStartupArgs(new[] { "PerformanceStudio.exe", "--new-instance", path });

                Assert.Equal(queryTabsBefore + 1, QueryTabs(window).Count());
                Assert.Equal(path, ((QuerySessionControl)QueryTabs(window).Last().Content!).SourceFilePath);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The named-mutex machinery itself, exercised on whatever platform the suite runs on.
    ///
    /// <para>The gate review's point: Program.Main's catch-all degrades a throwing mutex to
    /// "run fully" — the right call, but if named mutexes ever break wholesale on a platform
    /// (the Unix shim backs them with files under /tmp, and PlatformNotSupportedException is
    /// the classic wholesale failure), single-instancing would silently no-op there and the
    /// #489 clobber would be back with no symptom. This runs on the ubuntu CI runner on every
    /// push, so a platform where creating or re-opening a named mutex throws fails HERE,
    /// loudly, instead of degrading invisibly in the field. In-process rather than
    /// cross-process (the harness cannot spawn app instances), which still traverses the
    /// named create and second-open paths the shim has to serve; macOS has no CI leg, so the
    /// one-time bare-double-launch smoke there is a release-checklist item, not a test.</para>
    /// </summary>
    [Fact]
    public void NamedMutexMachineryWorksOnThisPlatform()
    {
        // A unique name per run: colliding with a real Studio instance on a dev machine
        // (or a parallel test run) would turn this into a flake about unrelated state.
        var name = $"{SingleInstance.MutexName}_selftest_{Guid.NewGuid():N}";

        using var first = new Mutex(initiallyOwned: true, name, out var createdFirst);
        Assert.True(createdFirst, "a fresh name must be created, not found");

        using var second = new Mutex(initiallyOwned: true, name, out var createdSecond);
        Assert.False(createdSecond, "a second open of the same name must see the existing mutex");
    }

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static System.Collections.Generic.IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);
}
