using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    internal void NewQuery_Click(object? sender, RoutedEventArgs e)
    {
        _queryCounter++;
        var label = $"Query {_queryCounter}";

        var session = new QuerySessionControl(_credentialService, _connectionStore);
        var tab = CreateTab(label, session);

        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL Server Execution Plans")
                {
                    Patterns = new[] { "*.sqlplan" }
                },
                new FilePickerFileType("SQL Scripts")
                {
                    Patterns = new[] { "*.sql" }
                },
                new FilePickerFileType("XML Files")
                {
                    Patterns = new[] { "*.xml" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                OpenFileByExtension(path);
        }
    }

    private async void OpenQuery_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Query",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL Scripts")
                {
                    Patterns = new[] { "*.sql" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                LoadSqlFile(path);
        }
    }

    private async void SaveQuery_Click(object? sender, RoutedEventArgs e)
    {
        await SaveQueryAsync();
    }

    /// <summary>
    /// Writes the active query tab's text to disk. Always prompts, but defaults to the
    /// file the query came from so saving back over it is the path of least resistance.
    /// Silently does nothing when the active tab is a plan rather than a query - the menu
    /// item is reachable from anywhere and there is nothing to save.
    /// </summary>
    private async Task SaveQueryAsync()
    {
        if (MainTabControl.SelectedItem is not TabItem { Content: QuerySessionControl session } tab)
            return;

        await SaveQueryAsync(tab, session);
    }

    /// <summary>
    /// Saves a named tab rather than whichever one is selected. The unsaved-changes prompt
    /// (#462) walks every tab, so it needs to save one that is not the active one, and a
    /// scratch tab answering Save arrives here to get a path.
    ///
    /// <para>#473 brings two wrinkles, both from sessions that are no longer tabs. A detached
    /// session has no <paramref name="tab"/> to retitle, hence the null. And the picker has to
    /// come off the window doing the asking: <see cref="Window.StorageProvider"/> here is the
    /// main window's, so a detached window closing with unsaved work would put its Save As
    /// dialog on a window behind the one the user is looking at — and, at shutdown, on one
    /// that is closing. <paramref name="storage"/> is how the caller says which window.</para>
    /// </summary>
    /// <returns>Whether the query reached disk. False also covers the user closing the picker.</returns>
    private async Task<bool> SaveQueryAsync(TabItem? tab, QuerySessionControl session, IStorageProvider? storage = null)
    {
        var picker = storage ?? StorageProvider;
        var existing = session.SourceFilePath;

        var options = new FilePickerSaveOptions
        {
            Title = "Save Query",
            SuggestedFileName = existing != null ? Path.GetFileName(existing) : "Query.sql",
            DefaultExtension = "sql",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("SQL Scripts")
                {
                    Patterns = new[] { "*.sql" }
                },
                FilePickerFileTypes.All
            }
        };

        if (existing != null)
        {
            var directory = Path.GetDirectoryName(existing);
            if (!string.IsNullOrEmpty(directory))
                options.SuggestedStartLocation = await picker.TryGetFolderFromPathAsync(directory);
        }

        var file = await picker.SaveFilePickerAsync(options);

        var path = file?.TryGetLocalPath();
        return path != null && SaveQueryToPath(tab, session, path);
    }

    /// <summary>
    /// The half of saving that does not need a human: write the text, remember where it went,
    /// and retitle the tab to match. Split out from the picker so it can be tested.
    ///
    /// <para><paramref name="tab"/> is null for a session that has been detached into its own
    /// window (#473). There is no tab to retitle; everything else about the save is the same,
    /// including which side of the write settles the dirty state.</para>
    /// </summary>
    internal bool SaveQueryToPath(TabItem? tab, QuerySessionControl session, string path)
    {
        try
        {
            /* Atomic on purpose: on the save-in-place path this file is the user's only copy
               of their query, and a plain truncate-then-write destroys it when the write dies
               halfway — disk full, crash, yanked share. AtomicFile stages a sibling .tmp and
               renames it over the top, so a failed save leaves the original bytes on disk and
               lands in the catch below with the session still dirty. The rename gives the file
               the temp's attributes and inherited ACLs rather than preserving the original's —
               the trade every editor that saves this way makes. */
            /* In the encoding the file was opened with, when it declared one: a .sql from SSMS
               is UTF-16 with a BOM, and writing it back as the default UTF-8 was a silent
               transcode of the user's only copy — reading honored the BOM, saving discarded
               it. A scratch session (and a BOM-less file) has null here and keeps the default
               UTF-8-without-BOM this always wrote. */
            AtomicFile.WriteAllText(path, session.QueryEditor.Text, session.SourceFileEncoding);
            session.SourceFilePath = path;
            /* #490: the one way a tab's place in the session-restore list changes while tab
               membership stays constant — a scratch gaining its first file, or Save As moving
               an existing one. The tab watcher sees neither (no tab was added, removed, or
               swapped), so the persist trigger lives at the assignment. Detached sessions save
               through here too (tab == null); their register entry serves up the new path the
               same way. */
            RequestSessionPersist();
            // Only a write that actually happened settles the dirty state; the catch below
            // deliberately leaves the session modified so the work is still guarded.
            session.MarkClean();

            if (tab != null)
                SetTabLabel(tab, Path.GetFileName(path));

            return true;
        }
        catch (Exception ex)
        {
            ShowFileError("Error Saving File", $"Failed to save: {Path.GetFileName(path)}", ex.Message);
            return false;
        }
    }

    private async void PasteXml_Click(object? sender, RoutedEventArgs e)
    {
        await PasteXmlAsync();
    }

    private static bool IsSupportedFile(string? path)
    {
        return path != null && _supportedExtensions.Any(ext =>
            path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null && files.Any(f => IsSupportedFile(f.TryGetLocalPath())))
                e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (IsSupportedFile(path))
                OpenFileByExtension(path!);
        }
    }

    /// <summary>
    /// Opens one or more files by path. Used by the macOS activation handler when a
    /// plan is double-clicked in Finder (the path arrives via an event, not argv).
    /// Marshals to the UI thread and skips paths that no longer exist.
    /// </summary>
    public void OpenFiles(IEnumerable<string> paths)
    {
        void OpenAll()
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    OpenFileByExtension(path);
            }
            Activate();
        }

        if (Dispatcher.UIThread.CheckAccess())
            OpenAll();
        else
            Dispatcher.UIThread.Post(OpenAll);
    }

    private void OpenFileByExtension(string filePath)
    {
        if (filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            LoadSqlFile(filePath);
        else
            LoadPlanFile(filePath);
    }

    internal void LoadSqlFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var fileName = Path.GetFileName(filePath);

            _queryCounter++;
            var session = new QuerySessionControl(_credentialService, _connectionStore);
            session.QueryEditor.Text = text;
            session.SourceFilePath = filePath;
            // What the file declared is what a save must write back — see SourceFileEncoding.
            session.SourceFileEncoding = DetectBomEncoding(filePath);
            // What was just loaded is what is on disk — the baseline every later edit is measured against.
            session.MarkClean();

            var tab = CreateTab(fileName, session);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();
        }
        catch (Exception ex)
        {
            ShowFileError("Error Opening File", $"Failed to open: {Path.GetFileName(filePath)}", ex.Message);
        }
    }

    /// <summary>
    /// The encoding a file's byte order mark declares, or null when it has none.
    ///
    /// <para>Deliberately a sniff of the mark rather than StreamReader.CurrentEncoding after a
    /// read: CurrentEncoding only moves off its default for UTF-16/32 marks, so it cannot tell
    /// a UTF-8-with-BOM file from a plain one — and the default instance it reports EMITS a
    /// mark on write, so handing it to the save would stamp BOMs onto files that never had
    /// one, a silent change in the opposite direction from the one being fixed. The mark is
    /// the fact being preserved, so the mark is what gets read.</para>
    /// </summary>
    private static Encoding? DetectBomEncoding(string filePath)
    {
        Span<byte> mark = stackalloc byte[4];
        int read;
        using (var stream = File.OpenRead(filePath))
            read = stream.Read(mark);

        // UTF-32 LE opens with UTF-16 LE's mark plus two zero bytes, so it is checked first.
        if (read >= 4 && mark[0] == 0xFF && mark[1] == 0xFE && mark[2] == 0x00 && mark[3] == 0x00)
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        if (read >= 4 && mark[0] == 0x00 && mark[1] == 0x00 && mark[2] == 0xFE && mark[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (read >= 3 && mark[0] == 0xEF && mark[1] == 0xBB && mark[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (read >= 2 && mark[0] == 0xFF && mark[1] == 0xFE)
            return Encoding.Unicode;
        if (read >= 2 && mark[0] == 0xFE && mark[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return null;
    }

    /// <summary>
    /// One modal for every file operation that can fail, so opening and saving report
    /// trouble the same way.
    ///
    /// <para>Shown ownerless until this window is visible. Both the command-line open and the
    /// restore of the previous session's tabs run from the constructor, so a file that is
    /// missing or unreadable at startup reaches here before there is anything to be modal
    /// over, and ShowDialog against a window that has not been shown throws rather than
    /// reporting the problem it was called about.</para>
    /// </summary>
    private void ShowFileError(string title, string headline, string detail)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 450,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = headline,
                        FontWeight = FontWeight.Bold,
                        Margin = new Avalonia.Thickness(0, 0, 0, 10)
                    },
                    new TextBlock
                    {
                        Text = detail,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        if (IsVisible)
            dialog.ShowDialog(this);
        else
            dialog.Show();
    }

    internal void LoadPlanFile(string filePath)
    {
        try
        {
            var xml = File.ReadAllText(filePath);

            // SSMS saves plans as UTF-16 with encoding="utf-16" in the XML declaration.
            // File.ReadAllText auto-detects the BOM, but the resulting C# string still
            // contains encoding="utf-16" which causes XDocument.Parse to fail.
            xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var fileName = Path.GetFileName(filePath);

            if (!ValidatePlanXml(xml, fileName))
                return;

            var viewer = new PlanViewerControl();
            viewer.SetConnectionServices(_credentialService, _connectionStore);
            viewer.LoadPlan(xml, fileName);
            viewer.SourceFilePath = filePath;

            // Wrap viewer with advice toolbar
            var content = CreatePlanTabContent(viewer);

            var tab = CreateTab(fileName, content);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();

            // Track in recent plans list and persist
            TrackRecentPlan(filePath);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to open {Path.GetFileName(filePath)}:\n\n{ex.Message}");
        }
    }

    private async Task PasteXmlAsync()
    {
        var xml = await ClipboardHelper.TryGetTextAsync(this);
        if (string.IsNullOrWhiteSpace(xml))
        {
            ShowError("Could not read any text from the clipboard.");
            return;
        }

        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        if (!ValidatePlanXml(xml, "Pasted Plan"))
            return;

        var viewer = new PlanViewerControl();
        viewer.SetConnectionServices(_credentialService, _connectionStore);
        viewer.LoadPlan(xml, "Pasted Plan");

        var content = CreatePlanTabContent(viewer);
        var tab = CreateTab("Pasted Plan", content);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private bool ValidatePlanXml(string xml, string label)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            if (doc.Root?.Name.LocalName != "ShowPlanXML" &&
                doc.Descendants(ns + "ShowPlanXML").FirstOrDefault() == null)
            {
                ShowError($"{label}: XML is valid but does not appear to be a SQL Server execution plan.\n\nExpected root element: ShowPlanXML");
                return false;
            }
            return true;
        }
        catch (System.Xml.XmlException ex)
        {
            ShowError($"{label}: The XML is not valid.\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The file behind every open tab that has one, in tab order, then the file behind every
    /// detached window that has one, in detach order. Plans and queries both, since
    /// <see cref="GetContentFilePath"/> answers for either shape.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="SaveOpenPlans"/> so a test can assert what would be
    /// persisted without writing over the user's real settings file.</para>
    ///
    /// <para>#490's detached half: this used to walk MainTabControl alone, so a file-backed
    /// plan or query detached into its own window at exit was never written down and never
    /// came back. Detached entries are appended AFTER the docked tabs — docked order is the
    /// order the user arranged and keeps it; detach order is best-effort. On the next start
    /// they all come back as ordinary docked tabs, not re-detached windows: remembering THAT
    /// a file was open is the data-loss fix, remembering window geometry is a different
    /// feature, deliberately not built here.</para>
    /// </remarks>
    internal List<string> CollectOpenTabPaths()
    {
        var paths = new List<string>();

        foreach (var item in MainTabControl.Items)
        {
            if (item is not TabItem tab) continue;

            var path = GetTabFilePath(tab);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }

        foreach (var content in _detachedTabContents)
        {
            var path = GetContentFilePath(content);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// Saves the file paths of all currently open file-based tabs, plans and queries alike,
    /// docked and detached alike (#490).
    /// </summary>
    private void SaveOpenPlans()
    {
        _appSettings.OpenTabs.Clear();
        _appSettings.OpenTabs.AddRange(CollectOpenTabPaths());

        AppSettingsService.Save(_appSettings);
    }

    // ── Continuous session persistence (#490) ─────────────────────────────

    /* #468 wrote the list once, at clean close, and #490 is what that cost: any abnormal exit
       — a crash, a task kill, an OS "shut down anyway" past the dirty-tab prompt — restored
       zero tabs, because the only writer never ran. Membership changes now write the list as
       they happen, debounced so a burst (session restore, Close All) lands as one write. The
       triggers are wired centrally, not per call site — see the TabContentWatcher hookup in
       the constructor for why. */

    /// <summary>How long the writer waits for a burst of membership changes to settle.</summary>
    private static readonly TimeSpan SessionPersistDebounce = TimeSpan.FromSeconds(1);

    /// <summary>Trailing-edge debounce for <see cref="SaveOpenPlans"/>; every request restarts it.</summary>
    private DispatcherTimer? _sessionPersistTimer;

    /// <summary>
    /// Whether a membership change is waiting to be written. Under the test host this flag is
    /// the whole mechanism — see <see cref="RequestSessionPersist"/>.
    /// </summary>
    private bool _sessionPersistPending;

    /// <summary>
    /// Notes that tab membership changed — a tab opened, closed, detached, redocked, or gained
    /// a file path — and schedules the debounced write. Called by the tab watcher for
    /// everything visible on the strip, and explicitly by the detached register and
    /// <see cref="SaveQueryToPath"/> for the two changes the strip cannot show.
    /// </summary>
    private void RequestSessionPersist()
    {
        /* OnClosed is already writing the final authoritative list (and force-closing the
           detached windows, whose Forget calls land right back here). Nothing may re-arm the
           timer against a window being torn down. */
        if (IsShuttingDown)
            return;

        _sessionPersistPending = true;

        /* No real timer under the test host, in #451's pattern: the suite shares one
           dispatcher across every test, so a timer armed here would tick during some LATER
           test's RunJobs and write THIS window's tab list over whatever that test had staged
           in the redirected settings file — exactly the cross-test bleed the redirect exists
           to stop. Tests drive the flush deterministically through
           <see cref="FlushPendingSessionPersistForTests"/> instead. */
        if (AppRuntimeMode.IsTestHost)
            return;

        if (_sessionPersistTimer == null)
        {
            _sessionPersistTimer = new DispatcherTimer { Interval = SessionPersistDebounce };
            _sessionPersistTimer.Tick += (_, _) => FlushSessionPersist();
        }

        // Stop-then-start restarts the interval, which is what makes it a debounce.
        _sessionPersistTimer.Stop();
        _sessionPersistTimer.Start();
    }

    /// <summary>
    /// Writes a pending membership change down now. The timer's tick, the end of
    /// <see cref="RestoreOpenPlans"/>, and the test seam all land here; a flush with nothing
    /// pending is free.
    /// </summary>
    private void FlushSessionPersist()
    {
        _sessionPersistTimer?.Stop();

        if (!_sessionPersistPending || IsShuttingDown)
            return;

        _sessionPersistPending = false;
        SaveOpenPlans();
    }

    /// <summary>
    /// The deterministic stand-in for the debounce timer's tick — tests call this where the
    /// real app waits out <see cref="SessionPersistDebounce"/>. A seam rather than a sleep
    /// because the harness shares one dispatcher across the whole suite; see
    /// <see cref="RequestSessionPersist"/> for what a real timer does to that arrangement.
    /// </summary>
    internal void FlushPendingSessionPersistForTests() => FlushSessionPersist();

    /// <summary>
    /// Writes the open-tab list down for the session that comes back after a Velopack
    /// restart. <see cref="OnClosed"/> does this on every ordinary shutdown, but
    /// ApplyUpdatesAndRestart exits the process without closing the window — and
    /// <see cref="RestoreOpenPlans"/> already cleared the saved list at startup, so without
    /// this the updated app relaunched with nothing. #490's continuous writer usually has the
    /// list current by now anyway, but "usually" is a debounce interval wide; this write is
    /// what makes the restart exact.
    /// </summary>
    internal void PersistSessionForRestart() => SaveOpenPlans();

    /// <summary>
    /// Restores the tabs from the previous session. Skips files that no longer exist.
    /// Falls back to a new query tab if nothing was restored.
    ///
    /// <para>The saved list holds queries as well as plans, so it routes on extension the
    /// same way an ordinary file open does. Sending a .sql file to LoadPlanFile would greet
    /// the user with "the XML is not valid" where their query used to be.</para>
    ///
    /// <para><b>The crash-loop defense (#490).</b> The list is cleared and saved EMPTY before
    /// the first file is opened, so a file that crashes the app during its own load is already
    /// off the list when the next start reads it — a poisoned entry can never wedge the app
    /// into crashing at every launch. (The clear used to run after the loop, which only
    /// defended against crashes AFTER restore finished; a file that crashed DURING it left
    /// the list intact and looped forever.) Every file that opens successfully re-enters the
    /// list through the debounced writer, so the net behavior is strictly better than the old
    /// all-or-nothing: the poison never persists, and everything that actually opened
    /// does.</para>
    ///
    /// <para>Honestly, "everything that opened" holds for a crash after restore, not during
    /// it. The re-adds are debounced and this loop runs synchronously on the UI thread, so a
    /// crash while file B loads means file A's pending re-add never flushed and A is lost for
    /// that start too. Accepted: the invariant being defended is that the poison never
    /// persists, not that every good tab survives a mid-restore crash. The flush at the end
    /// is the other half of the deal — once restore completes, the rebuilt list is on disk
    /// immediately rather than a debounce-interval later, so the common case (restore fine,
    /// crash any time afterwards) loses nothing.</para>
    /// </summary>
    private void RestoreOpenPlans()
    {
        /* Snapshot first: SaveOpenPlans and this method share the live list, and the clear
           below would otherwise empty the very thing being iterated. */
        var savedTabs = _appSettings.OpenTabs.ToList();

        _appSettings.OpenTabs.Clear();
        AppSettingsService.Save(_appSettings);

        var restored = false;

        foreach (var path in savedTabs)
        {
            if (File.Exists(path))
            {
                OpenFileByExtension(path);
                restored = true;
            }
        }

        /* Each successful open armed the debounced writer through the tab watcher; write it
           down NOW so a crash a moment after startup still finds the session on disk. */
        FlushSessionPersist();

        if (!restored)
        {
            // Nothing to restore — open a fresh query editor like before
            NewQuery_Click(this, new RoutedEventArgs());
        }
    }
}
