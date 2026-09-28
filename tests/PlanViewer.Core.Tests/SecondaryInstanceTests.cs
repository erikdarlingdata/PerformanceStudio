using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #489 review: a window started with <c>--new-instance</c> beside a running Studio used to
/// restore the running one's saved open-tab list, so it opened a copy of every tab, shared the
/// running window's scratch buffer ids (both then wrote, dropped and swept the same files), and
/// whichever window closed last overwrote the other's list. <c>--new-instance</c> now claims the
/// single-instance slot first; when another instance holds it, this process is a secondary. A
/// secondary restores nothing, never writes the list or a scratch buffer, never sweeps the
/// buffer folder, and keeps the list already on disk when it saves settings. It loses crash
/// recovery for its own tabs and nothing else.
///
/// <para><b>What "leaves it alone" is checked against.</b> Every secondary test stages what a
/// running owner leaves on disk — a list with a file and a scratch entry, that scratch's buffer,
/// and an old orphaned buffer that an ordinary start would sweep — and compares the settings file
/// and the buffer folder before and after, content AND last-write time, so a write of identical
/// bytes still fails. The test host arms no real timers, so each test drives the flushes itself
/// (<see cref="MainWindow.FlushPendingSessionPersistForTests"/>,
/// <see cref="MainWindow.FlushPendingScratchPersistForTests"/>) and closes the window through
/// its real close path: a test that only waited would pass for the wrong reason.</para>
///
/// <para><b>Why this joins the serial collection.</b> <see cref="SingleInstance.IsSecondaryInstance"/>
/// is process-wide and changes what <see cref="AppSettingsService.Save"/> writes, the same kind of
/// hazard as the save block the collection was made for. A test in another class that saved
/// settings while it was on would have its open-tab list replaced with the one on disk.</para>
/// </summary>
[Collection("SettingsFileStore serial")]
public class SecondaryInstanceTests
{
    private const string OwnerScratchText = "SELECT 2 AS owner_scratch;";

    // ── the secondary ─────────────────────────────────────────────────────

    [Fact]
    public void ASecondaryWindowRestoresNoneOfTheSavedListAndLeavesItAlone()
    {
        AsSecondaryOverOwnerSession((window, owner) =>
        {
            var sessions = Sessions(window).ToList();
            Assert.DoesNotContain(sessions, s => s.SourceFilePath == owner.SqlPath);
            Assert.DoesNotContain(sessions, s => s.ScratchBufferId == owner.ScratchId);
            Assert.DoesNotContain(sessions, s => s.QueryEditor.Text == OwnerScratchText);

            /* What a launch with nothing to restore does: the usual new tab, empty and clean. */
            var only = Assert.Single(window.MainTabControl.Items.OfType<TabItem>());
            var session = Assert.IsType<QuerySessionControl>(only.Content);
            Assert.Null(session.SourceFilePath);
            Assert.False(session.IsDirty);

            /* Nothing pending anywhere, and a forced flush of both writers stays inert. */
            window.FlushPendingSessionPersistForTests();
            window.FlushPendingScratchPersistForTests();
        });
    }

    [Fact]
    public void ASecondaryStillOpensItsFileArgumentAndNothingElse()
    {
        AsSecondaryOverOwnerSession((window, owner) =>
        {
            var fileArg = TempSql("SELECT 1 AS launched_with_this;");
            try
            {
                var tabsBefore = window.MainTabControl.Items.Count; // the constructor's usual new tab

                window.OpenFromStartupArgs(new[] { "PerformanceStudio.exe", "--new-instance", fileArg });

                var sessions = Sessions(window).ToList();
                Assert.Single(sessions, s => s.SourceFilePath == fileArg);
                Assert.Equal(tabsBefore + 1, window.MainTabControl.Items.Count); // the file, no fallback tab
                Assert.DoesNotContain(sessions, s => s.SourceFilePath == owner.SqlPath);
                Assert.DoesNotContain(sessions, s => s.ScratchBufferId == owner.ScratchId);
            }
            finally
            {
                File.Delete(fileArg);
            }
        });
    }

    [Fact]
    public void ScratchTabsInASecondaryNeverTouchTheSavedListOrTheBufferFolder()
    {
        AsSecondaryOverOwnerSession((window, _) =>
        {
            var first = NewScratchTab(window, "SELECT 1 AS typed_in_secondary;");
            window.FlushPendingScratchPersistForTests();

            first.QueryEditor.Text = "SELECT 1 AS edited_in_secondary;";
            window.FlushPendingScratchPersistForTests();

            NewScratchTab(window, "SELECT 2 AS second_scratch;");
            window.FlushPendingSessionPersistForTests();
            window.PersistSessionForRestart(); // the update-restart write

            /* Close the first tab through the real button and answer Don't Save: the moment an
               ordinary window deletes the buffer it wrote for that tab. */
            CloseButton(TabOf(window, first)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            AnswerDontSave(window);
            Assert.DoesNotContain(first, Sessions(window));
            window.FlushPendingScratchPersistForTests();

            /* Then close the window with the second tab still dirty: the walk asks, Don't Save
               lets it through, and OnClosed runs its final drain and list write. */
            window.Close();
            Dispatcher.UIThread.RunJobs();
            AnswerDontSave(window);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void ASecondaryStillAsksAboutADirtyScratchTabWhenItCloses()
    {
        AsSecondaryOverOwnerSession((window, _) =>
        {
            var session = NewScratchTab(window, "SELECT 1 AS unsaved_in_secondary;");
            window.FlushPendingScratchPersistForTests(); // persistence is off, so no buffer holds this text

            Assert.True(window.CloseNeedsConfirmation());

            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible, "the close is held while the question is up");
            var prompt = Assert.Single(window.OwnedWindows);

            /* Dismissing the prompt is Cancel: the window stays and the text is still there. */
            prompt.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.Equal("SELECT 1 AS unsaved_in_secondary;", session.QueryEditor.Text);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            AnswerDontSave(window);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void ASecondarysSettingsSaveKeepsTheListThatIsOnDisk()
    {
        AsSecondaryOverOwnerSession((window, owner) =>
        {
            /* The owner keeps working after this window started: it opens another tab and
               rewrites the list. Written straight to the file, as another process would, so this
               process's cached settings still hold the list they read at startup. */
            var opened = Path.Combine(Path.GetTempPath(), "opened_by_the_owner_later.sql");
            var later = new List<string>
            {
                owner.SqlPath,
                ScratchBufferStore.EntryFor(owner.ScratchId),
                opened,
            };
            var ownerCopy = AppSettingsService.Load().Clone();
            ownerCopy.OpenTabs = later;
            File.WriteAllText(AppSettingsService.SettingsFilePath, JsonSerializer.Serialize(ownerCopy));

            /* A save the secondary makes for its own reasons: opening a plan puts it on the
               Recent Plans list, which writes the whole settings file. */
            var plan = Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");
            window.LoadPlanFile(plan);

            var saved = ReadSettingsFile();
            Assert.Equal(later, saved.OpenTabs);
            Assert.Contains(Path.GetFullPath(plan), saved.RecentPlans); // the save itself did happen
        },
        filesMustStay: false);
    }

    [Fact]
    public void ASecondaryThatCannotReadTheSavedListDoesNotSave()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Unix permissions don't reliably block a same-user read the way FileShare.None does on Windows.");

        var owner = StageOwnerSession();
        var path = AppSettingsService.SettingsFilePath;
        var settings = AppSettingsService.Load();
        var originalDays = settings.QueryStoreSlicerDays;
        try
        {
            var before = File.ReadAllBytes(path);
            settings.QueryStoreSlicerDays = 91;

            SingleInstance.IsSecondaryInstance = true;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AppSettingsService.Save(settings); // must not throw
            }
            SingleInstance.IsSecondaryInstance = false;

            /* Not written, and not even attempted: an attempt would have staged a .tmp
               sibling and then failed on the rename. With no way to see the list there is no
               list to keep, and a guess is not written. */
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            SingleInstance.IsSecondaryInstance = false;
            settings.QueryStoreSlicerDays = originalDays;
            File.Delete(path + ".tmp");
            ResetState(owner);
        }
    }

    // ── the slot claim ────────────────────────────────────────────────────

    [Fact]
    public void ANewInstanceLaunchBesideAnOwnerRunsAsASecondary()
    {
        var name = UniqueMutexName();
        using var runningOwner = new Mutex(initiallyOwned: true, name, out var held);
        Assert.True(held);

        try
        {
            PlanViewer.App.Program.ClaimSlotForNewInstance(name);

            Assert.True(SingleInstance.IsSecondaryInstance);
        }
        finally
        {
            SingleInstance.IsSecondaryInstance = false;
        }
    }

    [Fact]
    public void ANewInstanceLaunchWithNoOtherOwnerClaimsTheSlotAndRestoresNormally()
    {
        var name = UniqueMutexName();

        HeadlessUi.Run(() =>
        {
            var owner = StageOwnerSession();
            MainWindow? window = null;
            try
            {
                PlanViewer.App.Program.ClaimSlotForNewInstance(name);

                Assert.False(SingleInstance.IsSecondaryInstance, "the slot was free, so this launch owns it");

                /* And it holds the slot now: the next launch finds the name taken and hands its
                   work over, which is the whole point of claiming it. */
                using (var probe = new Mutex(initiallyOwned: false, name, out var created))
                    Assert.False(created);

                window = new MainWindow();
                window.Show();
                AssertOwnsTheSavedSession(window, owner);
            }
            finally
            {
                SingleInstance.IsSecondaryInstance = false;
                PutAway(window);
                ResetState(owner);
            }
        });
    }

    [Fact]
    public void ANormalLaunchIsUnchanged()
    {
        HeadlessUi.Run(() =>
        {
            var owner = StageOwnerSession();
            MainWindow? window = null;
            try
            {
                Assert.False(SingleInstance.IsSecondaryInstance, "nothing sets the flag on an ordinary launch");

                window = new MainWindow();
                window.Show();
                AssertOwnsTheSavedSession(window, owner);
            }
            finally
            {
                PutAway(window);
                ResetState(owner);
            }
        });
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    /// <summary>What a running owner leaves on disk, as far as these tests care.</summary>
    private sealed record OwnerSession(string SqlPath, Guid ScratchId, Guid OrphanId);

    /// <summary>
    /// The whole secondary scenario, through the same door production uses: a slot already held
    /// by "the running instance" (a mutex of this test's own name), a <c>--new-instance</c>
    /// launch claiming it and finding it taken, and then a window built under the flag that
    /// leaves behind. The window is closed while still a secondary, and unless a test says
    /// otherwise the owner's files must come out exactly as they went in.
    /// </summary>
    private static void AsSecondaryOverOwnerSession(
        Action<MainWindow, OwnerSession> body, bool filesMustStay = true)
    {
        HeadlessUi.Run(() =>
        {
            var owner = StageOwnerSession();
            MainWindow? window = null;
            try
            {
                var before = Snapshot();

                var name = UniqueMutexName();
                using var runningOwner = new Mutex(initiallyOwned: true, name, out var held);
                Assert.True(held);
                PlanViewer.App.Program.ClaimSlotForNewInstance(name);
                Assert.True(SingleInstance.IsSecondaryInstance, "the slot is held, so this launch is a secondary");

                try
                {
                    window = new MainWindow();
                    window.Show();
                    body(window, owner);
                }
                finally
                {
                    PutAway(window);
                    SingleInstance.IsSecondaryInstance = false;
                }

                if (filesMustStay)
                    AssertUnchanged(before);
            }
            finally
            {
                ResetState(owner);
            }
        });
    }

    /// <summary>
    /// What ordinary startup did before and still does: restore the list in place, sweep debris,
    /// keep the restored scratch's buffer, and keep persisting new scratch content and the list.
    /// </summary>
    private static void AssertOwnsTheSavedSession(MainWindow window, OwnerSession owner)
    {
        var sessions = Sessions(window).ToList();
        Assert.Equal(2, window.MainTabControl.Items.Count); // the file and the scratch, no extra new tab
        Assert.Single(sessions, s => s.SourceFilePath == owner.SqlPath);

        var scratch = Assert.Single(sessions, s => s.ScratchBufferId == owner.ScratchId);
        Assert.Equal(OwnerScratchText, scratch.QueryEditor.Text);
        Assert.True(scratch.IsDirty);

        Assert.True(File.Exists(ScratchBufferStore.BufferPathFor(owner.ScratchId)));
        Assert.False(File.Exists(ScratchBufferStore.BufferPathFor(owner.OrphanId)),
            "an ordinary start sweeps a buffer nothing lists");
        Assert.Equal(
            new[] { owner.SqlPath, ScratchBufferStore.EntryFor(owner.ScratchId) },
            ReadSettingsFile().OpenTabs);

        var typed = NewScratchTab(window, "SELECT 9 AS typed_by_the_owner;");
        window.FlushPendingScratchPersistForTests();

        var id = typed.ScratchBufferId;
        Assert.NotNull(id);
        Assert.Equal(
            "SELECT 9 AS typed_by_the_owner;",
            File.ReadAllText(ScratchBufferStore.BufferPathFor(id!.Value)));
        Assert.Contains(ScratchBufferStore.EntryFor(id.Value), ReadSettingsFile().OpenTabs);
    }

    /// <summary>
    /// Writes what a running owner leaves behind: the list (a file tab, then a scratch tab), the
    /// scratch tab's buffer, and one buffer nothing lists that is old enough for a start-up sweep
    /// to delete. Done in ordinary mode, before any secondary exists, and left in the shared
    /// cache as well as on disk, which is where the next window reads its list from.
    /// </summary>
    private static OwnerSession StageOwnerSession()
    {
        var sqlPath = TempSql("SELECT 1 AS owner_file;");
        var scratchId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();

        ScratchBufferStore.Write(scratchId, OwnerScratchText);
        ScratchBufferStore.Write(orphanId, "SELECT 3 AS orphan;");
        File.SetLastWriteTimeUtc(
            ScratchBufferStore.BufferPathFor(orphanId),
            DateTime.UtcNow - ScratchBufferStore.OrphanGracePeriod - TimeSpan.FromDays(1));

        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        settings.OpenTabs.Add(sqlPath);
        settings.OpenTabs.Add(ScratchBufferStore.EntryFor(scratchId));
        AppSettingsService.Save(settings);

        return new OwnerSession(sqlPath, scratchId, orphanId);
    }

    /// <summary>Leaves nothing for the next test's window to restore or sweep.</summary>
    private static void ResetState(OwnerSession? owner)
    {
        SingleInstance.IsSecondaryInstance = false; // the save below has to be an ordinary one

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
                // Best-effort — a stuck file is swept by a later window anyway.
            }
        }

        if (owner != null)
            File.Delete(owner.SqlPath);
    }

    /// <summary>
    /// The settings file and every file in the scratch folder, with the time each was last
    /// written. Content alone would let a rewrite of identical bytes through.
    /// </summary>
    private static SortedDictionary<string, (byte[] Bytes, long Written)> Snapshot()
    {
        var snapshot = new SortedDictionary<string, (byte[] Bytes, long Written)>(StringComparer.Ordinal);

        void Add(string key, string path) =>
            snapshot[key] = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path).Ticks);

        Add("appsettings.json", AppSettingsService.SettingsFilePath);
        foreach (var file in ScratchFiles())
            Add("scratch/" + Path.GetFileName(file), file);

        return snapshot;
    }

    private static void AssertUnchanged(SortedDictionary<string, (byte[] Bytes, long Written)> before)
    {
        var after = Snapshot();

        Assert.Equal(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (var (name, was) in before)
        {
            Assert.True(was.Bytes.AsSpan().SequenceEqual(after[name].Bytes), $"{name} changed");
            Assert.True(was.Written == after[name].Written, $"{name} was written to");
        }
    }

    private static AppSettings ReadSettingsFile() =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettingsService.SettingsFilePath))!;

    private static string[] ScratchFiles() =>
        Directory.Exists(AppSettingsService.ScratchDirectory)
            ? Directory.GetFiles(AppSettingsService.ScratchDirectory)
            : Array.Empty<string>();

    /// <summary>
    /// A name of this test's own, so nothing here can meet the real slot on a developer's
    /// machine, or another run's. Same reasoning as SingleInstanceTests' mutex self-test.
    /// </summary>
    private static string UniqueMutexName() =>
        $"{SingleInstance.MutexName}_secondary_{Guid.NewGuid():N}";

    private static QuerySessionControl NewScratchTab(MainWindow window, string text)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        var session = Sessions(window).Last();
        session.QueryEditor.Text = text;
        return session;
    }

    /// <summary>Clicks Don't Save on the one prompt the window is asking.</summary>
    private static void AnswerDontSave(MainWindow window)
    {
        PromptButton(Assert.Single(window.OwnedWindows), "Don't Save")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Prompts closed, every session settled, window shut — from a finally, because the run
    /// where it matters is the run where an assertion above it failed (#474).
    /// </summary>
    private static void PutAway(MainWindow? window)
    {
        if (window == null || !window.IsVisible)
            return;

        foreach (var prompt in window.OwnedWindows.ToList())
            prompt.Close();

        foreach (var session in Sessions(window).ToList())
            session.MarkClean();

        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static TabItem TabOf(MainWindow window, QuerySessionControl session) =>
        window.MainTabControl.Items.OfType<TabItem>().Single(t => t.Content == session);

    private static Button CloseButton(TabItem tab) =>
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single();

    /// <summary>
    /// Digs the named button out of an UnsavedChangesDialog: content StackPanel, then the button
    /// row, then the caption. Same shape as ScratchBufferPersistenceTests.PromptButton.
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
