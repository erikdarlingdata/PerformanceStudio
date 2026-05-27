using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static readonly string OldFormatSettingsPath;

    static AppSettingsService()
    {
        SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceStudio");
        SettingsPath = Path.Combine(SettingsDir, "appsettings.json");
        OldFormatSettingsPath = Path.Combine(SettingsDir, "perfstudio_format_settings.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads settings from disk. Returns default settings if the file is missing or corrupt.
    /// Migrates legacy format settings from the old standalone file if present.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            AppSettings settings;
            if (!File.Exists(SettingsPath))
                settings = new AppSettings();
            else
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }

            // Migrate legacy format settings file into unified settings
            MigrateFormatSettings(settings);

            return settings;
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
            AtomicFile.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence — don't crash the app
        }
    }

    /// <summary>
    /// If the old perfstudio_format_settings.json exists and FormatOptions is not yet set,
    /// migrate the old settings into AppSettings and delete the old file.
    /// </summary>
    private static void MigrateFormatSettings(AppSettings settings)
    {
        try
        {
            if (settings.FormatOptions != null || !File.Exists(OldFormatSettingsPath))
                return;

            var json = File.ReadAllText(OldFormatSettingsPath);
            var legacy = JsonSerializer.Deserialize<SqlFormatSettings>(json, JsonOptions);
            if (legacy != null)
            {
                settings.FormatOptions = legacy;
                Save(settings);
                File.Delete(OldFormatSettingsPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppSettingsService: failed to migrate format settings: {ex.Message}");
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

    /// <summary>
    /// Default color palette for Multi QS Overview top databases.
    /// </summary>
    internal static readonly List<string> DefaultTopDbColors = new()
    {
        "#2EAEF1", "#F2994A", "#27AE60", "#9B51E0", "#EB5757",
        "#F2C94C", "#56CCF2", "#BB6BD9", "#E91E63", "#00BCD4",
    };
}

/// <summary>
/// Serializable settings model for the application.
/// </summary>
internal sealed class AppSettings
{
    // ── App State ────────────────────────────────────────────────────

    [JsonPropertyName("recent_plans")]
    public List<string> RecentPlans { get; set; } = new();

    [JsonPropertyName("open_plans")]
    public List<string> OpenPlans { get; set; } = new();

    [JsonPropertyName("accuracy_ratio_divergence_limit")]
    public double AccuracyRatioDivergenceLimit { get; set; } = 10;

    // ── Query Store Settings ─────────────────────────────────────────

    /// <summary>
    /// Number of days of Query Store data to load in the time-range slicer. Default 30.
    /// </summary>
    [JsonPropertyName("query_store_slicer_days")]
    public int QueryStoreSlicerDays { get; set; } = 30;

    /// <summary>
    /// Default metric for the top queries grid. Default "cpu" (= Total CPU).
    /// Values: cpu, avg-cpu, duration, avg-duration, reads, avg-reads,
    /// writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions.
    /// </summary>
    [JsonPropertyName("query_store_default_metric")]
    public string QueryStoreDefaultMetric { get; set; } = "cpu";

    /// <summary>
    /// Default number of top elements/groups shown in the grid. Default 25.
    /// </summary>
    [JsonPropertyName("query_store_top_limit")]
    public int QueryStoreTopLimit { get; set; } = 25;

    /// <summary>
    /// Default time range quick-filter selection (hours as string).
    /// Options: "3" (3h), "24" (24h), "48" (48h), "168" (7d), "720" (30d).
    /// </summary>
    [JsonPropertyName("query_store_default_time_range")]
    public string QueryStoreDefaultTimeRange { get; set; } = "24";

    /// <summary>
    /// Default time display mode: "Local", "Utc", or "Server".
    /// </summary>
    [JsonPropertyName("query_store_default_time_display")]
    public string QueryStoreDefaultTimeDisplay { get; set; } = "Local";

    /// <summary>
    /// Default group-by mode: "None", "QueryHash", or "Module".
    /// </summary>
    [JsonPropertyName("query_store_default_group_by")]
    public string QueryStoreDefaultGroupBy { get; set; } = "QueryHash";

    // ── Multi QS Overview Settings ───────────────────────────────────

    /// <summary>
    /// Number of top databases shown in the overview. Default 5, min 2, max 20.
    /// </summary>
    [JsonPropertyName("multi_qs_top_db_count")]
    public int MultiQsTopDbCount { get; set; } = 5;

    /// <summary>
    /// Hex color codes for top databases in the overview chart.
    /// </summary>
    [JsonPropertyName("multi_qs_top_db_colors")]
    public List<string> MultiQsTopDbColors { get; set; } = new(AppSettingsService.DefaultTopDbColors);

    // ── Query History Settings ───────────────────────────────────────

    /// <summary>
    /// Default metric for the query history chart. Default "AvgDurationMs".
    /// </summary>
    [JsonPropertyName("query_history_default_metric")]
    public string QueryHistoryDefaultMetric { get; set; } = "AvgDurationMs";

    /// <summary>
    /// Maximum number of plans fetched for a query history. Default 10, min 1, max 100.
    /// </summary>
    [JsonPropertyName("query_history_max_plans")]
    public int QueryHistoryMaxPlans { get; set; } = 10;

    // ── Script Options (Format) ──────────────────────────────────────

    /// <summary>
    /// SQL format options. Null means use <see cref="SqlFormatSettings"/> defaults.
    /// </summary>
    [JsonPropertyName("format_options")]
    public SqlFormatSettings? FormatOptions { get; set; }
}
