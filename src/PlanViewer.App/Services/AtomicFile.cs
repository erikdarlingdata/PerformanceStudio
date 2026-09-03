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

        if (encoding != null)
            File.WriteAllText(tmp, contents, encoding);
        else
            File.WriteAllText(tmp, contents);

        // File.Move with overwrite:true maps to MoveFileEx(MOVEFILE_REPLACE_EXISTING)
        // on Windows and rename(2) on Unix — both atomic when source and destination
        // live on the same filesystem, which is always the case here.
        File.Move(tmp, path, overwrite: true);
    }
}
