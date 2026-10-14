using System;
using System.IO;
using System.Text.Json;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static class ConfigLoader
{
    private const string ConfigFileName = ".planview.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads analyzer config from the first file found in this order:
    /// 1. Explicit path (--config flag)
    /// 2. .planview.json in current directory
    /// 3. ~/.planview.json in user home
    /// Returns AnalyzerConfig.Default if no file found.
    /// </summary>
    public static AnalyzerConfig Load(string? explicitPath = null)
    {
        string? configPath = null;

        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"Config file not found: {explicitPath}");
            configPath = explicitPath;
        }
        else
        {
            // Check current directory
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);
            if (File.Exists(cwdPath))
                configPath = cwdPath;
            else
            {
                // Check user home
                var homePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ConfigFileName);
                if (File.Exists(homePath))
                    configPath = homePath;
            }
        }

        if (configPath == null)
            return AnalyzerConfig.Default;

        var json = File.ReadAllText(configPath);
        AnalyzerConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AnalyzerConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Could not read {configPath}: {ex.Message}", ex);
        }

        if (config == null)
            return AnalyzerConfig.Default;

        ValidateSuppressions(config);
        return config;
    }

    /// <summary>
    /// A suppression that cannot match anything, or that hides a finding without saying why,
    /// is a mistake worth stopping for. Throws <see cref="InvalidDataException"/> naming the
    /// entry by its zero-based index (#651).
    /// </summary>
    private static void ValidateSuppressions(AnalyzerConfig config)
    {
        var entries = config.Suppressions;
        if (entries == null)
            return;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var prefix = $"Suppression {i} in {ConfigFileName}";

            if (entry == null)
                throw new InvalidDataException($"{prefix} is empty.");

            if (SuppressionEntry.NormalizeHash(entry.QueryHash).Length == 0)
                throw new InvalidDataException($"{prefix} has an empty \"query_hash\".");

            var hasType = !string.IsNullOrWhiteSpace(entry.Type);
            if (entry.Rule == null && !hasType)
                throw new InvalidDataException($"{prefix} needs \"rule\" or \"type\".");

            if (entry.Operator != null && string.IsNullOrWhiteSpace(entry.Operator))
                throw new InvalidDataException($"{prefix} has an empty \"operator\".");

            if (string.IsNullOrWhiteSpace(entry.Reason))
                throw new InvalidDataException($"{prefix} has no \"reason\".");
        }
    }
}
