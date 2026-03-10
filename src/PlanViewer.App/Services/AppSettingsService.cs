using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanViewer.App.Services;

/// <summary>
/// Persists recent plans and open session state to a JSON file in the app's local data directory.
/// </summary>
internal sealed class AppSettingsService
{
    private const int MaxRecentPlans = 10;
    private static readonly string SettingsDir;
    private static readonly string SettingsPath;

    static AppSettingsService()
    {
        SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceStudio");
        SettingsPath = Path.Combine(SettingsDir, "appsettings.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads settings from disk. Returns default settings if the file is missing or corrupt.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings to disk. Silently ignores write failures.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence — don't crash the app
        }
    }

    /// <summary>
    /// Adds a file path to the recent plans list (most recent first).
    /// Deduplicates by full path (case-insensitive on Windows).
    /// </summary>
    public static void AddRecentPlan(AppSettings settings, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);

        // Remove any existing entry for this path
        settings.RecentPlans.RemoveAll(p =>
            string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));

        // Insert at the front
        settings.RecentPlans.Insert(0, fullPath);

        // Trim to max size
        if (settings.RecentPlans.Count > MaxRecentPlans)
            settings.RecentPlans.RemoveRange(MaxRecentPlans, settings.RecentPlans.Count - MaxRecentPlans);
    }

    /// <summary>
    /// Removes a specific path from the recent plans list.
    /// </summary>
    public static void RemoveRecentPlan(AppSettings settings, string filePath)
    {
        settings.RecentPlans.RemoveAll(p =>
            string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Serializable settings model for the application.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>
    /// Most recently opened plan file paths, newest first. Max 10.
    /// </summary>
    [JsonPropertyName("recent_plans")]
    public List<string> RecentPlans { get; set; } = new();

    /// <summary>
    /// File paths that were open when the app last closed — restored on next launch.
    /// </summary>
    [JsonPropertyName("open_plans")]
    public List<string> OpenPlans { get; set; } = new();
}
