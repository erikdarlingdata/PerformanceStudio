using PlanViewer.Cli.ReplSurface;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;
using Repl.Mcp;

namespace PlanViewer.Core.Tests;

public sealed class McpPlanPathPolicyTests
{
    [Theory]
    [InlineData(@"C:\plans\query.sqlplan", false)]
    [InlineData(@"C:\plans\host.txt:query.sqlplan", true)]
    [InlineData(@"\\server\share\query.sqlplan", false)]
    public void ContainsWindowsAlternateDataStream_DistinguishesTheDriveDesignator(
        string path,
        bool expected)
    {
        Assert.Equal(expected, McpPlanPathPolicy.ContainsWindowsAlternateDataStream(path));
    }

    [Fact]
    public async Task OpenAsync_DeniesAdvertisedEmptyRoots()
    {
        var roots = new StubClientRoots(isSupported: true, hasSoftRoots: false, []);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await McpPlanPathPolicy.OpenAsync(
                "probe.sqlplan",
                roots,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_AllowsAPlanUnderTheFilesystemRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"root-plan-{Guid.NewGuid():N}.sqlplan");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan"), path);
        try
        {
            var filesystemRoot = Path.GetPathRoot(path)!;
            var roots = new StubClientRoots(
                isSupported: true,
                hasSoftRoots: false,
                [new McpClientRoot(new Uri(filesystemRoot), "filesystem")]);

            await using var authorized = await McpPlanPathPolicy.OpenAsync(
                path,
                roots,
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFileName(path), authorized.Label);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_ValidatesAndReturnsTheSameOpenedHandle()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"mcp-handle-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "root");
        var slot = Path.Combine(root, "slot");
        var originalSlot = Path.Combine(root, "slot-original");
        var outside = Path.Combine(temporary, "outside");
        Directory.CreateDirectory(slot);
        Directory.CreateDirectory(outside);
        var fileName = "plan.sqlplan";
        var expected = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan"),
            TestContext.Current.CancellationToken);
        var replacement = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Plans", "top_above_scan_plan.sqlplan"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(slot, fileName),
            expected,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outside, fileName),
            replacement,
            TestContext.Current.CancellationToken);

        try
        {
            var roots = new StubClientRoots(
                isSupported: true,
                hasSoftRoots: false,
                [new McpClientRoot(new Uri(root + Path.DirectorySeparatorChar), "plans")]);
            await using var authorized = await McpPlanPathPolicy.OpenAsync(
                Path.Combine("slot", fileName),
                roots,
                TestContext.Current.CancellationToken);

            var swapped = false;
            try
            {
                Directory.Move(slot, originalSlot);
                Directory.CreateSymbolicLink(slot, outside);
                swapped = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Some platforms deny the rename or symbolic-link creation while the handle is open.
            }

            using var reader = new StreamReader(authorized.Stream, leaveOpen: true);
            var openedContent = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Equal(expected, openedContent);
            if (swapped)
            {
                Assert.Equal(
                    replacement,
                    await File.ReadAllTextAsync(
                        Path.Combine(slot, fileName),
                        TestContext.Current.CancellationToken));
            }

            authorized.Stream.Position = 0;
            var operations = new PlanOperations(new InMemoryPlanCatalog());
            var opened = await operations.OpenAsync(
                authorized.Stream,
                authorized.Label,
                TestContext.Current.CancellationToken);
            Assert.Equal(fileName, opened.Label);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private sealed class StubClientRoots(
        bool isSupported,
        bool hasSoftRoots,
        IReadOnlyList<McpClientRoot> roots) : IMcpClientRoots
    {
        public bool IsSupported { get; } = isSupported;
        public bool HasSoftRoots { get; } = hasSoftRoots;
        public IReadOnlyList<McpClientRoot> Current => roots;

        public ValueTask<IReadOnlyList<McpClientRoot>> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(roots);

        public void SetSoftRoots(IEnumerable<McpClientRoot> newRoots) =>
            throw new NotSupportedException();

        public void ClearSoftRoots() => throw new NotSupportedException();
    }
}
