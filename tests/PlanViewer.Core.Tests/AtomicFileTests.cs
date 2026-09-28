using System.IO;
using System.Text;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// AtomicFile stages its write through a sibling <c>.tmp</c> and renames it into place so a
/// crash mid-write can't truncate the target file. That guarantee had a gap: the <c>.tmp</c> was
/// written with File.WriteAllText and renamed without ever being flushed to disk, so the bytes
/// could still be sitting in the OS write-behind cache when the rename landed — a power loss
/// right after a "successful" save could still leave the renamed file empty. These pin that the
/// fix (a FileStream, explicit encoding-preamble handling, and Flush(flushToDisk: true) before
/// the rename) writes the exact same bytes File.WriteAllText always did, for a null encoding, an
/// explicit BOM-emitting one, and a legacy single-byte code page.
/// </summary>
public class AtomicFileTests
{
    private const string Contents = "SELECT 1; -- café";

    [Fact]
    public void NullEncodingMatchesFileWriteAllTextsDefaultUtf8NoBom()
    {
        var path = TempPath();
        var expectedPath = TempPath();
        try
        {
            AtomicFile.WriteAllText(path, Contents);
            File.WriteAllText(expectedPath, Contents);

            Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(path));
            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]); // no BOM
            Assert.False(File.Exists(path + ".tmp"), "the staging file must not linger");
        }
        finally
        {
            File.Delete(path);
            File.Delete(expectedPath);
        }
    }

    [Fact]
    public void ExplicitBomEncodingMatchesFileWriteAllTextsPreamble()
    {
        var path = TempPath();
        var expectedPath = TempPath();
        try
        {
            AtomicFile.WriteAllText(path, Contents, Encoding.UTF8); // the BOM-emitting singleton
            File.WriteAllText(expectedPath, Contents, Encoding.UTF8);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(File.ReadAllBytes(expectedPath), bytes);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(expectedPath);
        }
    }

    /// <summary>
    /// Latin-1 (ISO-8859-1 / code page 28591) rather than a Windows ANSI code page: it is a
    /// legacy single-byte encoding with no preamble, built into the runtime since .NET 5, and —
    /// unlike code page 1252 — needs no System.Text.Encoding.CodePages provider, so it behaves
    /// identically on the Ubuntu CI runner and here.
    /// </summary>
    [Fact]
    public void LegacyCodePageMatchesFileWriteAllTexts()
    {
        var path = TempPath();
        var expectedPath = TempPath();
        try
        {
            AtomicFile.WriteAllText(path, Contents, Encoding.Latin1);
            File.WriteAllText(expectedPath, Contents, Encoding.Latin1);

            Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(expectedPath);
        }
    }

    [Fact]
    public void OverwritingAnExistingFileStillRoundTrips()
    {
        var path = TempPath();
        try
        {
            AtomicFile.WriteAllText(path, "first");
            AtomicFile.WriteAllText(path, Contents);

            Assert.Equal(Contents, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.atomicfiletest");
}
