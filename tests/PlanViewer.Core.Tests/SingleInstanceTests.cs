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
    /// to an old running build exactly as long as it can never name an existing file. The
    /// double-colons are illegal in Windows file names by design; if someone ever
    /// "tidies" the token into something path-representable, this fails and points here.
    /// </summary>
    [Fact]
    public void TheSentinelCanNeverBeMistakenForAFileByAnOldReceiver()
    {
        Assert.False(
            File.Exists(SingleInstance.ActivateSentinel),
            "an old receiver File.Exists-guards every pipe line, so the sentinel must never resolve to a real file");
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

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static System.Collections.Generic.IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);
}
