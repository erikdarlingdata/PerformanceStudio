using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #496: after #495 made the open-tab list survive abnormal exits, the one thing still lost
/// to a crash was a scratch tab's CONTENT — typed, never saved, backed by nothing. Content
/// now persists continuously to one buffer file per scratch tab (debounced, beside the
/// settings file), the tab rides the #495 list as a <c>scratch:&lt;guid&gt;</c> entry in
/// strip order, and the buffer dies at exactly the moments the user chooses its fate. The
/// design center under every test here: a buffer the user CHOSE to discard dies; a buffer
/// they NEVER GOT TO CHOOSE about survives.
///
/// <para>Same disciplines as SessionPersistenceTests, because this is the same feature one
/// layer deeper: assertions read the redirected FILES (#451/#487 — which is also what makes
/// letting the startup orphan sweep actually delete things safe), the debounce is driven
/// through <see cref="MainWindow.FlushPendingScratchPersistForTests"/> rather than waited
/// out (shared dispatcher — see RequestSessionPersist), and every test that stages
/// persisted state blanks it on the way out, list and buffer files both, because the next
/// MainWindow constructed anywhere in the run restores the one and sweeps the other.</para>
/// </summary>
public class ScratchBufferPersistenceTests
{
    [Fact]
    public void TypedScratchContentIsPersistedWithoutClosingTheApp()
    {
        HeadlessUi.Run(() =>
        {
            try
            {
                var window = new MainWindow();
                var session = NewScratchTab(window, "SELECT 1 AS scratch_live;");

                /* The keystrokes armed the content debounce through DirtyStateChanged; the
                   seam stands in for its tick. Before #496 this text existed nowhere but
                   the editor. */
                window.FlushPendingScratchPersistForTests();

                var id = session.ScratchBufferId;
                Assert.NotNull(id); // minted at first persist, not at construction

                Assert.Equal(
                    "SELECT 1 AS scratch_live;",
                    File.ReadAllText(ScratchBufferStore.BufferPathFor(id!.Value)));

                /* And the entry landed with the buffer, not a debounce later — the seam
                   mirrors the real tick's chained membership flush, so a crash right after
                   the idle write already finds both halves on disk. */
                Assert.Contains(ScratchBufferStore.EntryFor(id.Value), PersistedOpenTabs());
            }
            finally
            {
                ResetScratchState();
            }
        });
    }

    [Fact]
    public void ARestoredScratchTabIsDirtyIntactAndContinuesItsBuffer()
    {
        HeadlessUi.Run(() =>
        {
            var id = Guid.NewGuid();
            try
            {
                ScratchBufferStore.Write(id, "SELECT 1 AS survived_the_crash;");
                Seed(ScratchBufferStore.EntryFor(id));

                var window = new MainWindow();

                var session = Sessions(window).Single(s => s.ScratchBufferId == id);
                Assert.Equal("SELECT 1 AS survived_the_crash;", session.QueryEditor.Text);

                /* Dirty by definition: nothing the user chose backs this content, so the
                   modified marker and every close prompt must treat it as unsaved work. */
                Assert.True(session.IsDirty);
                Assert.Null(session.SourceFilePath);

                /* The SAME id came back — this session continues its buffer across
                   restarts rather than forking a new file per launch — and restore
                   re-listed it immediately, same as it does for file tabs. */
                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(id)));
                Assert.Contains(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());
            }
            finally
            {
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// The reason scratch entries live INSIDE the one ordered list instead of appended to
    /// it: a scratch tab sitting between two file tabs comes back between them. (#495's
    /// detached entries keep their documented append-after compromise; this pins that
    /// docked tabs never inherited it.)
    /// </summary>
    [Fact]
    public void AScratchEntryKeepsItsPlaceAmongFilePaths()
    {
        HeadlessUi.Run(() =>
        {
            var first = TempSql("SELECT 1 AS first;");
            var last = TempSql("SELECT 2 AS last;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(first);
                var session = NewScratchTab(window, "SELECT 0 AS in_between;");
                window.LoadSqlFile(last);

                window.FlushPendingScratchPersistForTests();

                var entry = ScratchBufferStore.EntryFor(session.ScratchBufferId!.Value);
                Assert.Equal(new[] { first, entry, last }, PersistedOpenTabs());
            }
            finally
            {
                ResetScratchState();
                File.Delete(first);
                File.Delete(last);
            }
        });
    }

    /// <summary>
    /// The chose half of the design center, through the real prompt: Don't Save is the user
    /// answering "this content may die", and the buffer obeys — or a discarded query would
    /// resurrect at the next start and the prompt's answer would mean nothing.
    /// </summary>
    [Fact]
    public void DontSaveAtTheClosePromptDeletesTheBuffer()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show(); // the prompt's ShowDialog needs a visible owner, headless or not

                var session = NewScratchTab(window, "SELECT 1 AS discarded_on_purpose;");
                window.FlushPendingScratchPersistForTests();

                var id = session.ScratchBufferId!.Value;
                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(id)));

                var tab = TabOf(window, session);
                CloseButton(tab).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                var prompt = Assert.Single(window.OwnedWindows);
                PromptButton(prompt, "Don't Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.DoesNotContain(tab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(id)),
                    "Don't Save is a choice, and a chosen buffer dies");
                Assert.Null(session.ScratchBufferId);

                window.FlushPendingScratchPersistForTests();
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());
            }
            finally
            {
                PutAwayMainWindow(window);
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// The other way a user chooses: a save that succeeds moves the content into a real
    /// file, the list entry becomes the path, and the buffer that was protecting the
    /// content retires.
    /// </summary>
    [Fact]
    public void ASuccessfulSaveTurnsTheEntryIntoThePathAndDeletesTheBuffer()
    {
        HeadlessUi.Run(() =>
        {
            var savedAs = Path.Combine(Path.GetTempPath(), $"saved_{Path.GetRandomFileName()}.sql");
            try
            {
                var window = new MainWindow();
                var session = NewScratchTab(window, "SELECT 1 AS graduated;");
                window.FlushPendingScratchPersistForTests();

                var id = session.ScratchBufferId!.Value;
                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(id)));
                Assert.Contains(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());

                Assert.True(window.SaveQueryToPath(TabOf(window, session), session, savedAs));
                window.FlushPendingScratchPersistForTests();

                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(id)),
                    "content saved is content chosen — the real file owns it now");
                Assert.Null(session.ScratchBufferId);

                var persisted = PersistedOpenTabs();
                Assert.Contains(savedAs, persisted);
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(id), persisted);
            }
            finally
            {
                ResetScratchState();
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    /// <summary>
    /// The consequence the design center promises: a CLEAN close leaves zero buffers,
    /// because every scratch tab with content was asked about on the way out — buffers
    /// exist on disk only after an exit nobody got to answer. This drives the whole route:
    /// window close, the #462/#473 walk, Don't Save clicked, OnClosed's final drain and
    /// list write.
    /// </summary>
    [Fact]
    public void ACleanCloseLeavesNoScratchBuffersBehind()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? window = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show();

                session = NewScratchTab(window, "SELECT 1 AS asked_about;");
                window.FlushPendingScratchPersistForTests();
                Assert.Single(ScratchFiles());

                window.Close();
                Dispatcher.UIThread.RunJobs();

                var prompt = Assert.Single(window.OwnedWindows);
                PromptButton(prompt, "Don't Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.False(window.IsVisible, "the answered walk lets the close proceed");
                Assert.Empty(ScratchFiles());
                Assert.DoesNotContain(PersistedOpenTabs(),
                    e => e.StartsWith(ScratchBufferStore.EntryPrefix, StringComparison.Ordinal));
            }
            finally
            {
                PutAwayMainWindow(window, session);
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// The tab nobody is ever asked about: a scratch whose text was deleted back out is
    /// clean (its saved-text baseline is forever ""), holds no unsaved work, and must not
    /// resurrect its old text at the next start as if the deletion never happened. This is
    /// also the branch that keeps the zero-buffers-after-clean-close invariant airtight for
    /// tabs the prompts skip.
    /// </summary>
    [Fact]
    public void AnEmptiedScratchTabShedsItsBufferOnTheNextFlush()
    {
        HeadlessUi.Run(() =>
        {
            try
            {
                var window = new MainWindow();
                var session = NewScratchTab(window, "SELECT 1 AS typed_then_deleted;");
                window.FlushPendingScratchPersistForTests();

                var id = session.ScratchBufferId!.Value;
                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(id)));

                session.QueryEditor.Text = "";
                window.FlushPendingScratchPersistForTests();

                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(id)));
                Assert.Null(session.ScratchBufferId);
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());
            }
            finally
            {
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// Startup collects what nothing references: buffers stranded by crash-window skew and
    /// AtomicFile .tmp siblings alike — deletion normally happens at the moment of choice,
    /// so unreferenced means debris. The referenced buffer rides through untouched.
    /// </summary>
    [Fact]
    public void OrphanedBufferFilesAreSweptAtStartupAndReferencedOnesAreKept()
    {
        HeadlessUi.Run(() =>
        {
            var kept = Guid.NewGuid();
            var orphan = Guid.NewGuid();
            var strayTmp = ScratchBufferStore.BufferPathFor(orphan) + ".tmp";
            try
            {
                ScratchBufferStore.Write(kept, "SELECT 1 AS referenced;");
                ScratchBufferStore.Write(orphan, "SELECT 2 AS stranded;");
                File.WriteAllText(strayTmp, "half a write");
                Seed(ScratchBufferStore.EntryFor(kept));

                var window = new MainWindow();

                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(kept)));
                Assert.NotNull(Sessions(window).SingleOrDefault(s => s.ScratchBufferId == kept));

                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(orphan)));
                Assert.False(File.Exists(strayTmp));
            }
            finally
            {
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// #495's poison invariant, mirrored for buffers: an entry whose buffer cannot load is
    /// skipped, never re-added (the poison-defense clear already ran, and no session exists
    /// to re-list it), and its file is removed rather than left to fail at every start. An
    /// unreadable buffer (a directory squatting on its path) and an empty one (debris the
    /// writer would never produce) both walk that path; the healthy entry next to them
    /// restores.
    /// </summary>
    [Fact]
    public void AFailedBufferLoadIsSkippedNotReAddedAndSwept()
    {
        HeadlessUi.Run(() =>
        {
            var unreadable = Guid.NewGuid();
            var empty = Guid.NewGuid();
            var healthy = Guid.NewGuid();
            var unreadablePath = ScratchBufferStore.BufferPathFor(unreadable);
            try
            {
                // A directory where the buffer file should be: File.ReadAllText throws.
                Directory.CreateDirectory(unreadablePath);
                ScratchBufferStore.Write(empty, "");
                ScratchBufferStore.Write(healthy, "SELECT 1 AS healthy;");
                Seed(
                    ScratchBufferStore.EntryFor(unreadable),
                    ScratchBufferStore.EntryFor(empty),
                    ScratchBufferStore.EntryFor(healthy));

                var window = new MainWindow();

                Assert.NotNull(Sessions(window).SingleOrDefault(s => s.ScratchBufferId == healthy));
                Assert.Null(Sessions(window).SingleOrDefault(s => s.ScratchBufferId == unreadable));
                Assert.Null(Sessions(window).SingleOrDefault(s => s.ScratchBufferId == empty));

                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(empty)));

                /* Restore rewrote the list on its way out; the poisoned entries must not
                   have ridden back in. */
                var persisted = PersistedOpenTabs();
                Assert.Contains(ScratchBufferStore.EntryFor(healthy), persisted);
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(unreadable), persisted);
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(empty), persisted);
            }
            finally
            {
                ResetScratchState();
                if (Directory.Exists(unreadablePath))
                    Directory.Delete(unreadablePath);
            }
        });
    }

    /// <summary>
    /// The cap: a pathological paste must not put megabytes on the idle writer's two-second
    /// cadence, so past ~1MB the tab simply behaves pre-#496 — and an already-written
    /// smaller buffer is removed rather than left to restore a stale fragment of what the
    /// user actually had. Shrinking back under the cap resumes persistence.
    /// </summary>
    [Fact]
    public void ABufferOverTheSizeCapIsNotPersistedAndAStaleOneIsRemoved()
    {
        HeadlessUi.Run(() =>
        {
            try
            {
                var window = new MainWindow();
                var session = NewScratchTab(window, "SELECT 1 AS small;");
                window.FlushPendingScratchPersistForTests();

                var firstId = session.ScratchBufferId!.Value;
                Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(firstId)));

                // One char past MainWindow.MaxScratchPersistChars (1 MiB).
                session.QueryEditor.Text = new string('x', (1024 * 1024) + 1);
                window.FlushPendingScratchPersistForTests();

                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(firstId)));
                Assert.Null(session.ScratchBufferId);
                Assert.Empty(ScratchFiles());

                session.QueryEditor.Text = "SELECT 1 AS small_again;";
                window.FlushPendingScratchPersistForTests();

                Assert.NotNull(session.ScratchBufferId);
                Assert.Equal(
                    "SELECT 1 AS small_again;",
                    File.ReadAllText(ScratchBufferStore.BufferPathFor(session.ScratchBufferId!.Value)));
            }
            finally
            {
                ResetScratchState();
            }
        });
    }

    /// <summary>
    /// A list from before #496 is nothing but plain paths, and a new build treats it
    /// exactly as #495 did — restored by extension, rewritten as paths, no scratch
    /// machinery invoked and no scratch files invented. Zero version fields is the whole
    /// compatibility design, so this pins its new-reading-old half.
    /// </summary>
    [Fact]
    public void AnOldFormatPlainPathListRestoresUnchanged()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS plain_old_path;");
            try
            {
                Seed(path);

                var window = new MainWindow();

                var session = Sessions(window).Single(s => s.SourceFilePath == path);
                Assert.Equal("SELECT 1 AS plain_old_path;", session.QueryEditor.Text);
                Assert.Equal(new[] { path }, PersistedOpenTabs());
                Assert.Empty(ScratchFiles());
            }
            finally
            {
                ResetScratchState();
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The old-reading-new half, pinned the way #494's activation sentinel taught: as the
    /// STRING PROPERTY that makes it true. An old build reading a new list guards every
    /// entry with File.Exists, so a scratch entry is safe to hand it exactly because the
    /// entry can never name an existing file — the colon is not legal in a Windows file
    /// name, and the app only ever writes absolute paths to the list, which a scratch entry
    /// is not on any platform. A File.Exists assertion here would prove nothing on a runner
    /// whose filesystem allows colons; the prefix's shape is the actual contract, so the
    /// prefix's shape is what this test holds still.
    /// </summary>
    [Fact]
    public void TheScratchEntryPrefixCanNeverNameAnExistingFile()
    {
        Assert.Contains(':', ScratchBufferStore.EntryPrefix);
        Assert.Equal("scratch:", ScratchBufferStore.EntryPrefix);

        var id = Guid.NewGuid();
        var entry = ScratchBufferStore.EntryFor(id);
        Assert.StartsWith(ScratchBufferStore.EntryPrefix, entry, StringComparison.Ordinal);

        // Roundtrip: what the writer emits is what restore routes to a buffer.
        Assert.True(ScratchBufferStore.TryParseEntry(entry, out var parsed));
        Assert.Equal(id, parsed);

        /* Everything that is not a well-formed entry routes as a PATH — including a
           prefixed entry with a mangled tail, and a real file on a colon-tolerant
           filesystem that happens to be named like one. */
        Assert.False(ScratchBufferStore.TryParseEntry(null, out _));
        Assert.False(ScratchBufferStore.TryParseEntry(@"C:\temp\query.sql", out _));
        Assert.False(ScratchBufferStore.TryParseEntry("scratch:not-a-guid", out _));
        Assert.False(ScratchBufferStore.TryParseEntry("SCRATCH:" + id.ToString("N"), out _),
            "the prefix is exact — case-mangled entries fall through to path handling");
    }

    /// <summary>
    /// #496's sixth requirement: off the tab strip is not out of the feature. A detached
    /// scratch window's edits keep flowing to the same buffer (the subscription lives on
    /// the session, which detach moves and redock reuses), its entry rides the #495
    /// detached register half of the list — and Don't Save at ITS close prompt kills the
    /// buffer the same way a docked one's does.
    /// </summary>
    [Fact]
    public void ADetachedScratchWindowPersistsAndItsDontSaveDeletes()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? window = null;
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show();

                session = NewScratchTab(window, "SELECT 1 AS docked_first;");
                window.FlushPendingScratchPersistForTests();
                var id = session.ScratchBufferId!.Value;

                detached = window.DetachTabToWindow(TabOf(window, session))!;
                Dispatcher.UIThread.RunJobs();

                session.QueryEditor.Text = "SELECT 2 AS edited_detached;";
                window.FlushPendingScratchPersistForTests();

                Assert.Equal(
                    "SELECT 2 AS edited_detached;",
                    File.ReadAllText(ScratchBufferStore.BufferPathFor(id)));
                Assert.Contains(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());

                detached.Close();
                Dispatcher.UIThread.RunJobs();

                var prompt = Assert.Single(detached.OwnedWindows);
                PromptButton(prompt, "Don't Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.False(detached.IsVisible);
                Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(id)),
                    "a chosen buffer dies in a detached window too");

                window.FlushPendingScratchPersistForTests();
                Assert.DoesNotContain(ScratchBufferStore.EntryFor(id), PersistedOpenTabs());
            }
            finally
            {
                PutAwayDetached(detached, session);
                PutAwayMainWindow(window, session);
                ResetScratchState();
            }
        });
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a fresh scratch tab and types into it — through NewQuery_Click and the editor
    /// text, so the persistence subscription is exercised the way a user exercises it.
    /// </summary>
    private static QuerySessionControl NewScratchTab(MainWindow window, string text)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        var session = Sessions(window).Last();
        session.QueryEditor.Text = text;
        return session;
    }

    /// <summary>
    /// What is actually on disk, read back through the same serializer the app writes with
    /// — same rationale as SessionPersistenceTests: crash survival is a property of the
    /// file, so the file is what gets asserted.
    /// </summary>
    private static List<string> PersistedOpenTabs()
    {
        var json = File.ReadAllText(AppSettingsService.SettingsFilePath);
        return JsonSerializer.Deserialize<AppSettings>(json)!.OpenTabs;
    }

    /// <summary>Every file currently in the redirected scratch directory.</summary>
    private static string[] ScratchFiles() =>
        Directory.Exists(AppSettingsService.ScratchDirectory)
            ? Directory.GetFiles(AppSettingsService.ScratchDirectory)
            : Array.Empty<string>();

    /// <summary>
    /// Stages entries where the next MainWindow will look — Load returns the process-wide
    /// cached instance, the very object the window reads (see RestoreQueryTabsTests.Seed).
    /// </summary>
    private static void Seed(params string[] entries)
    {
        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        settings.OpenTabs.AddRange(entries);
    }

    /// <summary>
    /// Leaves nothing for the next test's MainWindow to restore or sweep: the list (cache
    /// and file, same as SessionPersistenceTests.ResetPersistedState) and every buffer
    /// file. Buffer hygiene matters doubly here — a leaked scratch entry would restore a
    /// phantom tab in an unrelated test, and a leaked buffer file would be "swept" by that
    /// test's window, hiding a real sweep bug or inventing one.
    /// </summary>
    private static void ResetScratchState()
    {
        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        AppSettingsService.Save(settings);

        foreach (var file in ScratchFiles())
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort — a stuck file will be swept by a later window anyway.
            }
        }
    }

    /// <summary>
    /// Same job as UpdateRestartGuardTests.PutAway: prompts closed, session settled, window
    /// shut — from a finally, because the run where it matters is the run where an
    /// assertion failed and left the window up (#474).
    /// </summary>
    private static void PutAwayMainWindow(MainWindow? window, QuerySessionControl? session = null)
    {
        if (window == null || !window.IsVisible)
            return;

        foreach (var prompt in window.OwnedWindows.ToList())
            prompt.Close();

        session?.MarkClean();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Detached twin of the above, mirroring DetachedUnsavedChangesTests.PutAway.</summary>
    private static void PutAwayDetached(Window? detached, QuerySessionControl? session)
    {
        if (detached == null || !detached.IsVisible)
            return;

        foreach (var prompt in detached.OwnedWindows.ToList())
            prompt.Close();

        session?.MarkClean();
        Dispatcher.UIThread.RunJobs();
        detached.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static TabItem TabOf(MainWindow window, QuerySessionControl session) =>
        window.MainTabControl.Items.OfType<TabItem>().Single(t => t.Content == session);

    private static Button CloseButton(TabItem tab) =>
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single();

    /// <summary>
    /// Digs the named button out of an UnsavedChangesDialog: content StackPanel, then the
    /// button row, then the caption. Same shape as OpenInEditorOverwriteTests.PromptButton.
    /// </summary>
    private static Button PromptButton(Window prompt, string caption) =>
        ((StackPanel)prompt.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single(b => (string?)b.Content == caption);

    private static IEnumerable<QuerySessionControl> Sessions(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>()
            .Select(t => t.Content).OfType<QuerySessionControl>();

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }
}
