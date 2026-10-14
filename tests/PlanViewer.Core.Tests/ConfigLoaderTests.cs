using System;
using System.IO;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #651: the .planview.json loader. A suppression that cannot do its job, or that hides a finding
/// without saying why, stops the load with one line naming the entry by its zero-based index.
/// </summary>
public class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "planview-config-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, ".planview.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AValidSuppressionsListRoundTrips()
    {
        var config = ConfigLoader.Load(Write("""
            {
              // comments and trailing commas are allowed
              "rules": { "disabled": [3], "severity_overrides": { "10": "Info" } },
              "suppressions": [
                { "query_hash": "0x00E1987CD1C28B4C", "rule": 10, "operator": "eventuous.Messages", "reason": "Library owns the index." },
                { "query_hash": "00e1987cd1c28b4c", "type": "Implicit Conversion", "reason": "Vendor column type." },
              ]
            }
            """));

        Assert.True(config.IsRuleDisabled(3));
        Assert.Equal("Info", config.GetSeverityOverride(10));

        var suppressions = Assert.IsType<System.Collections.Generic.List<SuppressionEntry>>(config.Suppressions);
        Assert.Equal(2, suppressions.Count);
        Assert.Equal("0x00E1987CD1C28B4C", suppressions[0].QueryHash);
        Assert.Equal(10, suppressions[0].Rule);
        Assert.Equal("eventuous.Messages", suppressions[0].Operator);
        Assert.Equal("Library owns the index.", suppressions[0].Reason);
        Assert.Null(suppressions[0].Type);
        Assert.Equal("Implicit Conversion", suppressions[1].Type);
        Assert.Null(suppressions[1].Rule);
        Assert.Null(suppressions[1].Operator);
    }

    [Fact]
    public void AFileWithNoSuppressionsKeyStillLoads()
    {
        var config = ConfigLoader.Load(Write("""{ "rules": { "disabled": [5] } }"""));

        Assert.True(config.IsRuleDisabled(5));
        Assert.Null(config.Suppressions);
    }

    [Fact]
    public void AnEmptySuppressionsListLoads()
    {
        var config = ConfigLoader.Load(Write("""{ "suppressions": [] }"""));
        Assert.Empty(config.Suppressions!);
    }

    [Theory]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "rule": 3, "reason": "ok" }, { "query_hash": "0xAB", "rule": 4, "reason": "ok" }, { "query_hash": "0xAB", "rule": 5 } ] }""",
        "Suppression 2 in .planview.json has no \"reason\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "rule": 3, "reason": "  " } ] }""",
        "Suppression 0 in .planview.json has no \"reason\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "rule": 3, "reason": "ok" }, { "query_hash": "", "rule": 3, "reason": "ok" } ] }""",
        "Suppression 1 in .planview.json has an empty \"query_hash\".")]
    [InlineData("""{ "suppressions": [ { "rule": 3, "reason": "ok" } ] }""",
        "Suppression 0 in .planview.json has an empty \"query_hash\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0x", "rule": 3, "reason": "ok" } ] }""",
        "Suppression 0 in .planview.json has an empty \"query_hash\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "reason": "ok" } ] }""",
        "Suppression 0 in .planview.json needs \"rule\" or \"type\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "type": " ", "reason": "ok" } ] }""",
        "Suppression 0 in .planview.json needs \"rule\" or \"type\".")]
    [InlineData("""{ "suppressions": [ { "query_hash": "0xAB", "rule": 3, "operator": "", "reason": "ok" } ] }""",
        "Suppression 0 in .planview.json has an empty \"operator\".")]
    [InlineData("""{ "suppressions": [ null ] }""",
        "Suppression 0 in .planview.json is empty.")]
    public void AnInvalidSuppressionFailsWithOneLineNamingTheEntry(string json, string expected)
    {
        // The message names the file that was actually read, not the default file name.
        var path = Write(json);
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
        Assert.Equal(expected.Replace("in .planview.json", $"in {path}"), ex.Message);
    }

    [Fact]
    public void AnExplicitConfigNameAppearsInTheMessage()
    {
        var path = Path.Combine(_dir, "team.json");
        File.WriteAllText(path, """{ "suppressions": [ { "query_hash": "0xAB", "rule": 3 } ] }""");

        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));

        Assert.Equal($"Suppression 0 in {path} has no \"reason\".", ex.Message);
    }

    [Fact]
    public void MalformedJsonFailsWithAMessageNotAStackTrace()
    {
        var path = Write("""{ "suppressions": [ { "query_hash": """);
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.DoesNotContain(Environment.NewLine, ex.Message);
    }

    [Fact]
    public void AnUnreadableFileFailsWithAMessageNotAStackTrace()
    {
        var path = Write("""{ "rules": { "disabled": [] } }""");
        // Hold the file open with no sharing, so the loader's read is refused.
        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
        Assert.Contains(path, ex.Message);
        Assert.DoesNotContain(Environment.NewLine, ex.Message);
    }

    [Fact]
    public void ARuleThatIsNotANumberFailsAsAnInvalidFile()
    {
        Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(Write(
            """{ "suppressions": [ { "query_hash": "0xAB", "rule": "ten", "reason": "ok" } ] }""")));
    }

    [Fact]
    public void AMissingExplicitPathStillThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.Load(Path.Combine(_dir, "nope.json")));
    }
}
