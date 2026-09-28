using System;
using System.Diagnostics;
using System.IO;

namespace PlanViewer.App.Services;

/// <summary>
/// Shared corrupt/unreadable-file policy for <see cref="AppSettingsService"/>,
/// <see cref="ConnectionStore"/> and <see cref="SettingsFile"/>. Before this, all three treated
/// "exists but can't be parsed" and "exists but can't be read" the same as "does not exist":
/// defaults came back, and the very next save silently overwrote the user's file with those
/// defaults — for a read failure that might be transient (locked by another process, a network
/// share blip, antivirus), or a parse failure that lost content the user would want back.
///
/// <para>The two failure modes now diverge. A file that fails to PARSE is quarantined: moved
/// aside to a <c>.bad-&lt;UTC timestamp&gt;</c> sibling so the original bytes are never lost, and
/// the caller starts fresh from defaults exactly as it did before — a save right after is exactly
/// as safe as it always looked. A file that fails to READ is left exactly where it is: the caller
/// gets defaults back for this one read, but must refuse to save over the file until a later read
/// of the same path succeeds, because a locked-out reader can't tell whether what's on disk still
/// deserves to survive.</para>
/// </summary>
internal static class SettingsFileStore
{
    internal enum ReadOutcome
    {
        /// <summary>Parsed successfully.</summary>
        Loaded,

        /// <summary>No file at this path. Defaults; saves are allowed exactly as before.</summary>
        Missing,

        /// <summary>
        /// The file was read but did not parse — or parsed to the wrong shape (valid JSON, wrong
        /// structure; see <see cref="SettingsFile.Read"/>'s array-vs-object case). Moved aside to
        /// a <c>.bad-&lt;timestamp&gt;</c> sibling. Defaults; saves are allowed.
        /// </summary>
        Malformed,

        /// <summary>
        /// The file exists but could not even be read (locked, permissions) — or a Malformed
        /// file's quarantine move itself failed. Defaults; the caller must refuse to save over
        /// this path until a later read of it succeeds.
        /// </summary>
        Unreadable,
    }

    /// <summary>
    /// Reads <paramref name="path"/> and hands its text to <paramref name="parse"/>, applying the
    /// policy above. <paramref name="parse"/> should return null (or throw) for content that
    /// doesn't parse into a usable <typeparamref name="T"/> — a JSON syntax error and a
    /// structurally-valid-but-wrong-shape document (an array where an object was expected, a bare
    /// JSON <c>null</c>) are handled identically: neither is data worth keeping in place of the
    /// quarantine copy.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="logSource">Prefix for the Debug.WriteLine trace — the calling store's name.</param>
    /// <param name="parse">Parses the file's text into <typeparamref name="T"/>, or returns null/throws on bad content.</param>
    /// <param name="value">The parsed value on <see cref="ReadOutcome.Loaded"/>; null otherwise.</param>
    internal static ReadOutcome Read<T>(string path, string logSource, Func<string, T?> parse, out T? value)
        where T : class
    {
        if (!File.Exists(path))
        {
            value = null;
            return ReadOutcome.Missing;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"{logSource}: failed to read {path}: {ex.Message}");
            value = null;
            return ReadOutcome.Unreadable;
        }

        try
        {
            var parsed = parse(json);
            if (parsed != null)
            {
                value = parsed;
                return ReadOutcome.Loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{logSource}: failed to parse {path}: {ex.Message}");
        }

        value = null;
        return Quarantine(path, logSource) ? ReadOutcome.Malformed : ReadOutcome.Unreadable;
    }

    /// <summary>
    /// Moves an unparseable file aside so the next save can start clean without losing the
    /// original bytes. Returns false (and leaves the file exactly where it was) if the move
    /// itself fails — the caller then treats this the same as <see cref="ReadOutcome.Unreadable"/>.
    ///
    /// <para>Two readers can find the same broken file at once (the UI thread and the MCP
    /// server's). The name carries milliseconds and a counter, so they never collide on it, and
    /// a reader that finds the file already gone counts it as moved aside: the other reader
    /// moved it, and saving over the path is just as safe for both.</para>
    /// </summary>
    internal static bool Quarantine(string path, string logSource)
    {
        try
        {
            var stamped = $"{path}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var quarantined = stamped;
            for (var n = 1; File.Exists(quarantined); n++)
                quarantined = $"{stamped}-{n}";

            File.Move(path, quarantined, overwrite: false);
            Debug.WriteLine($"{logSource}: moved unreadable {path} to {quarantined}");
            return true;
        }
        catch (Exception ex) when (!File.Exists(path))
        {
            Debug.WriteLine($"{logSource}: {path} was already moved aside: {ex.Message}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{logSource}: could not quarantine {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// An IOException naming <paramref name="path"/>, for the stores whose save methods must
    /// refuse to write instead of silently skipping (ConnectionStore.Save, SettingsFile.Update) —
    /// their callers need to hear why the save did not happen rather than have it swallowed.
    /// </summary>
    internal static IOException UnreadableSaveRefused(string path) => new(
        $"Not saving {path}: it could not be read on the last load, so its on-disk content might not match what is about to be written. Try again once the file is readable.");
}
