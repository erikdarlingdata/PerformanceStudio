using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PlanViewer.Core.Models;

public class AnalyzerConfig
{
    [JsonPropertyName("rules")]
    public RulesConfig? Rules { get; set; }

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

public class RulesConfig
{
    [JsonPropertyName("disabled")]
    public List<int> Disabled { get; set; } = new();

    [JsonPropertyName("severity_overrides")]
    public Dictionary<int, string> SeverityOverrides { get; set; } = new();
}
