using PlanViewer.Cli.ReplSurface;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #441: the macOS resolver called fcntl(F_GETPATH) through a plain DllImport. fcntl(2) is variadic,
/// and on Apple arm64 variadic arguments are passed on the stack while a fixed-signature P/Invoke
/// puts them in registers — so the callee read a stack slot we never wrote.
///
/// It did not fail. fcntl returned 0 for success and wrote up to MAXPATHLEN bytes through whatever
/// pointer that slot happened to hold, an arbitrary ~1KB write on every call, while the buffer we
/// passed came back empty.
///
/// Nothing caught it, and that is the interesting part: the old test asserted on the returned
/// handle's Label and passed on Linux and Windows, which take entirely different branches. So this
/// asserts the one thing that was actually broken — that the resolver returns the real path of the
/// file that is genuinely open — and it asserts it on whatever platform the suite is running on.
/// </summary>
public class OpenedFilePathResolverTests
{
    [Fact]
    public void GetFinalPath_ReturnsTheRealPathOfTheOpenHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resolver-{Guid.NewGuid():N}.sqlplan");
        File.WriteAllText(path, "<ShowPlanXML />");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);

            var resolved = OpenedFilePathResolver.GetFinalPath(stream);

            /* Compared by identity rather than by string, because macOS answers with the canonical
               path — Path.GetTempPath() reports /var/folders/... while the kernel reports
               /private/var/folders/..., and /var is a symlink to /private/var. A string comparison
               here would fail for a reason that has nothing to do with the defect. */
            Assert.True(File.Exists(resolved), $"Resolver returned a path that does not exist: '{resolved}'");
            Assert.Equal(
                new FileInfo(path).Length,
                new FileInfo(resolved).Length);
            Assert.Equal(
                Path.GetFileName(path),
                Path.GetFileName(resolved));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The empty string is precisely what the broken call produced, and it is worth pinning that it
    /// can never be mistaken for a valid answer: an empty path would sail through the extension and
    /// containment checks in McpPlanPathPolicy as a Path.GetFullPath argument exception rather than
    /// as a denial.
    /// </summary>
    [Fact]
    public void GetFinalPath_NeverReturnsAnEmptyPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"resolver-{Guid.NewGuid():N}.sqlplan");
        File.WriteAllText(path, "<ShowPlanXML />");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            Assert.False(string.IsNullOrWhiteSpace(OpenedFilePathResolver.GetFinalPath(stream)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
