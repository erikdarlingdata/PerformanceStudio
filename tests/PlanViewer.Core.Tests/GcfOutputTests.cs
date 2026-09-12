using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BlackwellSystems.Gcf;
using PlanViewer.App.Mcp;
using Xunit;

namespace PlanViewer.Core.Tests;

// Mutates the PLANVIEWER_OUTPUT_FORMAT environment variable; the collection keeps these
// tests from racing each other (and any other env-mutating test) in parallel.
[Collection("PlanViewerOutputFormatEnv")]
public class GcfOutputTests : IDisposable
{
    public GcfOutputTests() => Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", null);

    public void Dispose() => Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", null);

    // A representative get_query_store_top result: the {server, ..., plans:[...]} envelope
    // wrapping an array of uniform per-plan records (the shape McpQueryStoreTools returns).
    // Doubles are exact binary fractions so the payload round-trips deterministically; the
    // precision-edge behavior is covered by the Numbered* tests below.
    private static string QueryStorePlans(int rows)
    {
        var plans = Enumerable
            .Range(0, rows)
            .Select(i => new
            {
                session_id = $"{(1000000 + i):x}-4a2b-4c3d-9e00-abcdef012345",
                query_id = (long)(40000 + i),
                plan_id = (long)(90000 + i * 3),
                query_hash = "0x8A3F00C1D2E4" + i.ToString("X4"),
                query_plan_hash = "0x9B4E11D2E3F5" + i.ToString("X4"),
                module_name = i % 4 == 0 ? $"dbo.usp_Proc{i % 9}" : null,
                label = $"QS:AppDb Q{40000 + i} P{90000 + i * 3}",
                query_text = "SELECT c.customer_id, c.name FROM dbo.Customers c WHERE c.active = 1 ORDER BY c.name",
                executions = (long)((i * 137 + 5) % 100000),
                total_cpu_ms = i * 100.5,
                avg_cpu_ms = i * 0.5 + 1.0,
                total_duration_ms = i * 250.25,
                avg_duration_ms = i * 0.25 + 2.0,
                total_logical_reads = (long)((i * 9931) % 5000000),
                avg_logical_reads = (long)((i * 13) % 40000),
                warning_count = i % 6,
                missing_index_count = i % 3,
                last_executed_utc = "2026-09-04 12:00:00",
                loaded = true,
                load_error = (string?)null,
            })
            .ToList();

        return JsonSerializer.Serialize(
            new { server = "SQLPROD01", database = "AppDb", order_by = "cpu", hours_back = 24, plan_count = rows, plans },
            new JsonSerializerOptions { WriteIndented = false } // matches the server's compact MCP output
        );
    }

    [Fact]
    public void Enabled_Reflects_Environment()
    {
        Assert.False(GcfOutput.Enabled);

        foreach (var value in new[] { "gcf", "GCF", " gcf " })
        {
            Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", value);
            Assert.True(GcfOutput.Enabled);
        }

        Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", "json");
        Assert.False(GcfOutput.Enabled);
    }

    [Fact]
    public void TryEncode_RecordArray_Is_Smaller_And_RoundTrips()
    {
        var json = QueryStorePlans(30);

        var wire = GcfOutput.TryEncode(json);

        Assert.NotNull(wire);
        Assert.StartsWith("GCF profile=generic", wire);
        Assert.True(wire!.Length < json.Length, "GCF wire must be smaller than the JSON");
        // Decoding then re-encoding reproduces the wire (stable, lossless round-trip).
        Assert.Equal(wire, Gcf.EncodeGeneric(Gcf.DecodeGeneric(wire)));
    }

    [Fact]
    public void TryEncode_Decoded_Wire_Carries_Input_Values()
    {
        // The claim is that the substituted wire carries the SAME value as the JSON, not
        // merely that it re-encodes to itself. Decode the wire and assert it reproduces the
        // input's values, checked against the literals the payload was built from.
        var wire = GcfOutput.TryEncode(QueryStorePlans(30));
        Assert.NotNull(wire);

        var root = Assert.IsType<OrderedMap>(Gcf.DecodeGeneric(wire!));
        Assert.Equal("SQLPROD01", (string?)root["server"]);
        Assert.Equal("AppDb", (string?)root["database"]);
        Assert.Equal(24L, (long)root["hours_back"]!);
        Assert.Equal(30L, (long)root["plan_count"]!);

        var plans = Assert.IsType<List<object?>>(root["plans"]);
        Assert.Equal(30, plans.Count);

        var first = Assert.IsType<OrderedMap>(plans[0]);
        Assert.Equal(40000L, (long)first["query_id"]!);
        Assert.Equal(90000L, (long)first["plan_id"]!);
        Assert.Equal(5L, (long)first["executions"]!);
        Assert.True((bool)first["loaded"]!);

        var last = Assert.IsType<OrderedMap>(plans[29]);
        Assert.Equal(40029L, (long)last["query_id"]!); // 40000 + 29
        Assert.Equal(90087L, (long)last["plan_id"]!);  // 90000 + 29 * 3
    }

    [Fact]
    public void TryEncode_Tiny_Payload_Falls_Back_To_Json()
    {
        var json = JsonSerializer.Serialize(new { status = "ok" });
        Assert.Null(GcfOutput.TryEncode(json)); // GCF not smaller: keep JSON
    }

    [Fact]
    public void TryEncode_Invalid_Json_Falls_Back()
    {
        Assert.Null(GcfOutput.TryEncode("{not json"));
    }

    private static string Numbered(object value, int rows)
    {
        var arr = Enumerable
            .Range(0, rows)
            .Select(_ => new Dictionary<string, object> { ["metric"] = value, ["server"] = "SQLPROD01" })
            .ToList();
        return JsonSerializer.Serialize(
            new { rows = arr },
            new JsonSerializerOptions { WriteIndented = false } // matches the server's compact MCP output
        );
    }

    // Builds the same {rows:[{metric,server}]} shape but writes the metric as a raw JSON
    // number token, so tokens that cannot be produced from a CLR value (an overflowing
    // literal such as 1e400) can be fed through the encoder exactly as a collector would.
    private static string NumberedRaw(string numberToken, int rows)
    {
        var items = string.Join(
            ",",
            Enumerable
                .Range(0, rows)
                .Select(_ => $"{{\"metric\":{numberToken},\"server\":\"SQLPROD01\"}}")
        );
        return $"{{\"rows\":[{items}]}}";
    }

    [Fact]
    public void TryEncode_Keeps_Decimal_That_Fits_Double()
    {
        // 33.5 is exactly representable as a double, so it round-trips and GCF is kept.
        var wire = GcfOutput.TryEncode(Numbered(33.5, 20));

        Assert.NotNull(wire);
        Assert.Contains("33.5", wire);
    }

    [Fact]
    public void TryEncode_Keeps_16Digit_ShortestRoundTrip_Double()
    {
        // 0.5029000043869019 is a real captured float8 value: a 16-significant-digit
        // shortest-round-trip double that IS exactly representable. It must encode. A
        // (decimal)d guard keeps only 15 digits and would wrongly decline it.
        var wire = GcfOutput.TryEncode(Numbered(0.5029000043869019, 20));

        Assert.NotNull(wire);
        Assert.Contains("0.5029000043869019", wire);
    }

    [Fact]
    public void TryEncode_Declines_NonFinite_Double()
    {
        // A token that overflows double (1e400) parses to Infinity. The guard must decline
        // it rather than silently encode Infinity where the JSON carried a finite token.
        Assert.Null(GcfOutput.TryEncode(NumberedRaw("1e400", 20)));
    }

    [Fact]
    public void TryEncode_Declines_High_Precision_Decimal()
    {
        // 33.333333333333333 (17 significant digits) cannot be held by a double without
        // loss. A same-shape array of integers this size encodes to GCF, so a null here is
        // the precision guard declining rather than never-grow: the result stays JSON
        // instead of a silently rounded wire.
        Assert.Null(GcfOutput.TryEncode(Numbered(33.333333333333333m, 20)));
    }

    [Fact]
    public void TryEncode_Declines_UInt64_Above_Int64()
    {
        // ulong.MaxValue exceeds Int64 and is not exactly a double either; keep JSON.
        var json = Numbered(18446744073709551615UL, 20);
        Assert.Null(GcfOutput.TryEncode(json));
    }

    [Fact]
    public void TryEncode_Preserves_Int64_Above_2Pow53()
    {
        // A default JSON-to-double parse would round 9007199254740993 to ...992; the
        // encoder must keep the exact integer, not render it as a float.
        var rows = Enumerable
            .Range(0, 20)
            .Select(_ => new { id = 9007199254740993L, name = "x" })
            .ToList();
        var json = JsonSerializer.Serialize(
            new { rows },
            new JsonSerializerOptions { WriteIndented = false } // matches the server's compact MCP output
        );

        var wire = GcfOutput.TryEncode(json);

        Assert.NotNull(wire);
        Assert.Contains("9007199254740993", wire);
        Assert.DoesNotContain("9.007", wire); // not a rounded float
    }
}
