using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    // ── Scratch buffer content persistence (#496) ─────────────────────────

    /* #495 made the open-tab LIST survive abnormal exits, which brought back every tab that
       had a file behind it. The remaining loss was the tab that never had one: a scratch
       query — typed, never saved — kept its place on nothing and lost its CONTENT to any
       crash, task kill, or OS "shut down anyway". Every interactive way to discard that
       content already stops and asks (#462/#469/#473/#477), so the design center here is
       the gap those prompts cannot cover:

           a buffer the user CHOSE to discard dies;
           a buffer they NEVER GOT TO CHOOSE about survives.

       Concretely: content is written continuously (debounced) to one file per scratch tab,
       the tab enters the #495 session list as a scratch:<guid> entry in strip order, and
       the buffer file is deleted at exactly the moments the user answers for it — Don't
       Save at any prompt, or a save that moves the content into a real file. After a clean
       close, every scratch buffer is therefore gone, because every one of them was chosen
       about; buffers exist on disk only after an exit nobody was asked about.

       SCOPE FENCE, on purpose: only SCRATCH tabs' content is persisted. Unsaved edits to a
       FILE-backed tab stay guarded by the prompts alone — the file is the durable copy the
       user opted into, and shadowing every open file's edits is a different feature with
       different questions (staleness against on-disk changes, most of all). That is #496's
       issue scope, not an oversight. */

    /// <summary>
    /// How long the writer waits after the last edit before writing a scratch buffer down.
    /// Deliberately its own debounce, longer than <see cref="SessionPersistDebounce"/>:
    /// membership changes are click-scale and rare, content changes are keystroke-scale and
    /// constant, and reusing the membership timer would either write the settings file on a
    /// typing cadence or slow membership writes to a typing idle. Two timers, two cadences,
    /// one flush discipline (each flush point drains both — see <see cref="FlushSessionPersist"/>).
    /// </summary>
    private static readonly TimeSpan ScratchPersistDebounce = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A scratch buffer larger than this is not persisted. A pathological paste must not
    /// grind the idle writer — rewriting megabytes to disk two seconds after every
    /// keystroke — so past the cap that one tab simply behaves as it did before #496:
    /// prompts guard it, crashes lose it. Chars rather than bytes because the check has to
    /// be free on every flush; for SQL text the two are within a small factor of each other,
    /// and the cap is a courtesy threshold, not a contract.
    /// </summary>
    private const int MaxScratchPersistChars = 1024 * 1024;

    /// <summary>Trailing-edge debounce for the content writer; every request restarts it.</summary>
    private DispatcherTimer? _scratchPersistTimer;

    /// <summary>
    /// The scratch sessions whose content changed since the last flush. A set keyed on the
    /// session (not the tab) because content identity lives on the session — a detached
    /// scratch window (#473) edits the same session object and lands in the same set, which
    /// is the whole of how detached scratch persistence works.
    /// </summary>
    private readonly HashSet<QuerySessionControl> _scratchPersistPending = new();

    /// <summary>
    /// Which sessions already have the persistence subscription, so the re-subscription
    /// path (redock rebuilds a tab around a living session via CreateTab) does not stack a
    /// second handler. ConditionalWeakTable for the same reason _tabDirtyGlyphUnhooks is
    /// one: the bookkeeping must not outlive the session it is about.
    /// </summary>
    private readonly ConditionalWeakTable<QuerySessionControl, object> _scratchPersistHooked = new();

    /// <summary>
    /// Wires a query session's edits into the scratch content writer. Called from
    /// <see cref="CreateTab"/> — the one place every top-level session passes through —
    /// rather than at each construction site, the same sixteen-call-sites reasoning as the
    /// #495 tab watcher. Idempotent per session, because redock passes a session through
    /// CreateTab a second time.
    ///
    /// <para>The subscription is never taken back off: unlike the glyph handler it closes
    /// over no TabItem, only the session and this window, so it pins nothing a closed tab
    /// should release — and a DETACHED session must keep persisting (#496's sixth
    /// requirement), which is exactly the case an unhook-on-detach would break.</para>
    /// </summary>
    private void HookScratchPersistence(QuerySessionControl session)
    {
        /* Only sessions that are scratch NOW. SourceFilePath moves null→path exactly once
           (the save) and never back, so a session arriving here file-backed can never need
           this hook later — the scope fence again, applied at subscription time. It also
           keeps a file tab's DirtyStateChanged invocation list exactly the one subscription
           the #473 glyph-leak test counts by reflection; a second subscriber there would
           read as the leak that test exists to catch. A scratch that gains a file KEEPS its
           subscription (nothing unhooks it), which is why the fire-time guard below still
           exists: it is what makes the kept subscription inert from the save on. */
        if (session.SourceFilePath != null)
            return;

        if (_scratchPersistHooked.TryGetValue(session, out _))
            return;

        _scratchPersistHooked.Add(session, new object());

        /* DirtyStateChanged fires on every editor text change (#462's wiring), which is the
           signal wanted here. It also fires on MarkClean, but a scratch session is only ever
           marked clean by the save that just gave it a SourceFilePath, so the guard below
           already ignores that firing. */
        session.DirtyStateChanged += (_, _) =>
        {
            if (session.SourceFilePath == null)
                RequestScratchPersist(session);
        };

        /* A session can arrive at its first CreateTab already holding text nobody typed
           into it there — Edit Query hands a plan's statement to a fresh scratch session,
           and a restored scratch (#496) comes back with its buffer's content. Both set the
           text before the tab exists, so the subscription above never saw it; queue one
           persist now so "scratch content on screen" implies "scratch content on disk,
           one debounce later". (Scratch is already guaranteed by the top of this method.) */
        if (session.IsDirty)
            RequestScratchPersist(session);
    }

    /// <summary>
    /// Notes that a scratch session's content changed and schedules the debounced write.
    /// </summary>
    private void RequestScratchPersist(QuerySessionControl session)
    {
        /* Same shutdown rule as RequestSessionPersist: OnClosed drains this set itself and
           nothing may re-arm a timer against a window being torn down. */
        if (IsShuttingDown)
            return;

        _scratchPersistPending.Add(session);

        /* No real timer under the test host — read RequestSessionPersist's comment for the
           full #451/#495 story: the suite shares one dispatcher, so a timer armed here would
           tick during some LATER test and write THIS window's scratch buffers over whatever
           that test had staged. Tests drive the flush through
           FlushPendingScratchPersistForTests instead. */
        if (AppRuntimeMode.IsTestHost)
            return;

        if (_scratchPersistTimer == null)
        {
            _scratchPersistTimer = new DispatcherTimer { Interval = ScratchPersistDebounce };
            /* The membership flush is chained on so a buffer and its scratch:<guid> entry
               reach disk in the same breath: the first write for a session assigns its id
               and requests a membership persist, and without the chained flush that entry
               would trail the buffer by a debounce — a crash in that gap would strand a
               buffer the startup sweep then deletes as unreferenced. */
            _scratchPersistTimer.Tick += (_, _) =>
            {
                FlushScratchBuffers();
                FlushSessionPersist();
            };
        }

        // Stop-then-start restarts the interval, which is what makes it a debounce.
        _scratchPersistTimer.Stop();
        _scratchPersistTimer.Start();
    }

    /// <summary>
    /// Writes every pending scratch buffer down now. The timer's tick, every membership
    /// flush point (<see cref="FlushSessionPersist"/>, so end-of-restore and the #495
    /// debounce both drain this), <see cref="OnClosed"/>, and the test seam all land here;
    /// a flush with nothing pending is free.
    ///
    /// <para>Deliberately NOT gated on <see cref="IsShuttingDown"/> the way the membership
    /// flush is: OnClosed calls this while shutting down, precisely because the final drain
    /// is part of the final write — see the ordering comment there.</para>
    /// </summary>
    private void FlushScratchBuffers()
    {
        _scratchPersistTimer?.Stop();

        if (_scratchPersistPending.Count == 0)
            return;

        /* Snapshot-and-clear before writing: PersistScratchBuffer can call
           DropScratchBuffer, which edits this set. */
        var pending = _scratchPersistPending.ToList();
        _scratchPersistPending.Clear();

        foreach (var session in pending)
            PersistScratchBuffer(session);
    }

    /// <summary>
    /// Writes one session's buffer, or removes it, according to what the session holds now.
    /// </summary>
    private void PersistScratchBuffer(QuerySessionControl session)
    {
        /* The scope fence, enforced at the writer as well as the subscription: a session
           that gained a file between queueing and flushing (Save As raced the debounce) is
           file-backed now, and SaveQueryToPath already deleted its buffer. */
        if (session.SourceFilePath != null)
            return;

        /* A buffer exists to protect UNSAVED work, so a session with none sheds its buffer.
           For a scratch session clean means empty — its saved-text baseline is forever ""
           (#462) — so this is what erases the buffer of a tab whose text the user deleted
           back out, instead of resurrecting that text at the next start as if the deletion
           never happened. Clean close leans on this too: the only scratch tabs the
           #462/#469/#477 prompts do not ask about are the ones with nothing typed, and this
           branch is what guarantees those leave no buffer behind either. */
        if (!session.IsDirty)
        {
            DropScratchBuffer(session);
            return;
        }

        var text = session.QueryEditor.Text ?? string.Empty;

        /* Over the cap the buffer is not merely skipped but removed: a stale smaller
           snapshot restoring under megabytes of newer typing would misrepresent what the
           user had, which is worse than the honest pre-#496 nothing. */
        if (text.Length > MaxScratchPersistChars)
        {
            DropScratchBuffer(session);
            return;
        }

        var firstPersist = session.ScratchBufferId == null;
        if (firstPersist)
        {
            /* The id is minted at first persist, not at construction, so an empty tab never
               owns a buffer; a restored scratch arrives with its id already set and keeps
               writing the same buffer across restarts. */
            session.ScratchBufferId = Guid.NewGuid();
        }

        try
        {
            ScratchBufferStore.Write(session.ScratchBufferId!.Value, text);
        }
        catch
        {
            /* Best-effort, same stance as AppSettingsService.Save: persistence must never
               crash the editor it exists to protect. A failed write self-heals — either a
               later flush succeeds, or restore finds no readable buffer and skips the
               entry. */
        }

        /* A newly minted id is a membership change: the scratch:<guid> entry has to enter
           the #495 list, and the tab watcher cannot see it (no tab was added or removed —
           the same blind spot as SaveQueryToPath's path change, solved the same way). */
        if (firstPersist)
            RequestSessionPersist();
    }

    /// <summary>
    /// Deletes a session's scratch buffer and forgets its identity. The mechanics of every
    /// way a buffer dies; the chose-vs-never-got-to-choose reasoning lives at the call
    /// sites, because WHICH moments may call this is the entire design of #496:
    /// <list type="bullet">
    /// <item>Don't Save answered at any #462/#469/#477 prompt — they chose
    /// (<see cref="ResolveCloseChoiceAsync"/>).</item>
    /// <item>A save that succeeded — the content lives in a real file now
    /// (<see cref="SaveQueryToPath"/>).</item>
    /// <item>A scratch tab or window closed with nothing unsaved in it — nothing left to
    /// protect (<see cref="TryCloseTabAsync"/>, the detached close, and the clean branch of
    /// <see cref="PersistScratchBuffer"/>).</item>
    /// </list>
    /// Cancel appears nowhere in that list: a cancelled close changes nothing.
    /// </summary>
    private void DropScratchBuffer(QuerySessionControl session)
    {
        /* Whatever was queued for this session must not be written after the drop — that
           would resurrect the buffer the user just chose out of existence. */
        _scratchPersistPending.Remove(session);

        if (session.ScratchBufferId is not { } id)
            return;

        session.ScratchBufferId = null;
        ScratchBufferStore.TryDelete(id);

        /* The scratch:<guid> entry has to leave the #495 list with the buffer. Gated inside
           RequestSessionPersist during shutdown, where OnClosed's own final SaveOpenPlans —
           which runs after the final drain — writes the list without it. */
        RequestSessionPersist();
    }

    /// <summary>
    /// The deterministic stand-in for the content debounce's tick — the #496 twin of
    /// <see cref="FlushPendingSessionPersistForTests"/>, for the same shared-dispatcher
    /// reason (see <see cref="RequestScratchPersist"/>). Mirrors the real tick exactly,
    /// membership chain included, so a test observes the same disk state a patient user
    /// would.
    /// </summary>
    internal void FlushPendingScratchPersistForTests()
    {
        FlushScratchBuffers();
        FlushSessionPersist();
    }

    /// <summary>
    /// Recreates one scratch tab from its persisted buffer during restore. False when the
    /// buffer cannot come back, and the buffer file is deleted on that path — the #495
    /// poison invariant, mirrored: an entry that fails to load is skipped, never re-added
    /// (the session that would re-list it is never created), and its file is swept rather
    /// than left to fail again at every start.
    /// </summary>
    private bool TryRestoreScratchTab(Guid id)
    {
        string text;
        try
        {
            text = File.ReadAllText(ScratchBufferStore.BufferPathFor(id));
        }
        catch
        {
            ScratchBufferStore.TryDelete(id);
            return false;
        }

        /* An empty buffer should not exist — the writer deletes rather than writes empties —
           so finding one means debris; restoring an empty tab from it would be noise. */
        if (string.IsNullOrEmpty(text))
        {
            ScratchBufferStore.TryDelete(id);
            return false;
        }

        _queryCounter++;
        var session = new QuerySessionControl(_credentialService, _connectionStore);
        session.QueryEditor.Text = text;

        /* The SAME id, not a fresh one: this session continues the buffer it came from, so
           its next flush overwrites in place and the list entry stays stable across any
           number of restarts. */
        session.ScratchBufferId = id;

        /* Deliberately no MarkClean, unlike LoadSqlFile: this content is unsaved BY
           DEFINITION — nothing on disk that the user chose backs it — so the tab must come
           back dirty, marker and close-prompts and all. The session's empty saved-text
           baseline gives that for free. */

        var tab = CreateTab($"Query {_queryCounter}", session);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
        return true;
    }
}
