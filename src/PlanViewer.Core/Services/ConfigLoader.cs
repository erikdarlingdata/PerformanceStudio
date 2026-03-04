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
        var config = JsonSerializer.Deserialize<AnalyzerConfig>(json, JsonOptions);
        return config ?? AnalyzerConfig.Default;
    }
}
