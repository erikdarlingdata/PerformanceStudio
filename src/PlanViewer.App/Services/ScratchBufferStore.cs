using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlanViewer.App.Services;

/// <summary>
/// The on-disk half of scratch buffer persistence (#496): one file per never-saved query tab
/// under <see cref="AppSettingsService.ScratchDirectory"/>, named by the tab's stable buffer
/// id, plus the <c>scratch:&lt;guid&gt;</c> entry format that puts those tabs on the same
/// ordered <c>open_tabs</c> list the file-backed tabs use (#495).
///
/// <para><b>Why the entry rides the existing list instead of a second one.</b> One list is
/// one ordering: scratch tabs interleave with file tabs on the strip, and two lists would
/// have to reinvent that interleaving (#495's detached entries already live with an
/// append-after compromise; docked tabs should not inherit it). Compatibility comes free and
/// is pinned the way #494's activation sentinel taught: an old build reading a new list
/// guards every entry with <c>File.Exists</c>, and <see cref="EntryPrefix"/> contains a
/// colon — not a legal character in a Windows file name, and the app only ever writes
/// absolute paths to the list, which an entry starting with <c>scratch:</c> is not on any
/// platform — so old builds skip these entries silently, exactly as they skip a file that
/// was deleted. A new build reading an old list sees only plain paths and behaves exactly
/// as before. No version field, no migration.</para>
///
/// <para><b>Privacy.</b> Scratch SQL can hold literals — names, ids, whatever the user was
/// querying for. These buffers land in the user's local profile beside the settings file,
/// which already stores the recent-plans list and, next to it, saved plan files whose XML
/// embeds full statement text and parameter values. Same machine, same user, same
/// sensitivity class as what is already there; no new exposure class is created.</para>
/// </summary>
internal static class ScratchBufferStore
{
    /// <summary>
    /// What marks an <c>open_tabs</c> entry as a scratch buffer rather than a file path.
    /// The colon is load-bearing — see the class comment — so the compat test pins this
    /// string directly rather than proving anything with a <c>File.Exists</c> that would
    /// be vacuous on a runner whose filesystem happily allows colons.
    /// </summary>
    internal const string EntryPrefix = "scratch:";

    /// <summary>
    /// .sql so a user digging through their profile can open a buffer and recognize it.
    /// </summary>
    private const string BufferExtension = ".sql";

    /// <summary>
    /// How long an unreferenced buffer survives the startup sweep. Three days spans a long
    /// weekend of not reopening Studio after a crash — see the age-gate comment in
    /// <see cref="SweepAllExcept"/>. Internal so the sweep tests can backdate past it
    /// instead of hardcoding a sibling value that drifts.
    /// </summary>
    internal static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromDays(3);

    /// <summary>The <c>open_tabs</c> entry for a scratch buffer.</summary>
    internal static string EntryFor(Guid id) => EntryPrefix + id.ToString("N");

    /// <summary>
    /// Whether an <c>open_tabs</c> entry names a scratch buffer. Anything that fails here —
    /// including a prefixed entry whose tail is not a GUID — is treated as a file path by
    /// the caller, which is also what makes a file literally named <c>scratch:something</c>
    /// on a colon-tolerant filesystem keep opening as the file it is.
    /// </summary>
    internal static bool TryParseEntry(string? entry, out Guid id)
    {
        id = default;
        return entry != null
            && entry.StartsWith(EntryPrefix, StringComparison.Ordinal)
            && Guid.TryParse(entry.AsSpan(EntryPrefix.Length), out id);
    }

    /// <summary>Where a buffer's content lives on disk.</summary>
    internal static string BufferPathFor(Guid id) =>
        Path.Combine(AppSettingsService.ScratchDirectory, id.ToString("N") + BufferExtension);

    /// <summary>
    /// Writes a buffer's content. Atomic for the same reason every other write in this app's
    /// profile is (#495): the buffer may be the only copy of the user's typing, and a crash
    /// mid-write must leave the previous content rather than a truncated file.
    /// </summary>
    internal static void Write(Guid id, string text)
    {
        Directory.CreateDirectory(AppSettingsService.ScratchDirectory);
        AtomicFile.WriteAllText(BufferPathFor(id), text);
    }

    /// <summary>
    /// Deletes a buffer's file, best-effort. Persistence in this app never throws at the
    /// user (<see cref="AppSettingsService.Save"/> sets that precedent); a buffer that
    /// cannot be deleted right now is unreferenced garbage the startup sweep collects later.
    /// </summary>
    internal static void TryDelete(Guid id)
    {
        try
        {
            File.Delete(BufferPathFor(id));
        }
        catch
        {
            // Best-effort — see doc comment.
        }
    }

    /// <summary>
    /// Deletes every file in the scratch directory that is not one of the referenced
    /// buffers. Run once at startup, after the saved tab list has been read (#496): a clean
    /// close deletes each buffer at the moment the user chooses its fate, so anything left
    /// unreferenced is debris — a buffer whose entry a crash-window skew lost, an
    /// <c>AtomicFile</c> <c>.tmp</c> stranded by a crash mid-write, a buffer a poisoned
    /// restore skipped. Matching on the exact expected file name (not the GUID stem) is
    /// what lets the sweep collect those <c>.tmp</c> siblings too.
    /// </summary>
    internal static void SweepAllExcept(IReadOnlyCollection<Guid> referenced)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(AppSettingsService.ScratchDirectory);
        }
        catch
        {
            // Most commonly: the directory does not exist because nothing has ever
            // persisted a scratch buffer. Nothing to sweep either way.
            return;
        }

        var keep = referenced
            .Select(id => id.ToString("N") + BufferExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /* Age gate (#496 review): an unreferenced buffer is USUALLY debris, but two crash
           shapes make a fresh one innocent — a buffer written moments before a crash in
           the buffer-then-list gap, and every bystander stranded when a mid-restore crash
           left the poison-cleared list empty. Only files past the grace period die, which
           turns "the sweep destroyed never-chosen content" into "an orphan lingered a few
           days as a recognizable .sql a person can still recover by hand" — the reason
           buffers carry that extension. The gate costs nothing on the paths that matter:
           chosen deletions (Don't Save, Save) delete directly and never come through here,
           and referenced buffers are never candidates at all. */
        var cutoff = DateTime.UtcNow - OrphanGracePeriod;

        foreach (var file in files)
        {
            if (keep.Contains(Path.GetFileName(file)))
                continue;

            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                File.Delete(file);
            }
            catch
            {
                // Locked or otherwise stuck — the next startup's sweep gets another turn.
            }
        }
    }
}
