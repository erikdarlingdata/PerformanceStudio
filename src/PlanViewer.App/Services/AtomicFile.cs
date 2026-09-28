using System.IO;
using System.Text;

namespace PlanViewer.App.Services;

/// <summary>
/// Helper for atomic text-file writes: write to a sibling .tmp and rename
/// into place so a crash mid-write can't truncate the target file. Callers
/// are responsible for creating the parent directory first.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Matches File.WriteAllText's own default encoding: UTF-8, and — unlike the
    /// <see cref="Encoding.UTF8"/> singleton — with an empty preamble, so passing no encoding
    /// below writes no BOM, exactly as it always has.
    /// </summary>
    private static readonly UTF8Encoding DefaultEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> atomically
    /// with respect to process crashes. If the process dies before the rename,
    /// <paramref name="path"/> keeps its previous contents and a stray
    /// <c>.tmp</c> sibling is left behind (cleaned up on the next call).
    /// </summary>
    /// <param name="encoding">
    /// Bytes to write the text as; null keeps File.WriteAllText's default, UTF-8
    /// without a BOM. Callers saving over a file the user opened pass the encoding
    /// that file arrived with, so a save-in-place is not also a silent transcode.
    /// </param>
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        var tmp = path + ".tmp";
        var actualEncoding = encoding ?? DefaultEncoding;

        // Written by hand rather than through File.WriteAllText/StreamWriter so the flush
        // below is reachable: crash-safety needs the .tmp's bytes durable on disk BEFORE the
        // rename, not just handed to the OS's write-behind cache, and neither of those helpers
        // exposes a way to ask for that. The preamble+bytes split is exactly what
        // File.WriteAllText(path, contents, encoding) does internally, so the bytes on disk
        // are identical either way — a null encoding writes none (DefaultEncoding's preamble
        // is empty), a given one writes its own, e.g. Encoding.Unicode's 2-byte UTF-16 LE mark.
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var preamble = actualEncoding.GetPreamble();
            if (preamble.Length > 0)
                stream.Write(preamble, 0, preamble.Length);

            var textBytes = actualEncoding.GetBytes(contents);
            stream.Write(textBytes, 0, textBytes.Length);

            // flushToDisk: true reaches past the OS write-behind cache (FlushFileBuffers on
            // Windows, fsync on Unix) — without it, a power loss right after a "successful"
            // rename could still leave the renamed file empty, because the rename can land on
            // disk before the data it points at does.
            stream.Flush(flushToDisk: true);
        }

        // File.Move with overwrite:true maps to MoveFileEx(MOVEFILE_REPLACE_EXISTING)
        // on Windows and rename(2) on Unix — both atomic when source and destination
        // live on the same filesystem, which is always the case here.
        File.Move(tmp, path, overwrite: true);
    }
}
