using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PlanViewer.Core.Models;

public class AnalyzerConfig
{
    [JsonPropertyName("rules")]
    public RulesConfig? Rules { get; set; }

    /// <summary>
    /// Findings to mark as accepted (#651). Matched on the statement's query hash plus a rule
    /// number or warning type. A suppressed finding stays in the output, flagged, with its reason.
    /// </summary>
    [JsonPropertyName("suppressions")]
    public List<SuppressionEntry>? Suppressions { get; set; }

    public static AnalyzerConfig Default => new();

    public bool IsRuleDisabled(int ruleNumber)
    {
        return Rules?.Disabled?.Contains(ruleNumber) == true;
    }

    public string? GetSeverityOverride(int ruleNumber)
    {
        if (Rules?.SeverityOverrides != null &&
            Rules.SeverityOverrides.TryGetValue(ruleNumber, out var severity))
            return severity;
        return null;
    }
}

public class SuppressionEntry
{
    [JsonPropertyName("query_hash")]
    public string? QueryHash { get; set; }

    /// <summary>Matches <see cref="PlanWarning.RuleNumber"/>, so it never matches an engine warning.</summary>
    [JsonPropertyName("rule")]
    public int? Rule { get; set; }

    /// <summary>Matches <see cref="PlanWarning.WarningType"/> exactly, ignoring case.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Optional. Substring of the operator label the JSON output prints, ignoring case.</summary>
    [JsonPropertyName("operator")]
    public string? Operator { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Trims and drops a leading 0x or 0X so both sides of a hash comparison look alike.</summary>
    public static string NormalizeHash(string? hash)
    {
        var h = (hash ?? "").Trim();
        if (h.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
            h = h.Substring(2);
        return h;
    }
}

public class RulesConfig
{
    [JsonPropertyName("disabled")]
    public List<int> Disabled { get; set; } = new();

    [JsonPropertyName("severity_overrides")]
    public Dictionary<int, string> SeverityOverrides { get; set; } = new();
}
